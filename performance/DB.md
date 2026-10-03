# PERF-05 PostgreSQL structural regression gate

Issue #606 adds request-local Npgsql command observation inside the explicit
PERF-02 fixture boundary. Both `ASPNETCORE_ENVIRONMENT=Test` and
`COGLATAS_PERFORMANCE_CI_FIXTURE_ENABLED=true` are required; the additional
`COGLATAS_PERFORMANCE_DB_CAPTURE_ENABLED=true` opt-in activates measurement.
Production/Development do not register the listener or middleware.

## Inventory and budgets

`db-scenarios.json` registers the nine current major list surfaces from
PERF-01. Each profile exercises two page sizes, repeated first/second pages,
and five measured iterations after a separate exact-route warm-up. Message
pages use the implemented timestamp cursor. Repeated identities/order,
disjoint adjacent pages, and fixture cardinalities are checked in memory;
response content and identifiers are not saved.

The structural policy is source-owned and finite: command hard ceiling,
fixed-page small-to-medium growth, page-size growth, actual database-page
materialization, and an extreme slow-command ceiling. Budget rationale,
source evidence, and the inventory main SHA are versioned. The validator
rejects missing inventory entries, disabled/infinite thresholds, missing or
duplicate measurements, incomplete instrumentation, inconsistent totals,
and unknown fields in command evidence.

The PostgreSQL gate job requires both collectors to succeed. It recomputes
per-sample structural decisions, compares both fixture cardinalities, and
fails on any structural violation. A passing Python/unit test is not a
passing product scenario.

## Safe instrumentation

Npgsql Activity spans count both EF commands and directly executed Npgsql
commands exactly once. Their SQL stays in memory: string/numeric literals,
parameters and comments are normalized before computing a SHA-256 identity.
Only the fingerprint, duration, failed boolean, allowlisted root table, and
outer-query LIMIT/ORDER flags leave the process. Activity tags, events,
exception descriptions, SQL text and parameter values are never serialized.

EF supplies `readOperations` where available. This is a safe upper bound on
rows consumed: it includes the final false Read attempt and is not called an
exact returned-row count. Direct Npgsql commands have null row evidence.
Nested predicate/JOIN LIMIT/ORDER clauses cannot masquerade as page clauses.
An EF derived root source can supply its bounded ordered collection page. The gate
requires a bounded ordered reader of the expected collection and rejects
oversized reads, including unbounded collection reads preceding an apparent
paged query. Batched related-table reads are not confused with page readers.

Each synthetic request has an opaque capture UUID. Its typed evidence file
is read, allowlist-validated and deleted; only the validated aggregate is
uploaded. Neither responses nor cookies, CSRF/login tokens, passwords,
parameters, connection strings, database error messages or raw EXPLAIN plans
are uploaded. Unknown capture fields fail before aggregation.

## Selected plan invariant

The canonical full-entity Task ID lookup is inspected on the deterministic medium fixture
with at least 3,000 Task rows, after ANALYZE. `EXPLAIN (FORMAT JSON)` must retain
an index equality lookup on the Task `Id` key. Index Scan, Index Only Scan and
the corresponding bitmap path satisfy the invariant when their index
condition constrains that key. Equivalent physical index names are accepted.
Costs, text, minor-version details and unrelated Seq Scans are not asserted. Only a
boolean result, check ID, allowlisted node types and table cardinality are retained. The planner is
not forced with `enable_seqscan=off`, and the check is not run on a small table.

## Duration adapter

PR checks block the ten-second per-command emergency ceiling only; they do
not block microsecond differences or single-run relative timing changes.
Five exact-page samples of `db.total_time_ms` are emitted for every scenario
in the PERF-03 measurement envelope. Page sizes are never mixed.

Main and nightly feed the page-5 stream and the PERF-02 fingerprint to the
existing PERF-03 comparator. Without an approved main baseline, its result
remains invalid/missing-baseline; current or PR samples are never silently
approved as a baseline. Repeated raw samples remain available for baseline
review and DB-time trends. `db-compare.py --baselines <directory>` supports
approved baseline documents at `<profile>/<scenario>.json` and blocks all
non-pass comparisons. Baseline governance remains owned by PERF-03.

## Existing product debt is deliberately detected

The source inventory at `bb6d04351a55a954b01e5e732add3ba8a366cdb9` already has
structural violations:

- Project list: application-side paging plus per-row permission checks.
- Task list: full-project materialization, per-Task authorization and
  application-side paging.
- Conversation list: last-message/read/unread/member lookups per row.
- Announcement list: read-confirmation lookup per row.
- Notification list: current-target visibility resolution in batches/per row.
- Workspace list: currently unpaged; it is inventoried and tested for query
  growth, without inventing a paging parameter contract.

These are documented in each scenario, not exempted or made green with
cardinality-dependent budgets. #606 explicitly excludes fixing #74/#78
product behavior. If execution confirms these violations, this PR must stay
unmerged until separately authorized product remediation is available. No
claim of full Issue acceptance or green CI may be based on local unit tests.

PostgreSQL 18 evidence on candidate `86aaf5a5b0fcd43b54a97e3c3b78562d3e83ce72`
confirmed the following median command counts for first-page requests:

| Scenario | Small, page 5 | Medium, page 5 | Medium, page 10 | Confirmed failure |
| --- | ---: | ---: | ---: | --- |
| Project list | 36 | 44 | 84 | Application paging, query growth/hard ceiling |
| Task list | 1,452 | 6,252 | 6,252 | Full-project materialization, cardinality growth |
| Conversation list | 50 | 79 | 119 | Cardinality/page-size growth, hard ceiling |
| Notification list | 4 | 9 | 9 | Oversized candidate materialization, cardinality growth |
| Announcement list | 11 | 11 | 16 | Page-size query growth |

Workspace, My Tasks, Files and Message list scenarios passed the structural
checks. Both collectors completed with fixture version 2. These observations
are regression evidence, not approved duration baselines or budget relaxations.

## Validation

The DB opt-in selects fixture version 2, supplying Workspace-owned Attachment rows for the Files API
and distinct deterministic Message cursor timestamps after EF's creation-time
stamping. The version participates in the fixture hash; version-1 baselines
are incompatible with DB evidence. The base PERF-02/API fixture retains
version 1 and its established hash when DB capture is disabled. The host creates the capture directory before starting the
app container so the collector can remove its evidence files.

```bash
python3 -m unittest discover -s tests/ci -p 'test_performance_db.py'
dotnet test tests/Coglatas.Tests/Coglatas.Tests.csproj --configuration Release \
  --filter 'FullyQualifiedName~PerformanceDbCaptureTests'
```

The real Npgsql controlled-N+1 and batched-query test requires
`POSTGRES_TEST_CONNECTION_STRING`. Its local skip is not PostgreSQL evidence.
The dedicated `PostgreSQL structural performance` workflow executes the
Release backend against PostgreSQL 18, using PERF-02 health, target, migration,
fixture, warm-up, fingerprint and teardown guards. It uploads sanitized
per-scenario query evidence and blocking results; no product pagination code
is changed by this issue.
