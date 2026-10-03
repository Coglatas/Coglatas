import http from 'k6/http';
import { Counter, Trend } from 'k6/metrics';

// Export only scalar custom metrics. Protected bodies and credentials remain
// in VU memory; built-in HTTP tags/URLs and console output are never saved.
const config = JSON.parse(open(__ENV.PERF_K6_CONFIG));
const base = __ENV.COGLATAS_PERFORMANCE_BASE_URL;
if (!/^http:\/\/(127\.0\.0\.1|localhost|\[::1\]):\d+$/.test(base)) {
  throw new Error('PERF-04 requires an isolated loopback target');
}
const metrics = {};
for (const item of config.scenarios) {
  const key = item.id.replace(/[^a-zA-Z0-9]/g, '_');
  metrics[item.id] = {
    latency: new Trend(`perf_${key}_latency`, true),
    requests: new Counter(`perf_${key}_requests`),
    errors: new Counter(`perf_${key}_errors`),
    timeouts: new Counter(`perf_${key}_timeouts`),
    seconds: new Counter(`perf_${key}_seconds`),
  };
}
const authFailures = new Counter('perf_auth_failures');
const healthFailures = new Counter('perf_health_failures');
let session;
let snapshot;
let movingTaskId;

export const options = {
  scenarios: { api: {
    executor: 'shared-iterations', vus: config.profile.vus,
    iterations: config.profile.iterations,
    maxDuration: config.profile.maxDuration,
  } },
  summaryTrendStats: ['min', 'med', 'max', 'p(50)', 'p(95)', 'p(99)', 'count'],
  // PERF-03 owns the decisions; k6 performs no independent threshold checks.
  systemTags: [], noCookiesReset: true, userAgent: 'Coglatas-PERF-04',
};
function request(method, path, body, headers, retainBody = false) {
  return http.request(method, `${base}${path}`, body, {
    headers, timeout: config.profile.requestTimeout, redirects: 0,
    responseType: retainBody ? 'text' : 'none',
  });
}
function requiredJson(response) {
  if (response.status !== 200) throw new Error('PERF-04 protected precondition failed');
  try { return response.json(); } catch (_) {
    throw new Error('PERF-04 invalid protected precondition');
  }
}
function initialize() {
  try {
    const headers = { 'X-Tenant-Slug': config.identities.tenantSlug };
    const csrf = requiredJson(request('GET', '/api/security/csrf-token', null, headers, true));
    if (!csrf.token || !csrf.headerName) throw new Error('PERF-04 missing CSRF');
    headers['Content-Type'] = 'application/json';
    headers[csrf.headerName] = csrf.token;
    const login = request('POST', '/api/auth/login', JSON.stringify({
      email: config.identities.operatorEmail, password: __ENV.COGLATAS_PERFORMANCE_PASSWORD,
    }), headers);
    if (login.status !== 200 || request('GET', '/api/auth/me', null, headers).status !== 200) {
      throw new Error('PERF-04 authentication failed');
    }
    const authenticatedCsrf = requiredJson(request('GET', '/api/security/csrf-token', null, headers, true));
    if (!authenticatedCsrf.token || !authenticatedCsrf.headerName) throw new Error('PERF-04 missing authenticated CSRF');
    headers[authenticatedCsrf.headerName] = authenticatedCsrf.token;
    session = headers;
    for (let iteration = 0; iteration < config.profile.warmupIterations; iteration++) {
      for (const item of config.scenarios.filter(s => s.method === 'GET')) {
        if (request('GET', route(item.path), null, session).status !== 200) {
          throw new Error('PERF-04 warm-up failed');
        }
      }
    }
    // Mutation warm-up is read-only. Each write group starts from a reseeded DB.
    snapshot = requiredJson(request('GET', boardPath(), null, session, true));
    const card = snapshot.cards.find(c => c.uiPermissions.canMove &&
      snapshot.cards.filter(other => other.workflowStageId === c.workflowStageId).length > 1);
    if (!card) throw new Error('PERF-04 no resettable mutable card');
    movingTaskId = card.taskId;
  } catch (_) {
    authFailures.add(1);
    session = null;
    throw new Error('PERF-04 authenticated preflight or warm-up failed');
  }
}
function route(template) {
  return template.replace(/\{([a-zA-Z]+)\}/g, (_, key) => {
    const value = config.identities[key];
    if (!value) throw new Error('PERF-04 missing fixture identity');
    return encodeURIComponent(value);
  });
}
function boardPath() {
  return `/api/projects/${config.identities.kanbanProjectId}/kanban?maxCards=300`;
}
function mutation() {
  const card = snapshot.cards.find(c => c.taskId === movingTaskId);
  if (!card) throw new Error('PERF-04 lost mutable card');
  const others = snapshot.cards.filter(c => c.workflowStageId === card.workflowStageId && c.taskId !== card.taskId)
    .sort((a, b) => a.boardOrder - b.boardOrder);
  const atBeginning = card.boardOrder < others[0].boardOrder;
  const response = request('POST', `/api/tasks/${card.taskId}/kanban-move`, JSON.stringify({
    targetWorkflowStageId: card.workflowStageId,
    targetBeforeTaskId: atBeginning ? null : others[0].taskId,
    targetAfterTaskId: atBeginning ? others[others.length - 1].taskId : null,
    expectedTaskVersion: card.version, expectedBoardVersion: snapshot.board.version,
  }), session, true);
  if (response.status === 200) {
    const next = requiredJson(response).snapshot;
    const changed = next.cards.find(c => c.taskId === movingTaskId);
    if (!changed || changed.version <= card.version || next.board.version <= snapshot.board.version) {
      throw new Error('PERF-04 mutation did not persist a version advance');
    }
    snapshot = next;
  }
  return response;
}
export default function () {
  if (!session) initialize();
  for (const item of config.scenarios) {
    const started = Date.now();
    const response = item.method === 'POST' ? mutation() : request('GET', route(item.path), null, session);
    const metric = metrics[item.id];
    metric.requests.add(1);
    metric.errors.add(response.status === 200 ? 0 : 1);
    metric.timeouts.add(response.error_code === 1050 ? 1 : 0);
    metric.latency.add(response.timings.duration);
    metric.seconds.add(Math.max((Date.now() - started) / 1000, 0.000001));
  }
}
export function teardown() {
  healthFailures.add(http.get(`${base}/health/ready`, {
    redirects: 0, timeout: config.profile.requestTimeout, responseType: 'none',
  }).status === 200 ? 0 : 1);
}
export function handleSummary(data) {
  const rows = [];
  for (const item of config.scenarios) {
    const key = item.id.replace(/[^a-zA-Z0-9]/g, '_');
    const values = name => (data.metrics[`perf_${key}_${name}`] || {}).values || {};
    const latency = values('latency');
    rows.push({
      scenario: item.id, requestCount: values('requests').count || 0,
      errorCount: values('errors').count || 0, timeoutCount: values('timeouts').count || 0,
      p50: latency['p(50)'] || 0, p95: latency['p(95)'] || 0, p99: latency['p(99)'] || 0,
      durationSeconds: values('seconds').count || 0,
    });
  }
  return { [__ENV.PERF_K6_OUTPUT]: JSON.stringify({
    schemaVersion: 1, warmupSamplesExcluded: true,
    authFailures: ((data.metrics.perf_auth_failures || {}).values || {}).count || 0,
    healthFailures: ((data.metrics.perf_health_failures || {}).values || {}).count || 0,
    scenarios: rows,
  }) };
}
