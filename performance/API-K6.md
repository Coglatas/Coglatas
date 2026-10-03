# PERF-04 API benchmark

`api-k6.json` supplements the PERF-01 scenario inventory with a bounded API
profile and coarse regression budgets. These ceilings detect major regressions;
they are not production capacity claims or newly established customer SLOs.

The harness pins Grafana k6 **1.0.0**, verifies the running binary version, and
uses only repository-owned scripts. It targets the PERF-02 isolated Release
ASP.NET Core + PostgreSQL environment with the public-PR source overlay. No
Syncfusion or production credential is passed to PR code.

## Coverage and sample boundaries

The one-VU profile measures 20 requests for each of 13 required scenarios:
authenticated session, Workspace list/detail, Project list/detail, Task
list/detail/My Tasks, Kanban, current-contract Gantt, paginated Conversations,
Notifications, and Kanban mutation. The paginated endpoints use their current
bounded page-size contracts. Gantt deliberately uses the implemented snapshot
route, without inventing cursor parameters.

CSRF/login/authenticated preflight and two read warm-up iterations happen in VU
memory before custom metrics begin. Warm-up and mutation-preparation reads do
not contribute to request counts, latency, or throughput. Any denied read,
failed authentication, redirect, missing scenario, partial group, timeout,
unhealthy recovery, or benchmark process failure blocks the gate.

Mutation chooses a permitted synthetic card and alternates its rank between the
beginning/end of its current stage. Each successful write must advance both
Task and board versions. Every independent trial starts a fresh PERF-02 stack,
which migrates and reseeds the disposable database before the mutation group.
No test-only production endpoint is added. Nightly PERF-10 can reuse the script
and collection entry point with a separately approved extended workload policy.

## Decisions

All decisions are made by `compare_api_documents` in the PERF-03 comparator;
k6 and the normalization adapter contain no competing performance thresholds.

- PR: one short trial; p50/p95/p99 hard ceilings of 1/2/3 seconds and zero
  error/timeout budgets. Task list alone has a reviewed 2-second p50 ceiling:
  initial five-trial main evidence measured 1.19–1.24 seconds on the unchanged
  baseline, so the initial 1-second default was below existing behavior. The
  review ledger and `evidence/perf04-task-list-calibration.json` preserve the
  measurements and before/after policy decisions. Other scenario ceilings and
  relative/noise/error policies remain unchanged. Throughput is recorded. There is no fabricated measured
  baseline for an absolute-only gate.
- Main: five independent paired baseline/current trials. Compare medians of the
  per-trial p50/p95/p99 and throughput. A latency increase must exceed both 50%
  and the metric's 100/200/300 ms noise floor; a throughput decrease over 40%
  blocks. The common PERF-03 MAD/range variability policy still rejects unstable
  main evidence. Every individual trial must meet the absolute/error ceilings.

Throughput is requests per second of the scenario's accumulated active wall
time at one VU, including its client command processing. It is not a capacity
estimate and does not include another scenario's idle time.

The exact reviewed baseline source SHA is pinned in `api-k6.json`. The main
runner verifies it is an ancestor of `origin/main`, checks it out separately,
and measures it with the same current benchmark contract on the same runner.
Both fixture and environment fingerprints must match. The main source SHA is
never advanced automatically. A source/fixture/toolchain incompatibility fails
rather than silently generating a new baseline.

Baseline SHA changes, removal of a blocking scenario/budget, and threshold or
latency-noise-floor relaxation are checked with the existing PERF-03 transition
validator and `baseline-updates.json`. Ledger budget IDs are
`perf04.<scenario>.<metric>.<field>`. Changes need reviewable reason and
before/after evidence; raising a baseline to hide a regression is prohibited.

## CI and artifacts

`.github/workflows/performance-api.yml` always produces the stable
`performance-fast` check. Source/backend-test, runtime/toolchain, performance,
and workflow changes require the API child job; explicit non-applicable routes
may skip it. Required missing/skipped/cancelled child jobs fail the aggregate.
The workflow runs the fast mode on ordinary PRs and regression mode on main/dispatch. PRs changing the harness/contracts additionally run the five-trial main comparison to verify the integration before merge.
Branch protection registration and the cross-lane `ci/performance` aggregate
remain PERF-11 ownership.

Only allowlisted counters/percentiles/rates, toolchain/fixture fingerprints,
profile, exact SHAs and common v1 metric decisions are uploaded for 14 days.
Raw HTTP output, response bodies, cookies, CSRF/session tokens, passwords and
k6 process logs are never uploaded. Credentials and k6 configuration use a
private temporary directory removed on completion/failure. Process diagnostics
are discarded instead of echoing protected data. A failed or absent run cannot
reuse stale result files.

## Local commands

Requires Linux Docker Compose v2. The password must be a newly generated
synthetic credential, never a production login.

```bash
python3 scripts/performance/api_k6.py validate
python3 -m unittest discover -s tests/ci -p 'test_performance*.py'
node --test tests/ci/performance-api-k6.test.mjs
bash scripts/performance/run-api-k6.sh fast
bash scripts/performance/run-api-k6.sh regression
```

The local VM transport tests cover delayed/500 responses, authentication,
post-run health, actual mutation version advancement, protected-output
projection and warm-up exclusion. Parser/comparator tests cover missing,
insufficient, unstable, incompatible and deterministic evidence. The real
Release/PostgreSQL/k6 run in CI is required runtime evidence; VM tests alone do
not prove database behavior.
