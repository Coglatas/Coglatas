import { buildFunctionalGrep } from './build-functional-grep.mjs';
import { requiredOwners } from '../../tests/functional/fixtures/functional-evidence-reporter.mjs';
import { spawnSync } from 'node:child_process';

const domain = process.env.COGLATAS_FUNCTIONAL_DOMAIN,
  failureExitCode = 1,
  gate = process.env.COGLATAS_FUNCTIONAL_SELECTED_GATES,
  journeys = requiredOwners(domain, gate),
  result = spawnSync(process.execPath, [
  'node_modules/@playwright/test/cli.js', 'test',
  '--config', 'playwright.functional.config.ts',
  '--grep', buildFunctionalGrep({ backends: ['real'], gates: [gate], journeys }),
  '--project=functional-chromium', '--workers=1', '--retries=0',
], { env: process.env, stdio: 'inherit' });
if (result.error) {
  throw result.error;
}
process.exitCode = result.status ?? failureExitCode;
