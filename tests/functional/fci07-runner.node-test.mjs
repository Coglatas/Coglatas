import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import test from 'node:test';

import * as yaml from 'js-yaml';

const composeRunnerPath = 'scripts/ci/run-fci07-functional-security.sh';
const ownerRunnerPath = 'scripts/ci/run-fci07-playwright-owners.sh';
const overlayPath = 'infra/compose/security/fci07-functional-security.yml';
const specPath = 'tests/functional/security-negative/cross-scope-negative-matrix.spec.ts';

test('FCI-07 runners are syntactically valid and keep the security fixture boundary explicit', () => {
  for (const path of [composeRunnerPath, ownerRunnerPath]) {
    const syntax = spawnSync('bash', ['-n', path], { encoding: 'utf8' });
    assert.equal(syntax.status, 0, syntax.stderr || `bash -n failed for ${path}`);
  }

  const composeRunner = readFileSync(composeRunnerPath, 'utf8');
  for (const gate of ['functional-fast', 'functional-full', 'functional-extended']) {
    assert.match(composeRunner, new RegExp(gate));
  }
  assert.match(composeRunner, /infra\/compose\/security\/security\.yml/);
  assert.match(composeRunner, /infra\/compose\/security\/fci07-functional-security\.yml/);

  const overlay = readFileSync(overlayPath, 'utf8');
  const parsed = yaml.load(overlay);
  assert.ok(parsed && typeof parsed === 'object' && !Array.isArray(parsed));
  const service = parsed.services?.['real-backend-playwright'];
  assert.equal(service?.environment?.COGLATAS_SECURITY_CI_FIXTURE_ENABLED, 'true');
  assert.match(String(service?.environment?.COGLATAS_SECURITY_CI_PASSWORD ?? ''), /COGLATAS_SECURITY_CI_PASSWORD/);
  assert.match(String(service?.command ?? ''), /run-fci07-playwright-owners\.sh/);
});

test('FCI-07 requires both owner journeys and rejects skipped or empty owner execution', () => {
  const ownerRunner = readFileSync(ownerRunnerPath, 'utf8');
  assert.match(ownerRunner, /FUNC-AUTHZ-001/);
  assert.match(ownerRunner, /FUNC-AUTHZ-002/);
  assert.match(ownerRunner, /--journey "\$owner"/);
  assert.match(ownerRunner, /testcases\.length !== 1/);
  assert.match(ownerRunner, /testcase\.includes\(owner\)/);
  assert.match(ownerRunner, /skipped\|failure\|error/);

  const spec = readFileSync(specPath, 'utf8');
  assert.match(spec, /journeyId:\s*'FUNC-AUTHZ-001'/);
  assert.match(spec, /journeyId:\s*'FUNC-AUTHZ-002'/);
  assert.match(spec, /negativeAuthz:\s*true/);
  assert.doesNotMatch(spec, /\b(?:test(?:\.[A-Za-z_$][\w$]*)*|testInfo)\.(?:skip|fixme)\b/);

  const packageJson = JSON.parse(readFileSync('package.json', 'utf8'));
  assert.equal(packageJson.scripts['test:functional:fci07'], 'bash scripts/ci/run-fci07-functional-security.sh functional-fast');
  assert.equal(packageJson.scripts['test:functional:fci07:full'], 'bash scripts/ci/run-fci07-functional-security.sh functional-full');
  assert.equal(packageJson.scripts['test:functional:fci07:extended'], 'bash scripts/ci/run-fci07-functional-security.sh functional-extended');
});
