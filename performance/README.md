# Performance CI contract and environment (PERF-01 / PERF-02)

This directory is the versioned contract for Performance CI. It defines what is measured, the deterministic workload shapes, the metrics collected, and which results may block CI. PERF-02 adds the isolated execution foundation; PERF-04 adds the pinned API k6 harness documented in [API-K6.md](API-K6.md). Lighthouse scenarios and product-code performance changes remain separate work.

JSON is used so validation can run with the Python standard library already available in CI. The parser rejects non-standard numeric constants such as `Infinity` and `NaN` and produces a deterministic summary.

## Inventory baseline

The initial inventory was taken from `main` at `79efe27722e3b9c2ddc2d6d5eed5010299e4df32` on 2026-09-04. Routes in `scenarios.json` are backed by current controller or Angular route source; no route is added from a future plan alone.

The scenario matrix covers authentication/session bootstrap, Workspace and Project list/detail, Task list/detail/My Tasks, Kanban, Gantt, File metadata/list/download preflight, Conversation/message list, Notification/Announcement list, one resettable mutation, and SignalR as a separate realtime metric family.

## Dataset profiles

`datasets.json` defines deterministic `small`, `medium`, and `large` seed manifests. Counts are workload shapes for repeatable testing, not product-capacity promises. The focus cardinalities deliberately exercise different contracts:

- `small`: mostly one or two pages;
- `medium`: sustained multi-page list and Gantt reads;
- `large`: deep pagination and a Gantt graph above the historical PR06 full-snapshot envelope.

The list defaults recorded from current source are Project/Task/My Tasks `50`, Files/Conversations/Notifications/Announcements `20`, Messages `50`, and Kanban `MaxCards=300` with an explicit maximum of `500`.

### Gantt contract and current implementation mismatch

Issue #270 is closed as completed and defines cursor pagination with default page size `100`, maximum page size `200`, stable ordering, virtual scrolling, and a bounded client cache. It also states that any future Project-wide hard limit is a separate deployment-configurable, load-tested owner decision.

Current `main` does not match that closed-issue contract: `GET /api/projects/{projectId}/gantt` still has no cursor/page-size input, and the source still enforces the temporary PR06 full-snapshot envelope of 500 combined items and 2,000 dependencies. Active PR06 verification documentation also still describes large-project delivery as follow-up work.

PERF-01 therefore records both facts without silently choosing the stale implementation as a new product contract:

- canonical PERF dataset pagination: #270 default `100`, max `200`;
- Project-wide hard limit: none approved by #270;
- observed 500/2,000 values: implementation-mismatch evidence only, **never** dataset capacity limits;
- the `large` profile intentionally exceeds both historical temporary values. A future performance runner must fail closed rather than shrinking the fixture or treating a rejected/missing sample as a performance success until the product implementation and #270 status are reconciled.

## Metrics and gate classes

API scenarios declare p50/p95/p99 latency, error rate, and throughput. Relevant list/load paths also declare query count, total DB time, and optional slow-query evidence. Browser-backed scenarios declare navigation/load/interaction metrics. Kanban/Gantt and realtime scenarios reserve runtime RSS/heap/GC/CPU/DB-connection metrics for extended runs. SignalR uses its own realtime metric family.

Gate classes are:

- `hard-ceiling`: an existing absolute product ceiling; violation blocks immediately;
- `relative-regression`: compare with an approved measured baseline identity;
- `trend-only`: record but do not block while variance/baselines mature;
- `extended-only`: collect only in nightly or explicitly requested runs.

The initial contract does not invent millisecond SLOs. Latency and DB/runtime values remain trend/extended-only until repeatable measurements establish baselines. PERF-04 later adds coarse API regression budgets in `api-k6.json` and the shared PERF-03 comparator; see [API-K6.md](API-K6.md). The only initial hard ceiling was the existing Angular production initial-bundle `maximumError` of `1.02MB` from `frontend/angular.json`.

A relative-regression budget is invalid unless it names an approved baseline identity, SHA, date, evidence, and comparison rule. A budget without rationale/baseline metadata is invalid. `Infinity`, `NaN`, missing samples, timeouts, effectively-disabled thresholds, and average-only latency gates are forbidden by policy. Tiny GitHub-hosted runner deltas are not blocking until a stable relative envelope is explicitly approved.

Any PR that relaxes a blocking budget must carry reason, before evidence, and after evidence in the change/review record. Do not change a numeric ceiling only to make CI green.

## Ownership boundaries

- #74: DB-side pagination ownership for large list reads (historically closed as not planned, retained as the performance-boundary reference).
- #78: N+1/query-count ownership for list/read projections (historically closed as not planned, retained as the query-shape boundary reference).
- #270: large-project Gantt pagination/virtualization contract and its current source/status reconciliation.

PERF-01 owns only the test contract and validation. It does not absorb product-code performance remediation.

## PERF-02 deterministic environment

`infra/compose/performance/environment.yml` is the only benchmark application stack defined by PERF-02. It uses PostgreSQL 18, the repository `Dockerfile` (Release ASP.NET Core plus Angular production build), a dedicated `coglatas_performance` database, loopback-only host publication, isolated Compose volumes, and a Test-only SaaS/header tenant resolver. Functional, demo, browser-smoke, and Security CI fixtures are explicitly disabled.

The performance fixture is registered through `PerformanceCiHostingStartup`; it is a no-op unless `COGLATAS_PERFORMANCE_CI_FIXTURE_ENABLED=true` **and** `ASPNETCORE_ENVIRONMENT=Test`. Before the HTTP server accepts traffic it:

1. rejects any provider other than PostgreSQL and any database/host outside the dedicated local/Compose allowlist;
2. fails if EF migrations are not at head;
3. truncates all application tables except `__EFMigrationsHistory` in the dedicated database;
4. rebuilds the selected PERF-01 `small`/`medium`/`large` graph using seed-derived stable UUIDs and deterministic ordering;
5. verifies both global and focus cardinalities;
6. writes `fixture.json` containing fixture version, manifest seed, fixture hash, cardinalities, and stable benchmark identities.

The PBKDF2 salt used for the synthetic login credential is intentionally not part of fixture identity or the fixture hash. The hash binds fixture version + the exact `datasets.json` bytes + profile + fixed profile seed, so repeated runs prove the workload contract itself has not drifted.

### Lifecycle command

Set the protected Angular build license and any synthetic local-only password, then run:

```bash
export SYNCFUSION_LICENSE='...'
export COGLATAS_PERFORMANCE_PASSWORD='synthetic-local-password'
COGLATAS_PERFORMANCE_PROFILE=small bash scripts/performance/with-environment.sh
```

The harness always performs: clean pre-teardown → build → PostgreSQL health → migration → deterministic fixture/startup → app health → preflight → non-measured warm-up → environment fingerprint → optional benchmark command → clean `down --volumes --remove-orphans` teardown.

To run a future PERF-03/04 benchmark inside the same guardrail, append its command:

```bash
COGLATAS_PERFORMANCE_PROFILE=medium \
  bash scripts/performance/with-environment.sh <benchmark-command> <args...>
```

The command receives `COGLATAS_PERFORMANCE_BASE_URL` plus paths to fixture, preflight, warm-up, and environment evidence. If `COGLATAS_PERFORMANCE_RESULTS_FILE` is set, PERF-02 additionally requires the generic measurement envelope: warm-up excluded, at least the configured measured sample count, no timeout/non-zero exit, and `environmentStable=true`.

### Warm-up contract

`environment.json` declares the selected API warm-up routes and `measured=false`. Login, DB-backed representative reads, application/JIT work, and route materialization happen before the benchmark command. Warm-up samples are written only to `warmup.json` and are forbidden from measured evidence.

Browser scenarios must also declare cache state. `cold` means a fresh browser context with empty HTTP cache before measured navigation. `warm` may reuse a browser context only within one scenario ID; cache is never carried across scenarios.

### Target safety

Benchmark base URLs are accepted only for explicit-port HTTP hosts in the local/Compose allowlist (`127.0.0.1`, `localhost`, `::1`, and dedicated performance network aliases). HTTPS, credentials in the URL, path-prefixed base URLs, arbitrary DNS names, school hosts, and public production hosts fail before any benchmark request is sent.

The C# fixture independently checks the PostgreSQL database name and data source, so bypassing the Python harness does not turn a production database into a performance seed target.

### Machine-readable evidence

A successful environment run writes under `artifacts/performance/<profile>/`:

- `fixture.json`: seed/profile/cardinality/stable identities and fixture hash;
- `preflight.json`: target/health/fixture preconditions;
- `warmup.json`: explicitly non-measured warm-up samples and browser cache policy;
- `environment.json`: commit SHA, runner OS/image, CPU count/model, memory, .NET SDK/runtime, Node/npm, PostgreSQL, Playwright/browser, container image identities, and fixture hash.

Missing required fingerprint fields fail the run rather than producing partial benchmark evidence.

### CI trust split

`.github/workflows/performance-contract.yml` is PR-safe and does not receive protected secrets. It validates Python/shell syntax, PERF-01, PERF-02 target/fixture/failure contracts, and regression tests.

`.github/workflows/performance-environment.yml` runs on reviewed `main` changes or explicit dispatch under the existing `syncfusion-licensed-build` protected environment. It runs the selected environment twice, compares deterministic fixture identity/cardinality, asserts no benchmark containers or volumes remain after each run, and uploads the machine-readable PERF-02 evidence. The synthetic login password is generated per workflow run and masked; it is not a repository or production credential.

## Validation

Run both fail-closed validators:

```bash
python3 scripts/ci/verify-performance-contract.py
python3 scripts/ci/verify-performance-environment.py
```

Run their regression tests:

```bash
python3 -m unittest discover -s tests/ci -p 'test_performance_contract.py'
python3 -m unittest discover -s tests/ci -p 'test_performance_environment.py'
```

Validation rejects duplicate scenario IDs, unknown metrics/gates, missing/disabled budget metadata, relative budgets without baseline identity, invalid pagination/cardinality math, a Gantt page size above #270's maximum, a `large` Gantt profile silently collapsed into the old temporary full-snapshot envelope, public/production-like benchmark targets, incomplete fixture evidence, warm-up mixed into measured samples, insufficient samples, process failures/timeouts, missing environment fingerprints, and environment instability.
