import { spawnSync } from 'node:child_process';
import { buildFunctionalGrep } from './build-functional-grep.mjs';
import { requiredOwners } from '../../tests/functional/fixtures/functional-evidence-reporter.mjs';

const domain = process.env.COGLATAS_FUNCTIONAL_DOMAIN;
const gate = process.env.COGLATAS_FUNCTIONAL_SELECTED_GATES;
const journeys = requiredOwners(domain, gate);
const result = spawnSync(process.execPath, [
  'node_modules/@playwright/test/cli.js', 'test',
  '--config', 'playwright.functional.config.ts',
  '--grep', buildFunctionalGrep({ gates: [gate], journeys, backends: ['real'] }),
  '--project=functional-chromium', '--workers=1', '--retries=0',
], { env: process.env, stdio: 'inherit' });
if (result.error) throw result.error;
process.exitCode = result.status ?? 1;
