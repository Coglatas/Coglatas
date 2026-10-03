import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { requiredFci04Steps } from './fixtures/fci04-owner-reporter.mjs';

const playwrightCli = fileURLToPath(new URL('../../node_modules/@playwright/test/cli.js', import.meta.url));
const playwrightImport = fileURLToPath(new URL('../../node_modules/@playwright/test/index.js', import.meta.url));
const reporter = fileURLToPath(new URL('./fixtures/fci04-owner-reporter.mjs', import.meta.url));

function runFixture(gate, mode) {
  const directory = mkdtempSync(join(tmpdir(), 'fci04-reporter-'));
  try {
    const steps = mode === 'incomplete' ? requiredFci04Steps(gate).slice(1) : requiredFci04Steps(gate);
    const declaration = mode === 'missing' ? '' : `
      test${mode === 'skipped' ? '.skip' : ''}('reporter contract fixture', {
        annotation: [
          { type: 'journey', description: 'FUNC-TASK-001' },
          { type: 'backend', description: 'real' },
          { type: 'functional-gates', description: ${JSON.stringify(gate)} }
        ]
      }, async () => {
        for (const step of ${JSON.stringify(steps)}) {
          await test.step(step, async () => {});
        }
      });`;
    writeFileSync(join(directory, 'contract.spec.ts'), `import { test } from ${JSON.stringify(playwrightImport)};\n${declaration}`);
    writeFileSync(join(directory, 'playwright.config.ts'), `export default {
      testDir: '.', workers: 1, retries: 0, reporter: [['list'], [${JSON.stringify(reporter)}]]
    };`);
    const result = spawnSync(process.execPath, [playwrightCli, 'test', '--pass-with-no-tests'], {
      cwd: directory,
      env: { ...process.env, COGLATAS_FUNCTIONAL_SELECTED_GATES: gate, TARGET_SHA: 'a'.repeat(40) },
      encoding: 'utf8',
      timeout: 30_000,
    });
    assert.ifError(result.error);
    const output = `${result.stdout}\n${result.stderr}`;
    if (mode === 'passed') {
      assert.equal(result.status, 0, output);
      const evidence = JSON.parse(readFileSync(join(directory, `test-results/fci04-${gate}-owner.json`), 'utf8'));
      assert.equal(evidence.status, 'PASS');
      assert.equal(evidence.candidateSha, 'a'.repeat(40));
      assert.deepEqual(evidence.completedSteps, requiredFci04Steps(gate));
    } else {
      assert.notEqual(result.status, 0, output);
      assert.match(output, /FUNC-TASK-001/);
    }
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
}

test('actual Playwright reporter accepts completed fast/full steps without browser or backend credit', () => {
  runFixture('functional-fast', 'passed');
  runFixture('functional-full', 'passed');
});

test('actual Playwright reporter fails missing/skipped/incomplete owners despite an otherwise green runner', () => {
  runFixture('functional-fast', 'missing');
  runFixture('functional-fast', 'skipped');
  runFixture('functional-full', 'incomplete');
});
