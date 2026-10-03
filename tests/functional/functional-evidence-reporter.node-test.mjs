import assert from 'node:assert/strict';
import test from 'node:test';
import { spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { ownerResult, requiredOwners } from './fixtures/functional-evidence-reporter.mjs';

test('only one successful first attempt earns Functional PASS', () => {
  assert.equal(ownerResult({ expectedStatus: 'passed', results: [{ status: 'passed', retry: 0, duration: 1 }] }).status, 'PASS');
  assert.equal(ownerResult({ expectedStatus: 'passed', results: [{ status: 'failed', retry: 0, duration: 1 }, { status: 'passed', retry: 1, duration: 1 }] }).status, 'FLAKY');
  assert.equal(ownerResult({ expectedStatus: 'passed', results: [] }).status, 'BLOCKED');
  assert.equal(ownerResult({ expectedStatus: 'failed', results: [{ status: 'failed', retry: 0, duration: 1 }] }).status, 'SKIPPED');
  assert.equal(ownerResult({ expectedStatus: 'passed', results: [{ status: 'skipped', retry: 0, duration: 1 }] }).status, 'SKIPPED');
});

test('all four domains have bounded owners and extended selects implemented announcement expansion', () => {
  for (const domain of ['core', 'files', 'collaboration', 'authz-negative']) {
    assert.ok(requiredOwners(domain, 'functional-fast').length > 0);
  }
  assert.ok(requiredOwners('collaboration', 'functional-extended').includes('FUNC-ANN-001'));
  assert.ok(!requiredOwners('collaboration', 'functional-fast').includes('FUNC-ANN-001'));
  assert.throws(() => requiredOwners('unknown', 'functional-full'));
});

test('actual Playwright metadata reporter fails empty, skipped, quarantined, and retry-pass owners', () => {
  const cli = fileURLToPath(new URL('../../node_modules/@playwright/test/cli.js', import.meta.url));
  const playwright = fileURLToPath(new URL('../../node_modules/@playwright/test/index.js', import.meta.url));
  const reporter = fileURLToPath(new URL('./fixtures/functional-evidence-reporter.mjs', import.meta.url));
  for (const mode of ['passed', 'missing', 'skipped', 'quarantined', 'flaky']) {
    const directory = mkdtempSync(join(tmpdir(), 'functional-metadata-'));
    try {
      mkdirSync(join(directory, 'artifacts/functional'), { recursive: true });
      mkdirSync(join(directory, 'tests/functional'), { recursive: true });
      writeFileSync(join(directory, 'artifacts/functional/setup-core.json'), '{"setupSeconds":1}');
      writeFileSync(join(directory, 'tests/functional/quarantine.json'), JSON.stringify({ entries: mode === 'quarantined' ? [{ journeyId: 'FUNC-TASK-001' }] : [] }));
      const declaration = mode === 'missing' ? '' : `
        test${mode === 'skipped' ? '.skip' : ''}('synthetic reporter plumbing', {
          annotation: [{type:'journey',description:'FUNC-TASK-001'},{type:'backend',description:'real'}]
        }, async ({}, info) => {
          if (${JSON.stringify(mode)} === 'flaky' && info.retry === 0) throw new Error('synthetic first attempt failure');
        });`;
      writeFileSync(join(directory, 'metadata.spec.ts'), `import { test } from ${JSON.stringify(playwright)};\n${declaration}`);
      writeFileSync(join(directory, 'playwright.config.ts'), `export default {testDir:'.', workers:1, retries:1, reporter:[[${JSON.stringify(reporter)}]]};`);
      const result = spawnSync(process.execPath, [cli, 'test', '--pass-with-no-tests'], {
        cwd: directory, encoding: 'utf8', timeout: 30000,
        env: { ...process.env, TARGET_SHA: 'a'.repeat(40), GITHUB_RUN_ID: '100', GITHUB_RUN_ATTEMPT: '1',
          COGLATAS_FUNCTIONAL_DOMAIN: 'core', COGLATAS_FUNCTIONAL_SELECTED_GATES: 'functional-full' },
      });
      assert.ifError(result.error);
      const evidence = JSON.parse(readFileSync(join(directory, 'artifacts/functional/lane-core.json'), 'utf8'));
      assert.equal(evidence.commitSha, 'a'.repeat(40));
      assert.equal(evidence.journeys[0].status, { passed: 'PASS', missing: 'BLOCKED', skipped: 'SKIPPED', quarantined: 'QUARANTINED', flaky: 'FLAKY' }[mode]);
      assert.equal(result.status === 0, mode === 'passed', result.stdout + result.stderr);
      assert.ok(!JSON.stringify(evidence).includes('synthetic first attempt failure'));
    } finally {
      rmSync(directory, { recursive: true, force: true });
    }
  }
});
