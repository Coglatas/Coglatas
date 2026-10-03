import { spawn } from 'node:child_process';
import {
  getComposeProjectName,
  normalizeExitCode,
  redactSecrets,
  selectComposeInvocation
} from './real-backend-smoke-compose-helpers.mjs';

const composeFile = 'infra/compose/test/real-backend-smoke.yml';
const projectName = getComposeProjectName(process.env, process.pid);
const composeEnv = { ...process.env, COMPOSE_PROJECT_NAME: projectName };
const reusePrebuiltApp = composeEnv.COGLATAS_REUSE_PREBUILT_APP_IMAGE === '1';

let composeInvocation;
let cleanupPromise;
let signalReceived = false;

process.once('SIGINT', () => void handleSignal('SIGINT', 130));
process.once('SIGTERM', () => void handleSignal('SIGTERM', 143));

process.exitCode = await main();

async function main() {
  let exitCode = 1;

  try {
    composeInvocation = await selectComposeInvocation(isCommandAvailable);
    console.log(`Using ${composeInvocation.command} ${composeInvocation.prefix.join(' ')} with isolated project ${projectName}.`);

    exitCode = (await runCompose(['-p', projectName, '-f', composeFile, 'config', '--quiet'], { capture: true, redact: true })).exitCode;
    if (exitCode === 0) {
      const upArgs = ['-p', projectName, '-f', composeFile, 'up', '--detach'];
      upArgs.push(reusePrebuiltApp ? '--no-build' : '--build', 'postgres', 'app');
      exitCode = (await runCompose(upArgs)).exitCode;
    }

    if (exitCode === 0) {
      await waitForHealthy('postgres');
      await waitForMigration();
      await waitForHealthy('app');

      exitCode = (await runCompose(realBackendPlaywrightRunArgs())).exitCode;
    }
  } catch (error) {
    console.error(error instanceof Error ? error.message : error);
    exitCode = 1;
  } finally {
    if (exitCode !== 0) {
      await collectFailureEvidence();
    }

    await cleanupOnce();
  }

  return exitCode;
}

function realBackendPlaywrightRunArgs() {
  const args = ['-p', projectName, '-f', composeFile, 'run'];
  if (composeEnv.COGLATAS_REAL_BACKEND_P0_SETUP === '1') {
    args.push('--env', 'COGLATAS_REAL_BACKEND_P0_SETUP=1');
  }
  if (composeEnv.COGLATAS_FUNCTIONAL_FILES_GATE) {
    if (!['functional-fast', 'functional-full'].includes(composeEnv.COGLATAS_FUNCTIONAL_FILES_GATE)) {
      throw new Error('Invalid FCI-05 Files owner gate.');
    }
    args.push('--env', `COGLATAS_FUNCTIONAL_FILES_GATE=${composeEnv.COGLATAS_FUNCTIONAL_FILES_GATE}`);
  }
  args.push('real-backend-playwright');
  return args;
}

async function isCommandAvailable(command, args) {
  const result = await runCommand(command, args, { allowFailure: true, capture: true, silent: true });
  return result.exitCode === 0;
}

async function waitForHealthy(service) {
  const deadline = Date.now() + 240_000;
  let lastState = 'not created';

  while (Date.now() < deadline) {
    const containerId = await getServiceContainerId(service);
    if (containerId) {
      const state = await inspectContainerState(containerId);
      lastState = state || lastState;
      const [status, health, exitCodeText] = lastState.split(/\s+/);

      if (status === 'running' && health === 'healthy') {
        return;
      }

      if (status === 'exited' || status === 'dead') {
        throw new Error(`${service} container stopped before becoming healthy: ${lastState}`);
      }

      const containerExitCode = Number(exitCodeText);
      if (Number.isFinite(containerExitCode) && containerExitCode !== 0) {
        throw new Error(`${service} container failed before becoming healthy: ${lastState}`);
      }
    }

    await delay(1_000);
  }

  throw new Error(`Timed out waiting for ${service} to become healthy. Last state: ${lastState}`);
}

async function waitForMigration() {
  const deadline = Date.now() + 240_000;
  let lastState = 'not created';

  while (Date.now() < deadline) {
    const containerId = await getServiceContainerId('migrate');
    if (containerId) {
      const state = await inspectContainerState(containerId);
      lastState = state || lastState;
      const [status, , exitCodeText] = lastState.split(/\s+/);
      const migrationExitCode = Number(exitCodeText);

      if (status === 'exited' && migrationExitCode === 0) {
        return;
      }

      if ((status === 'exited' || status === 'dead') && Number.isFinite(migrationExitCode)) {
        throw new Error(`migrate container failed before the app could start: ${lastState}`);
      }
    }

    await delay(1_000);
  }

  throw new Error(`Timed out waiting for migrate to complete successfully. Last state: ${lastState}`);
}

async function getServiceContainerId(service) {
  const result = await runCompose(['-p', projectName, '-f', composeFile, 'ps', '--all', '-q', service], {
    allowFailure: true,
    capture: true,
    silent: true
  });
  return result.output.trim().split(/\s+/)[0] || '';
}

async function inspectContainerState(containerId) {
  const result = await runDocker(
    [
      'inspect',
      '--format',
      '{{.State.Status}} {{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}} {{.State.ExitCode}}',
      containerId
    ],
    { allowFailure: true, capture: true, silent: true }
  );
  return result.output.trim();
}

function runDocker(args, options = {}) {
  return runCommand('docker', args, options);
}

function runCompose(args, options = {}) {
  if (!composeInvocation) {
    return Promise.resolve({ exitCode: 1, output: 'Docker Compose command was not selected.' });
  }

  return runCommand(composeInvocation.command, [...composeInvocation.prefix, ...args], options);
}

function runCommand(command, args, options = {}) {
  return new Promise((resolve) => {
    let settled = false;
    let stdout = '';
    let stderr = '';
    let spawnError;
    const child = spawn(command, args, {
      cwd: process.cwd(),
      env: composeEnv,
      stdio: options.capture ? ['ignore', 'pipe', 'pipe'] : 'inherit'
    });

    const finish = (code) => {
      if (settled) {
        return;
      }

      settled = true;
      const output = `${stdout}${stderr}${spawnError ? `${spawnError.message}\n` : ''}`;
      if (options.capture && output.length > 0 && !options.silent) {
        process.stdout.write(options.redact ? redactSecrets(output) : output);
      }

      resolve({ exitCode: normalizeExitCode(code), output });
    };

    if (options.capture) {
      child.stdout?.on('data', (chunk) => {
        stdout += chunk;
      });
      child.stderr?.on('data', (chunk) => {
        stderr += chunk;
      });
    }

    child.once('error', (error) => {
      spawnError = error;
      finish(127);
    });
    child.once('close', (code) => finish(code));
  });
}

async function collectFailureEvidence() {
  if (!composeInvocation) {
    return;
  }

  console.error('Collecting real-backend smoke failure evidence (secrets redacted).');
  await runCompose(['-p', projectName, '-f', composeFile, 'ps', '--all'], { allowFailure: true, capture: true, redact: true });
  for (const service of ['postgres', 'migrate', 'app', 'real-backend-playwright']) {
    await runCompose(['-p', projectName, '-f', composeFile, 'logs', '--no-color', '--tail', '300', service], {
      allowFailure: true,
      capture: true,
      redact: true
    });
  }
}

function cleanupOnce() {
  if (cleanupPromise) {
    return cleanupPromise;
  }

  cleanupPromise = composeInvocation
    ? runCompose(['-p', projectName, '-f', composeFile, 'down', '--volumes', '--remove-orphans'], {
        allowFailure: true,
        capture: true,
        redact: true
      })
    : Promise.resolve();
  return cleanupPromise;
}

async function handleSignal(signal, exitCode) {
  if (signalReceived) {
    return;
  }

  signalReceived = true;
  console.error(`Received ${signal}; cleaning up real-backend smoke containers.`);
  await cleanupOnce();
  process.exit(exitCode);
}

function delay(milliseconds) {
  return new Promise((resolve) => setTimeout(resolve, milliseconds));
}
