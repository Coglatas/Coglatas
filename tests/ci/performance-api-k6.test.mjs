import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
import test from 'node:test';

const contract = JSON.parse(fs.readFileSync('performance/api-k6.json', 'utf8'));
const source = fs.readFileSync('scripts/performance/api-k6.js', 'utf8')
  .replace(/^import .*;\n/gm, '')
  .replace('export default function ()', 'function measure()')
  .replaceAll('export ', '');

function harness(fault = '') {
  const recorded = new Map();
  let requests = 0;
  let version = 1;
  let order = 1;
  class Counter {
    constructor(name) { this.name = name; recorded.set(name, []); }
    add(value) { recorded.get(this.name).push(value); }
  }
  const snapshot = () => ({ board: { version }, cards: [
    { taskId: 'moving', workflowStageId: 'stage', boardOrder: order, version, uiPermissions: { canMove: true } },
    { taskId: 'other', workflowStageId: 'stage', boardOrder: 2, version: 1, uiPermissions: { canMove: true } },
  ] });
  const http = {
    request(method, url, body, options) {
      requests++;
      assert.equal(options.redirects, 0);
      if (fault === 'auth' && url.endsWith('/api/auth/login')) return { status: 401 };
      let json = {};
      if (url.includes('csrf-token')) json = { token: 'protected-token', headerName: 'X-CSRF' };
      if (url.includes('/kanban?')) json = snapshot();
      if (method === 'POST' && url.includes('kanban-move')) {
        const command = JSON.parse(body);
        assert.equal(command.expectedTaskVersion, version);
        assert.equal(command.expectedBoardVersion, version);
        if (fault !== 'noop') { version++; order = command.targetBeforeTaskId ? 1 : 3; }
        json = { snapshot: snapshot() };
      }
      return { status: fault === '500' && url.includes('/api/notifications') && requests > 40 ? 500 : 200,
               timings: { duration: fault === 'slow' ? 4000 : 10 }, json: () => json };
    },
    get() { return { status: fault === 'health' ? 500 : 200 }; },
  };
  const config = { ...contract, identities: {
    tenantSlug: 'perf-small', operatorEmail: 'synthetic@example.invalid',
    workspaceId: 'workspace', taskListProjectId: 'project', taskId: 'task',
    kanbanProjectId: 'kanban', ganttProjectId: 'gantt',
  } };
  const context = vm.createContext({
    http, Counter, Trend: Counter, Date, JSON,
    open: () => JSON.stringify(config),
    __ENV: { PERF_K6_CONFIG: '/private/config.json', PERF_K6_OUTPUT: '/private/result.json',
             COGLATAS_PERFORMANCE_BASE_URL: 'http://127.0.0.1:18080', COGLATAS_PERFORMANCE_PASSWORD: 'protected-password' },
  });
  vm.runInContext(source, context);
  return { context, recorded, get requests() { return requests; } };
}

test('warm-up and login are excluded, mutation advances state, summary has no protected values', () => {
  const h = harness();
  for (let i = 0; i < 20; i++) vm.runInContext('measure()', h.context);
  vm.runInContext('teardown()', h.context);
  assert.equal(h.recorded.get('perf_workspace_list_requests').length, 20);
  assert.equal(h.recorded.get('perf_mutation_kanban_move_requests').length, 20);
  assert.ok(h.requests > 20 * contract.scenarios.length);
  h.context.data = { metrics: Object.fromEntries([...h.recorded].map(([name, values]) => [name, { values: {
    count: values.reduce((a, b) => a + b, 0), 'p(50)': 10, 'p(95)': 10, 'p(99)': 10,
  } }])) };
  const result = vm.runInContext('handleSummary(data)', h.context);
  const text = JSON.stringify(result);
  assert.ok(!text.includes('protected'));
  assert.ok(!text.includes('synthetic@example'));
  assert.ok(!text.includes('http://'));
});

test('authentication failure aborts and records failure', () => {
  const h = harness('auth');
  assert.throws(() => vm.runInContext('measure()', h.context), /preflight/);
  assert.equal(h.recorded.get('perf_auth_failures')[0], 1);
  assert.equal(h.recorded.get('perf_workspace_list_requests').length, 0);
});

test('mutation success without persistence cannot report a complete sample group', () => {
  const h = harness('noop');
  assert.throws(() => vm.runInContext('measure()', h.context), /version advance/);
  assert.equal(h.recorded.get('perf_mutation_kanban_move_requests').length, 0);
});

test('post-run health failure is captured', () => {
  const h = harness('health');
  vm.runInContext('teardown()', h.context);
  assert.equal(h.recorded.get('perf_health_failures')[0], 1);
});

test('injected 500 is an error and delay remains visible to the comparator', () => {
  const failed = harness('500');
  for (let i = 0; i < 20; i++) vm.runInContext('measure()', failed.context);
  assert.ok(failed.recorded.get('perf_notification_list_errors').some(value => value === 1));
  const slow = harness('slow');
  vm.runInContext('measure()', slow.context);
  assert.equal(slow.recorded.get('perf_workspace_list_latency')[0], 4000);
});
