import assert from 'node:assert/strict';
import test from 'node:test';
import { requiredFci04Steps, validateFci04Owner } from './fixtures/fci04-owner-reporter.mjs';
import { buildFci04OwnerPlan, fci04P0Gates } from './fixtures/fci04-owner-plan.mjs';

const owner = { journey: 'FUNC-TASK-001', backend: 'real', gates: 'functional-fast,functional-full' };
const record = (gate) => ({ status: 'passed', retry: 0, steps: requiredFci04Steps(gate) });

test('P0 requires an owner for unset/empty gates and rejects empty normalized or unknown selections', () => {
  for (const raw of [undefined, '', '   ']) {
    assert.equal(fci04P0Gates(raw), 'functional-fast');
    assert.equal(buildFci04OwnerPlan(fci04P0Gates(raw)).length, 1);
  }
  assert.equal(fci04P0Gates('functional-full'), 'functional-full');
  assert.throws(() => fci04P0Gates(','), /zero selected owner gates/);
  assert.throws(() => fci04P0Gates('functional-fastish'), /explicit functional/);
});

test('selects one exact real owner per explicit gate independently of legacy grep', () => {
  const plan = buildFci04OwnerPlan('functional-fast,functional-full,functional-fast', true);
  assert.equal(plan.length, 2);
  for (const [index, gate] of ['functional-fast', 'functional-full'].entries()) {
    const run = plan[index];
    assert.equal(run.environment.COGLATAS_FUNCTIONAL_SELECTED_GATES, gate);
    assert.equal(run.environment.COGLATAS_FCI04_REQUIRED, '1');
    assert.ok(run.args.includes('--project=functional-chromium'));
    assert.ok(run.args.includes('--retries=0'));
    const grep = new RegExp(run.args[run.args.indexOf('--grep') + 1]);
    const tags = `@functional @${gate} @real-backend @journey-FUNC-TASK-001`;
    assert.equal(grep.test(tags), true);
    assert.equal(grep.test(tags.replace('FUNC-TASK-001', 'FUNC-TASK-002')), false);
    assert.equal(grep.test(tags.replace('@real-backend', '@mock-backend')), false);
  }
  assert.throws(() => buildFci04OwnerPlan('', true), /zero selected owner gates/);
  assert.throws(() => buildFci04OwnerPlan('functional-fastish'), /explicit functional/);
  assert.deepEqual(buildFci04OwnerPlan(), []);
});

test('accepts exactly one executed fast/full owner with every required step', () => {
  for (const gate of ['functional-fast', 'functional-full']) {
    assert.equal(validateFci04Owner([owner], [record(gate)], gate).status, 'PASS');
  }
  assert.equal(requiredFci04Steps('functional-fast').length, 9);
  assert.equal(requiredFci04Steps('functional-full').length, 11);
});

test('rejects missing, duplicated, mocked, mistagged, skipped, failed, and retried owners', () => {
  const gate = 'functional-fast';
  for (const [discovered, records] of [
    [[], []],
    [[owner], []],
    [[owner, owner], [record(gate)]],
    [[{ ...owner, backend: 'mock' }], [record(gate)]],
    [[{ ...owner, journey: 'FUNC-TASK-002' }], [record(gate)]],
    [[{ ...owner, gates: 'functional-fastish' }], [record(gate)]],
    [[owner], [{ ...record(gate), status: 'skipped' }]],
    [[owner], [{ ...record(gate), status: 'failed' }]],
    [[owner], [{ ...record(gate), retry: 1 }]],
    [[owner], [record(gate), record(gate)]],
  ]) {
    assert.throws(() => validateFci04Owner(discovered, records, gate), /FUNC-TASK-001/);
  }
});

test('rejects missing full expansion and identifies the missing journey step', () => {
  assert.throws(
    () => validateFci04Owner([owner], [record('functional-fast')], 'functional-full'),
    /FUNC-TASK-001 \/ STEP-03/,
  );
  assert.throws(() => requiredFci04Steps(''), /explicit fast\/full gate/);
  assert.throws(() => requiredFci04Steps('functional-fast,functional-full'), /explicit fast\/full gate/);
});
