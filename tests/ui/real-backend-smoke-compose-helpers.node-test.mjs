import assert from 'node:assert/strict';
import test from 'node:test';
import {
  buildRealBackendPlaywrightPlan,
  composeProjectName,
  composeV2Invocation,
  isHstsPreloadedHttpUrl,
  isStaticAngularServerUrl,
  legacyComposeInvocation,
  normalizeExitCode,
  redactSecrets,
  selectComposeInvocation
} from './real-backend-smoke-compose-helpers.mjs';

const DEFAULT_PLAN_RUN_COUNT = 2,
  SINGLE_RUN_COUNT = 1;

test('sanitizes Compose project names and keeps them within the Compose limit', () => {
  const name = composeProjectName(['Coglatas site!', 'RUN/42', 'pid:123', 'x'.repeat(80)]);

  assert.match(name, /^[a-z0-9][a-z0-9_-]*$/);
  assert.ok(name.length <= 63);
  assert.equal(composeProjectName(['---']), 'coglatas-real-backend-smoke');
});

test('prefers Docker Compose v2 when it is available', async () => {
  const calls = [];
  const invocation = await selectComposeInvocation(async (command, args) => {
    calls.push([command, args]);
    return true;
  });

  assert.deepEqual(invocation, composeV2Invocation);
  assert.deepEqual(calls, [['docker', ['compose', 'version']]]);
});

test('falls back to legacy docker-compose when Compose v2 is unavailable', async () => {
  const calls = [];
  const invocation = await selectComposeInvocation(async (command, args) => {
    calls.push([command, args]);
    return command === 'docker-compose';
  });

  assert.deepEqual(invocation, legacyComposeInvocation);
  assert.deepEqual(calls, [
    ['docker', ['compose', 'version']],
    ['docker-compose', ['version']]
  ]);
});

test('reports a clear error when neither Compose command is available', async () => {
  await assert.rejects(
    () => selectComposeInvocation(async () => false),
    /Docker Compose is required for the real-backend browser smoke/
  );
});

test('redacts connection, browser, cookie, CSRF, authorization, and invite secrets', () => {
  const redacted = redactSecrets([
    'Password=database-secret;Host=postgres',
    'COGLATAS_BROWSER_SMOKE_PASSWORD: browser-secret',
    'Authorization: Bearer api-secret',
    'Cookie: session=secret',
    'X-CSRF-Token: csrf-secret',
    'InvitationToken: invite-secret',
    '{"token":"json-secret","password":"json-password"}'
  ].join('\n'));

  for (const secret of ['database-secret', 'browser-secret', 'api-secret', 'session=secret', 'csrf-secret', 'invite-secret', 'json-secret', 'json-password']) {
    assert.doesNotMatch(redacted, new RegExp(secret.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')));
  }
});

test('rejects the static Angular server URL and preserves child exit codes', () => {
  assert.equal(isStaticAngularServerUrl('http://127.0.0.1:4173'), true);
  assert.equal(isStaticAngularServerUrl('http://localhost:4173/app/login'), true);
  assert.equal(isStaticAngularServerUrl('http://coglatas-backend:8080'), false);
  assert.equal(isHstsPreloadedHttpUrl('http://app:8080'), true);
  assert.equal(isHstsPreloadedHttpUrl('http://service.example.app:8080'), true);
  assert.equal(isHstsPreloadedHttpUrl('https://service.example.app:8080'), false);
  assert.equal(isHstsPreloadedHttpUrl('http://coglatas-backend:8080'), false);
  assert.equal(normalizeExitCode(37), 37);
  assert.equal(normalizeExitCode(null), 1);
});

test('runs migrated Functional owners with their config before legacy regression', () => {
  const plan = buildRealBackendPlaywrightPlan();
  const [functionalRun, legacyRun] = plan;
  assert.deepEqual(plan.map((entry) => entry.name), [
    'Functional real-backend owners',
    'legacy real-backend regression'
  ]);
  assert.equal(plan.length, DEFAULT_PLAN_RUN_COUNT);
  const [configFlag, configPath] = functionalRun.args;
  assert.deepEqual([configFlag, configPath], ['--config', 'playwright.functional.config.ts']);
  assert.ok(functionalRun.args.includes('project-task/core-golden-journey.spec.ts'));
  assert.ok(functionalRun.args.includes('--project=functional-chromium'));
  assert.equal(functionalRun.args.includes('--pass-with-no-tests'), false);
  assert.ok(legacyRun.args.includes('tests/ui/real-backend-smoke.spec.ts'));
  assert.ok(legacyRun.args.includes('--project=chromium-desktop'));
});

test('keeps manifest-focused and custom runs on the legacy-compatible single invocation', () => {
  const focused = buildRealBackendPlaywrightPlan([], 'required title');
  const [focusedRun] = focused;
  const grepIndex = focusedRun.args.indexOf('--grep');
  assert.equal(focused.length, SINGLE_RUN_COUNT);
  assert.deepEqual(focusedRun.args.slice(grepIndex), ['--grep', 'required title']);
  assert.ok(focusedRun.args.includes('tests/ui/real-backend-smoke.spec.ts'));

  const custom = buildRealBackendPlaywrightPlan(['custom.spec.ts'], 'focused');
  assert.deepEqual(custom, [{
    name: 'custom',
    args: ['custom.spec.ts', '--grep', 'focused']
  }]);
});

test('P0 selection executes one Files owner independently of the legacy title grep', () => {
  for (const gate of ['functional-fast', 'functional-full']) {
    const [owner, legacy] = buildRealBackendPlaywrightPlan([], 'legacy required title', gate);
    assert.equal(owner.functionalGate, gate);
    assert.ok(owner.args.includes('files/files-fast-journey.spec.ts'));
    assert.equal(owner.args.includes('--grep'), false);
    assert.ok(legacy.args.includes('legacy required title'));
  }
  assert.throws(() => buildRealBackendPlaywrightPlan([], '', 'typo'), /Files owner gate/u);
});
