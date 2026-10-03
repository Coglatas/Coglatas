import { spawn } from 'node:child_process';
import { buildFci04OwnerPlan } from '../functional/fixtures/fci04-owner-plan.mjs';
import { fileURLToPath } from 'node:url';
import { prepareRealBackendP0State } from './prepare-real-backend-p0-state.mjs';
import {
  buildRealBackendPlaywrightPlan,
  isHstsPreloadedHttpUrl,
  isStaticAngularServerUrl
} from './real-backend-smoke-compose-helpers.mjs';

const focusedGrep = process.env.COGLATAS_REAL_BACKEND_SMOKE_GREP?.trim(),
  ownerPlan = buildFci04OwnerPlan(process.env.COGLATAS_FCI04_GATES, process.env.COGLATAS_FCI04_ONLY === '1'),
  playwrightCli = fileURLToPath(new URL('../../node_modules/@playwright/test/cli.js', import.meta.url)),
  playwrightPlan = [...ownerPlan],
  successExitCode = 0;

if (process.env.COGLATAS_FCI04_ONLY !== '1') {
  playwrightPlan.push(...buildRealBackendPlaywrightPlan(process.argv.slice(2), focusedGrep));
}

if (process.env.COGLATAS_ISSUE_683_EVIDENCE === '1') {
  for (const run of playwrightPlan.filter((entry) => entry.name !== 'Functional real-backend owners')) {
    run.args.push('--add-reporter=./tests/ui/issue-683-race-evidence-reporter.mjs');
  }
}

let exitCode = 1;

try {
  const configuration = validateConfiguration(process.env);
  await waitForReady(configuration.baseURL);
  if (process.env.COGLATAS_REAL_BACKEND_P0_SETUP === '1') {
    await prepareRealBackendP0State(configuration);
  }
  exitCode = await playwrightPlan.reduce(async (previousCodePromise, run) => {
    const previousCode = await previousCodePromise;
    if (previousCode !== successExitCode) {
      return previousCode;
    }
    console.log(`Running ${run.name}.`);
    return runPlaywright(configuration.baseURL, run.args, run.environment);
  }, Promise.resolve(successExitCode));
} catch (error) {
  console.error(error instanceof Error ? error.message : error);
}

process.exitCode = exitCode;

function validateConfiguration(environment) {
  const baseURL = environment.PLAYWRIGHT_BASE_URL?.trim();
  const email = environment.COGLATAS_BROWSER_SMOKE_EMAIL?.trim();
  const password = environment.COGLATAS_BROWSER_SMOKE_PASSWORD;

  if (environment.COGLATAS_REAL_BACKEND_SMOKE !== '1') {
    throw new Error('COGLATAS_REAL_BACKEND_SMOKE=1 is required. Use `npm run test:ui:real-backend` for the self-contained real-backend smoke.');
  }

  if (!baseURL) {
    throw new Error('PLAYWRIGHT_BASE_URL is required for the real-backend smoke. The Compose runner sets it to http://coglatas-backend:8080.');
  }

  try {
    new URL(baseURL);
  } catch {
    throw new Error('PLAYWRIGHT_BASE_URL must be an absolute HTTP(S) URL for the real-backend smoke.');
  }

  if (isStaticAngularServerUrl(baseURL)) {
    throw new Error('PLAYWRIGHT_BASE_URL points to the static Angular server on port 4173. Use `npm run test:ui:real-backend` instead of the static runner.');
  }

  if (isHstsPreloadedHttpUrl(baseURL)) {
    throw new Error('PLAYWRIGHT_BASE_URL uses an HTTP .app hostname that Chromium upgrades to HTTPS through HSTS. Use the Compose alias http://coglatas-backend:8080.');
  }

  if (!email) {
    throw new Error('COGLATAS_BROWSER_SMOKE_EMAIL is required for the real-backend smoke seed.');
  }

  if (!email.toLowerCase().endsWith('@example.test')) {
    throw new Error('COGLATAS_BROWSER_SMOKE_EMAIL must use synthetic @example.test data for the real-backend smoke.');
  }

  if (!password) {
    throw new Error('COGLATAS_BROWSER_SMOKE_PASSWORD is required for the real-backend smoke seed.');
  }

  return { baseURL, email, password };
}

function runPlaywright(baseURL, playwrightArgs, additionalEnvironment = {}) {
  return new Promise((resolve) => {
    let settled = false;
    const finish = (code) => {
      if (!settled) {
        settled = true;
        resolve(Number.isInteger(code) && code >= 0 ? code : 1);
      }
    };

    const child = spawn(process.execPath, [playwrightCli, 'test', ...playwrightArgs], {
      cwd: process.cwd(),
      env: { ...process.env, PLAYWRIGHT_BASE_URL: baseURL, ...additionalEnvironment },
      stdio: 'inherit'
    });

    child.once('error', (error) => {
      console.error(`Unable to start Playwright: ${error.message}`);
      finish(1);
    });
    child.once('close', finish);
  });
}

async function waitForReady(baseURL) {
  const readinessUrl = new URL('/health/ready', baseURL).toString();
  const deadline = Date.now() + 180_000;
  let lastStatus = '';
  let connectionRefused = false;
  let lastError = '';

  while (Date.now() < deadline) {
    try {
      const response = await fetch(readinessUrl);
      lastStatus = `${response.status} ${response.statusText}`;
      if (response.ok) {
        return;
      }
    } catch (error) {
      const cause = error instanceof Error && 'cause' in error ? error.cause : undefined;
      const code = cause && typeof cause === 'object' && 'code' in cause ? cause.code : undefined;
      connectionRefused ||= code === 'ECONNREFUSED';
      lastError = error instanceof Error ? error.message : String(error);
    }

    await new Promise((resolve) => setTimeout(resolve, 500));
  }

  if (connectionRefused) {
    throw new Error(`Connection refused while waiting for ${readinessUrl}. The canonical command starts the backend automatically: npm run test:ui:real-backend.`);
  }

  if (lastStatus.startsWith('503')) {
    throw new Error(`Real backend readiness at ${readinessUrl} remained 503. Inspect the app and migrate container logs for database, migration, or seed failures.`);
  }

  throw new Error(`Timed out waiting for real backend readiness at ${readinessUrl}.${lastStatus ? ` Last status: ${lastStatus}` : ''}${lastError ? ` Last error: ${lastError}` : ''}`);
}
