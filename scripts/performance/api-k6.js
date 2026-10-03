/* global __ENV, open */
import { Counter, Trend } from 'k6/metrics';
import http from 'k6/http';

// Export only scalar custom metrics. Keep protected bodies and credentials in
// VU memory; built-in HTTP tags/URLs and console output are never saved.
let movingTaskId = null, session = null, snapshot = null;
const benchmark = {
  acceptMutation: (response, card) => {
    const { snapshot: next } = benchmark.requiredJson(response),
      changed = next.cards.find(candidate => candidate.taskId === movingTaskId);
    if (!changed || changed.version <= card.version || next.board.version <= snapshot.board.version) {
      throw new Error('PERF-04 mutation did not persist a version advance');
    }
    snapshot = next;
  },
  authFailures: new Counter('perf_auth_failures'),
  authenticate: () => {
    const headers = benchmark.csrfHeaders({ 'X-Tenant-Slug': benchmark.config.identities.tenantSlug }),
      loginBody = JSON.stringify({
        email: benchmark.config.identities.operatorEmail, password: __ENV.COGLATAS_PERFORMANCE_PASSWORD,
      });
    headers['Content-Type'] = 'application/json';
    if (benchmark.request('POST', '/api/auth/login', { body: loginBody, headers }).status !== benchmark.httpOk ||
        benchmark.request('GET', '/api/auth/me', { headers }).status !== benchmark.httpOk) {
      throw new Error('PERF-04 authentication failed');
    }
    return benchmark.csrfHeaders(headers);
  },
  base: __ENV.COGLATAS_PERFORMANCE_BASE_URL,
  beginMutation: () => {
    // Mutation warm-up is read-only. Each write group starts from a reseeded DB.
    snapshot = benchmark.requiredJson(benchmark.request('GET', benchmark.boardPath(), { retainBody: true }));
    const card = benchmark.mutableCard(snapshot.cards);
    if (!card) {
      throw new Error('PERF-04 no resettable mutable card');
    }
    movingTaskId = card.taskId;
  },
  boardPath: () => `/api/projects/${benchmark.config.identities.kanbanProjectId}/kanban?maxCards=${benchmark.maximumBoardCards}`,
  config: JSON.parse(open(__ENV.PERF_K6_CONFIG)),
  csrfHeaders: headers => {
    const csrf = benchmark.requiredJson(benchmark.request('GET', '/api/security/csrf-token', { headers, retainBody: true }));
    if (!csrf.token || !csrf.headerName) {
      throw new Error('PERF-04 missing CSRF');
    }
    headers[csrf.headerName] = csrf.token;
    return headers;
  },
  currentCard: () => {
    const card = snapshot.cards.find(candidate => candidate.taskId === movingTaskId);
    if (!card) {
      throw new Error('PERF-04 lost mutable card');
    }
    return card;
  },
  handleSummary: data => ({ [__ENV.PERF_K6_OUTPUT]: JSON.stringify({
    authFailures: ((data.metrics.perf_auth_failures || {}).values || {}).count || benchmark.zero,
    healthFailures: ((data.metrics.perf_health_failures || {}).values || {}).count || benchmark.zero,
    scenarios: benchmark.config.scenarios.map(item => benchmark.summaryRow(data, item)),
    schemaVersion: benchmark.one,
    warmupSamplesExcluded: true,
  }) }),
  healthFailures: new Counter('perf_health_failures'),
  httpOk: 200,
  initialize: () => {
    try {
      session = benchmark.authenticate();
      benchmark.warmup();
      benchmark.beginMutation();
    } catch {
      benchmark.authFailures.add(benchmark.one);
      session = null;
      throw new Error('PERF-04 authenticated preflight or warm-up failed');
    }
  },
  maximumBoardCards: 300,
  metricKey: identifier => identifier.replace(/[^a-zA-Z0-9]/gu, '_'),
  metrics: {},
  millisecondsPerSecond: 1000,
  minimumDurationSeconds: 0.000001,
  moveCommand: (card, others) => {
    const command = {
      expectedBoardVersion: snapshot.board.version,
      expectedTaskVersion: card.version,
      targetAfterTaskId: null,
      targetBeforeTaskId: others[benchmark.zero].taskId,
      targetWorkflowStageId: card.workflowStageId,
    };
    if (card.boardOrder < others[benchmark.zero].boardOrder) {
      command.targetAfterTaskId = others[others.length - benchmark.one].taskId;
      command.targetBeforeTaskId = null;
    }
    return command;
  },
  mutableCard: cards => cards.find(card => card.uiPermissions.canMove &&
    cards.filter(other => other.workflowStageId === card.workflowStageId).length > benchmark.one),
  mutation: () => {
    const card = benchmark.currentCard(),
      response = benchmark.request('POST', `/api/tasks/${card.taskId}/kanban-move`, {
        body: JSON.stringify(benchmark.mutationCommand(card)), retainBody: true,
      });
    if (response.status === benchmark.httpOk) {
      benchmark.acceptMutation(response, card);
    }
    return response;
  },
  mutationCommand: card => {
    const others = snapshot.cards.filter(other => other.workflowStageId === card.workflowStageId &&
        other.taskId !== card.taskId).sort((left, right) => left.boardOrder - right.boardOrder);
    return benchmark.moveCommand(card, others);
  },
  one: 1,
  request: (method, path, { body = null, headers = session, retainBody = false } = {}) => {
    let responseType = 'none';
    if (retainBody) {
      responseType = 'text';
    }
    return http.request(method, `${benchmark.base}${path}`, body, {
      headers, redirects: benchmark.zero, responseType, timeout: benchmark.config.profile.requestTimeout,
    });
  },
  requiredJson: response => {
    if (response.status !== benchmark.httpOk) {
      throw new Error('PERF-04 protected precondition failed');
    }
    try {
      return response.json();
    } catch {
      throw new Error('PERF-04 invalid protected precondition');
    }
  },
  route: template => template.replace(/\{(?<identity>[a-zA-Z]+)\}/gu, (match, key) => {
    const value = benchmark.config.identities[key];
    if (!value) {
      throw new Error('PERF-04 missing fixture identity');
    }
    return encodeURIComponent(value);
  }),
  scenarioRequest: item => {
    if (item.method === 'POST') {
      return benchmark.mutation();
    }
    return benchmark.request('GET', benchmark.route(item.path));
  },
  summaryRow: (data, item) => {
    const counterValues = name => (data.metrics[`perf_${benchmark.metricKey(item.id)}_${name}`] || {}).values || {},
      latency = counterValues('latency');
    return {
      durationSeconds: counterValues('seconds').count || benchmark.zero,
      errorCount: counterValues('errors').count || benchmark.zero,
      p50: latency['p(50)'] || benchmark.zero,
      p95: latency['p(95)'] || benchmark.zero,
      p99: latency['p(99)'] || benchmark.zero,
      requestCount: counterValues('requests').count || benchmark.zero,
      scenario: item.id,
      timeoutCount: counterValues('timeouts').count || benchmark.zero,
    };
  },
  teardown: () => {
    benchmark.healthFailures.add(Number(http.get(`${benchmark.base}/health/ready`, {
      redirects: benchmark.zero, responseType: 'none', timeout: benchmark.config.profile.requestTimeout,
    }).status !== benchmark.httpOk));
  },
  timeoutCode: 1050,
  warmup: () => {
    for (let iteration = benchmark.zero; iteration < benchmark.config.profile.warmupIterations; iteration += benchmark.one) {
      for (const item of benchmark.config.scenarios.filter(scenario => scenario.method === 'GET')) {
        if (benchmark.request('GET', benchmark.route(item.path)).status !== benchmark.httpOk) {
          throw new Error('PERF-04 warm-up failed');
        }
      }
    }
  },
  zero: 0
}, { handleSummary } = benchmark, options = {
  noCookiesReset: true,
  scenarios: { api: {
    executor: 'shared-iterations', iterations: benchmark.config.profile.iterations,
    maxDuration: benchmark.config.profile.maxDuration, vus: benchmark.config.profile.vus,
  } },
  summaryTrendStats: ['min', 'med', 'max', 'p(50)', 'p(95)', 'p(99)', 'count'],
  // PERF-03 owns the decisions; k6 performs no independent threshold checks.
  systemTags: [], userAgent: 'Coglatas-PERF-04',
}, { teardown } = benchmark;

if (!/^http:\/\/(?:127\.0\.0\.1|localhost|\[::1\]):\d+$/u.test(benchmark.base)) {
  throw new Error('PERF-04 requires an isolated loopback target');
}
for (const item of benchmark.config.scenarios) {
  const key = benchmark.metricKey(item.id);
  benchmark.metrics[item.id] = {
    errors: new Counter(`perf_${key}_errors`),
    latency: new Trend(`perf_${key}_latency`, true),
    requests: new Counter(`perf_${key}_requests`),
    seconds: new Counter(`perf_${key}_seconds`),
    timeouts: new Counter(`perf_${key}_timeouts`),
  };
}

export { handleSummary, options, teardown };

export default function measure() {
  if (!session) {
    benchmark.initialize();
  }
  for (const item of benchmark.config.scenarios) {
    const beganAt = Date.now(), response = benchmark.scenarioRequest(item), scenarioMetrics = benchmark.metrics[item.id];
    scenarioMetrics.requests.add(benchmark.one);
    scenarioMetrics.errors.add(Number(response.status !== benchmark.httpOk));
    scenarioMetrics.timeouts.add(Number(response.error_code === benchmark.timeoutCode));
    scenarioMetrics.latency.add(response.timings.duration);
    scenarioMetrics.seconds.add(Math.max((Date.now() - beganAt) / benchmark.millisecondsPerSecond, benchmark.minimumDurationSeconds));
  }
}
