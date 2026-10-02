# Testing

The historical audit below predates the PR07-C remediation. No final
post-remediation backend, frontend, or hosted result is recorded in this file;
see `docs/verification/task-v1-pr07-c-deadline-digest.md` for the required
evidence record.

## Historical verification snapshot (2026-08-02)

The prior full backend and frontend audit used a disposable migrated PostgreSQL
18 database and a local Windows UI diagnostic. It is retained only as
historical environment context. Its result counts do not establish acceptance
of the current PR07-C remediation; the final run must record its own immutable
HEAD, PostgreSQL availability, and exact totals.

### Historical 2026-06-28 A-04 auth boundary verification

- Release build completed with 0 warnings and 0 errors.
- `AuthSecurityHttpTests` passed 15/15 after the test harness persisted Data Protection keys to an isolated temp directory instead of the Windows user-profile key folder.
- `TenantIsolation` filtered tests passed 24/24.
- Full backend suite passed 128/128.
- Docker daemon and local PostgreSQL were unavailable on this host, so container/runtime database assertions remain separate evidence work.

### 2026-06-19 backend audit verification

- Release build completed with 0 warnings and 0 errors.
- The audit environment prevented the .NET test runner from opening its local IPC socket, so tests did not execute in that turn.
- Existing test files were inspected for coverage, but the backend audit does not claim a fresh passing test run.
- Detailed regression recommendations are in `docs/BACKEND_LOGIC_AUDIT.md`.

## Test layers

### Unit and service tests

Located under `tests/Coglatas.Tests/`.

Covered areas include:

- auth hashing, invite rejection cases, user status, lockout;
- admin service rules;
- organization authorization;
- projects/tasks;
- forms and events;
- notifications;
- global and conversation-scoped Message notification suppression, including
  mention notifications and current Tenant membership scoping;
- integration/API-token foundations;
- local file storage;
- pagination safety;
- tenancy and save rules.

Most service fixtures use fakes or EF Core InMemory.

### HTTP tests

`AuthSecurityHttpTests`:

- starts Kestrel on an ephemeral local port;
- uses cookie authentication;
- tests CSRF, hidden session ID, revoked/expired sessions, and suspended users;
- uses EF Core InMemory.

`HttpTenantIsolationTests`:

- starts Kestrel;
- uses a test authentication handler;
- exercises controllers, tenant middleware, services, repositories, and core cross-tenant boundaries;
- uses EF Core InMemory, not PostgreSQL and not the production cookie scheme.

### PostgreSQL tests

`PostgreSqlIntegrationTests` checks:

- no pending migrations;
- tenant-scoped repository behavior;
- tenant-scoped search across several result types.

The tests require `POSTGRES_TEST_CONNECTION_STRING`.

PostgreSQL tests use `PostgreSqlFactAttribute` plus `PostgreSqlTestEnvironment.RequireConnectionString()`.
When the variable is absent locally, they are explicitly reported as skipped at discovery;
when `CI=true` or `GITHUB_ACTIONS=true`, the missing variable is a test failure. This
prevents an unconfigured PostgreSQL suite from being reported as a pass.

### Issue #357 Task execution source-scope foundation

The focused backend selection is tagged `Scope=Issue357`. It covers the
Project-default/complete-Task-override inheritance rule, version and
first-override conflict behavior, same-key replay after later policy edits,
immutable run-policy snapshots, safe tenant/authorization hiding, required
audit failure, default and append-only direct-EF guards, controller mapping,
strict JSON, and CSRF-protected mutation. The focused Angular component tests
cover the authorized Task panel/editor, safe errors, protected-state clear,
and HTTP refresh after metadata-only Project/Task invalidations. Static
Playwright coverage is a frontend behavior check only; its API responses are
mocked.

The `TaskExecutionScopeFoundation` migration's PostgreSQL backfill, scope
triggers, and immutable snapshot guards require a configured
`POSTGRES_TEST_CONNECTION_STRING` or authoritative CI evidence. The real
Compose browser smoke does not run an execution provider because none exists;
it cannot prove an outbound-Web or source-material workflow. See
`docs/verification/p0-task-execution-scope-foundation.md` for the current
candidate evidence and limitations.

### Issue #339 Task context summary

Focused Angular coverage derives only a zero-to-two count from the two
server-authorized Issue #357 policy booleans, distinguishes Project default
from Task override, follows authoritative Project/Task invalidation refetches,
and synchronously removes the compact summary on authorization clearing or a
denied refresh. Contract coverage requires the explicit non-inventory label
and rejects source identifiers/details.

The existing Task execution-scope Playwright scenario runs at 320 pixels and
adds keyboard focus transfer from the compact summary to the detailed context,
horizontal-containment, and axe checks. Its API is mocked and does not replace
the existing backend authorization tests. Issue #339 adds no backend route,
schema, provider, or runtime behavior. See
`docs/verification/p1-task-context-summary.md`.

### Issue #330 Workspace Continue working

Focused Angular coverage validates the strict versioned opaque history schema,
Tenant/user/Workspace partitioning, deduplication and caps, exact Project/File
reauthorization, three-request hydration concurrency, server-only metadata,
redaction, status mapping, revoked/mismatched pruning, transient retention with
no stale rendering, storage failure, realtime authorization clearing, catch-up,
generation cancellation, and late-response rejection. Integration specs also
cover Project-detail and parent-Project Task touches plus Files-page and
Task-attachment download timing.

Download tests prove that neither metadata reads nor grant issuance advance File
recency; only successful Blob handoff does. Grant FileObject mismatch is
rejected, raw tokens never enter history or signal state, and grant/Blob success
or denial after a Workspace switch cannot touch another bucket or invoke an
obsolete Task-detail retry. The focused built-app Playwright scenario uses
mocked API responses to check exact Project/File reads, absence of Task reads,
redacted File presentation, grant-backed download, strict stored fields,
capability-gated empty actions, axe, and horizontal containment at 320 pixels.
It proves frontend behavior only, not ASP.NET Core integration.

No schema, migration, API, or provider-backed behavior is introduced. The
candidate reuses existing Project/File detail and download-grant contracts, so
conditional PostgreSQL execution and a new real-backend mutation scenario are
not applicable to the local browser-history behavior. See
`docs/verification/p0-continue-working.md` for exact candidate results and
remaining exact-head gates.

### Issue #349 exact-event Audit metadata disclosure

Focused backend coverage verifies the independent sensitive-metadata
capability is denied before identifier lookup, current-Tenant projection,
generic absent/malformed/cross-Tenant 404 behavior, recursive prohibited-field
removal, canonical response redaction, and absence of metadata from the Audit
grid list and errors. These tests use EF Core InMemory; the change adds no
schema or provider-specific query.

Focused Angular coverage verifies capability-gated presentation, no request
before explicit disclosure, safe text-only JSON rendering, fixed error text,
hide/focus behavior, exact-event request cancellation, and stale-response
rejection. The static Playwright case exercises the same flow at 320 pixels
with keyboard activation, horizontal-overflow checks, and axe. Its responses
are mocked and do not replace backend authorization evidence. See
`docs/verification/p0-audit-sensitive-metadata.md`.

### Issue #356 Files inspector tabs

Focused Angular coverage verifies the single Preview/Details/Activity
inspector, native tab semantics and keyboard movement, Preview reset, essential
versus secondary metadata, absent edit controls, and non-rendering of internal
storage/scan values. The built-app Chromium scenario runs at 320 pixels in both
configured projects and covers focus restoration, horizontal containment, axe,
and the absence of Audit/activity/version requests.

This Issue changes no backend contract. Static Playwright responses are mocked
and do not establish ASP.NET Core integration. The Activity tab remains an
explicit unavailable state until Issue #363 defines a bounded, file-specific,
server-authorized activity/version projection. See
`docs/verification/p0-files-inspector-tabs.md` for exact results and limits.

### Issue #410 canonical Task create

The focused backend selection covers strict canonical request binding,
required idempotency, safe tenant/resource hiding, manager-only initial
assignee and Task-override authority, member/Milestone eligibility, atomic
audit/invalidation staging, and replay after mutable Task, override,
Milestone, and assignee changes. The recorded local run passed 9 tests. The
broader backend suite recorded 951 passed with 242 conditional PostgreSQL
tests skipped because `POSTGRES_TEST_CONNECTION_STRING` was unavailable.

Focused Angular evidence recorded 6 spec files / 38 tests passing under Node
24.19.0. The production Angular build passed after compacting the new
Task-create stylesheet; the Task-create budget warning is gone, while the
repository's pre-existing bundle and unrelated style-budget warnings remain.
A focused 320-pixel Chromium static test passed 1/1 against fresh production
output. It checks keyboard entry, horizontal-overflow absence, strict
canonical body plus CSRF/idempotency headers, and the absence of Start,
runtime, raw-provider, and raw-source controls. Static responses are mocked;
they do not establish browser-to-ASP.NET Core compatibility.

The existing mandatory MVP0 real-backend scenario has source coverage for the
canonical Project Detail-to-Task-create flow, including the exact POST body,
CSRF/idempotency headers, HTTP 201, persisted Task/Brief detail, and no
execution-run request. A local Compose attempt stopped during the Docker
frontend build because `SYNCFUSION_LICENSE` was not configured; no application
container or Playwright assertion ran, and scoped cleanup completed. This is
an environmental startup limitation, not a P0 assertion failure or
real-backend evidence. See `docs/verification/p0-task-create.md` for scope and
limits.

### Issue #359 Message search and quick filters

Focused Angular coverage verifies the exact bounded `type=Message` Search
request, strict result type/identity/internal-route validation, canonical DM
route reuse, removable condition chips, clear-all behavior, zero-result focus
recovery, stale-request cancellation, and fixed error redaction. Issue #355
replaces its initial client-only Conversation conditions with the authoritative
views covered below. The static Playwright scenario uses a 320-pixel viewport
and exercises the mobile disclosure, keyboard submit/toggle/remove/Escape/
focus-return flow, no horizontal overflow, and axe. Its APIs are mocked; the
existing HTTP/PostgreSQL Search tests remain the authorization evidence. See
`docs/verification/p1-message-search-filters.md`.

### Issue #367 advanced Message filters

Tests tagged `Scope=Issue367` cover validation, fixed non-reflective HTTP model
binding failures, current readable-Conversation authorization, cross-Tenant
and cross-Workspace non-disclosure, author-option projection, half-open date
boundaries, exact `(CreatedAt, Id)` read-cursor ties, legacy zero-sequence and
null-ID compatibility, corrupt cursor fail-closed behavior, and canonical safe
attachment `With`/`Without` complements. The PostgreSQL test must run with a
real `POSTGRES_TEST_CONNECTION_STRING`; an environment skip is not provider
evidence.

Focused Angular tests cover compound request mapping, removable chips,
authorized sender resolution, malformed/deferred URL state, private free-text
scrubbing across history, request cancellation/generation guards, same-filter
held-author races, dialog focus containment, and keyboard sender selection.
The static Playwright scenario forces 320 CSS pixels and covers the canonical
query parameters, local-calendar date conversion, chips, Back/Forward privacy,
focus return, no horizontal overflow, and axe. Its APIs are mocked and do not
replace the HTTP/PostgreSQL authorization proof. See
`docs/verification/p2-message-advanced-filters.md`.

### Issue #355 Conversation inbox views

Backend tests tagged `Scope=Issue355` cover All/Unread/Mentions/Later counts
and filtering, the current recursive Conversation-read boundary, nonparticipant
and cross-Tenant non-disclosure, private Later mutation, and independence from
the read cursor. Conditional PostgreSQL cases apply the additive migration over
the previous schema, verify its column/index and Down/reapply path, and execute
the composed unread/Mention/Later predicates with an inaccessible Conversation
present. They require `POSTGRES_TEST_CONNECTION_STRING`; an environment-skipped
local result is not PostgreSQL evidence.

Focused Angular tests cover authoritative count/view mapping, Later mutation
and refresh, independent search-result scope, loading/error/filtered-empty
states, and keyboard operation. The static Playwright flow exercises the
desktop and forced-320-pixel navigation, direct Conversation return, visible
focus, horizontal overflow, and axe. Its API is mocked, so the HTTP and
PostgreSQL tests remain the authorization and persistence evidence.

### Issue #368 saved Message follow-up

Backend tests tagged `Scope=Issue368` cover current-user privacy, sequential
idempotent save/complete, exact timeline and older-reply anchor loading,
deleted/mismatched/cross-Tenant reply-anchor non-disclosure, cross-Tenant and
nonparticipant non-disclosure, revocation after save, list/count
reauthorization, and unchanged read cursors. Conditional PostgreSQL tests
verify the additive table, unique and paging indexes, Down/reapply chain,
production composed readability query, bounded exact-reply projection,
durable-but-hidden revoked rows, and concurrent save/complete reconciliation.
They require
`POSTGRES_TEST_CONNECTION_STRING`; a local skip is not PostgreSQL evidence.

Focused Angular tests cover strict fail-closed DTO mapping, private paging and
completion, the distinct Saved messages surface, exact Message/thread route
parameters, anchored thread request/refresh behavior, truthful bounded-state
copy, and the shared Message More action. The static Playwright case runs at
320 pixels and verifies keyboard operation, 44-pixel controls, exact focus on
an older reply included outside the ordinary latest window, zero read-state
calls, CSRF-protected completion, no horizontal overflow, and axe. Its API is
mocked, so HTTP/PostgreSQL tests remain
the backend compatibility and authorization evidence. See
`docs/verification/p2-message-follow-ups.md`.

### Issue #362 same-Conversation Message threads

Backend tests tagged `Scope=Issue362` cover the exact read/post routes,
root/reply projection, deterministic 100-reply bound, truthful `hasMore`, main
timeline exclusion, first-reply `CanCreateThread`, later posting authority,
read-only access, removed/nonparticipant/admin/cross-Tenant/cross-Workspace/
Project-scope/revoked-membership denial without body/count/name leakage,
idempotency target isolation, metadata-only audit and realtime payloads,
deleted reply/root tombstones, deleted-root list continuity with body and
attachment redaction, and application rejection of cross-Conversation/
cross-Tenant links.

Conditional PostgreSQL coverage applies the additive migration over a legacy
Message, verifies its column/index/check/foreign key and Down/reapply paths,
and runs the production query against canonical, corrupt cross-scope, and
deleted rows. The provider query retains a deleted root with a durable
same-Conversation reply while omitting an ordinary deleted Message. It also
proves that the provider caps participant rows at three per root and that
concurrent independent-context same-key commits return one
Message while rolling back the loser's audit/notification/outbox rows; an
unrelated database constraint still propagates. It requires
`POSTGRES_TEST_CONNECTION_STRING`; absence outside CI is an explicit
environment limitation, not PostgreSQL pass evidence.

Focused Angular coverage validates strict bounded DTO mapping, separate main
and thread drafts, stable retry identity, reply-event timeline exclusion,
authoritative participant-summary refetch with out-of-order protection,
protected-state clearing, channel/DM wiring, durable tombstones, keyboard
operation, trigger focus return, no focus theft across load completion,
local/realtime/reload deleted-root continuity, out-of-order delete/summary
reconciliation, transient revalidation safety, and
POST-400 draft/retry preservation followed by an authorized GET revalidation.
A denied revalidation clears the full protected projection. The dedicated
static Playwright scenario
uses a 320-pixel viewport and checks the mobile pane, horizontal overflow,
keyboard post/close, CSRF/body shape, draft isolation, and axe. Its API is
mocked and therefore does not replace the HTTP or PostgreSQL tests. See
`docs/verification/p1-message-thread-context.md` for the exact current run
record and environmental limits.

### Issue #354 advisory Task-create quality checklist

The focused Task-create page component suite covers missing optional Brief
items, native focus movement to Goal/Deliverable/Constraints, complete trimmed
Brief values, inherited and manager-selected effective source-policy display,
and non-blocking create with optional values absent. The recorded focused run
passed 11 tests under Node 24.19.0; application and spec TypeScript
compilation also passed.

The production Angular build passed without adding a Task-create component
style-budget warning. The existing forced-320-pixel Task-create Playwright
scenario passed in Chromium desktop and mobile. It keyboard-activates a missing
Goal action, verifies focus, fills the Brief, verifies the compact 4/4 review,
and retains its no-horizontal-overflow and axe checks. Static responses remain
mocked and cannot prove server authorization or persistence.

The mandatory real-backend MVP0 scenario is extended in source to inspect the
server-returned effective policy, exercise the native focus action, verify the
4/4 advisory state, and then persist the canonical Task/Brief. It remains
subject to the protected Compose/CI real-backend gate; it must never be treated
as proof of a Web/provider/runtime workflow. See
`docs/verification/p0-task-quality-checklist.md` for scope and limitations.

### TASK-V1-PR07-B immediate notification tests

PR07-B adds focused service/contract tests for the exact recipient matrix,
pre-mutation assignee capture, actor suppression, mandatory-versus-Watch
independence, duplicate relationships, direct-mention validation, Important
comment union, and ordinary-comment no-notify-all behavior. Command tests cover
assignment/Reviewer/Blocked/review/deadline integration, compatibility-route
normalization, default-off rollout behavior, safe semantic payloads, and the
separation of Gantt planned dates from hard `DeadlineAt`.

The deadline classifier suite fixes one `now` instant and exercises null/value,
23h59m/24h, local Today, local Overdue, and timezone-conversion boundaries.
Request/HTTP contract tests distinguish an omitted deadline from explicit null,
reject client significance fields, and prove the schedule request cannot own a
hard deadline.

Conditional PostgreSQL PR07-B tests are the authoritative transaction and
concurrency evidence. They must run with
`POSTGRES_TEST_CONNECTION_STRING` and cover one committed Task mutation,
relationship/Audit/Notification/business-Outbox/signal-Outbox unit; stale and
authorization zero-row outcomes; injected Task-save, Audit, and Outbox failures; retry;
and concurrent writers leaving one visible logical Notification. A local run
without the variable is not PostgreSQL evidence. Exact commands and final
counts are recorded in
`docs/verification/task-v1-pr07-b-immediate-notifications.md`.

The PR07-B relationship-target regression set suspends a `WorkspaceMember`
while retaining the User and `ProjectMember` records. Canonical
assignee/reviewer/collaborator and compatibility Assignee/Reviewer/Support
changes must reject before save, leaving Task, relationship, Watch,
Notification, NotificationUserState, Audit, and Outbox state unchanged.
Authorized cleanup of an already-revoked relationship remains permitted. The
focused TRX manifest is `scripts/ci/task-pr07b-required-tests.txt`; record its
active and matched counts from the final exact-HEAD run.

The TaskComment remediation additionally requires focused unit and HTTP proof
that revoked comment authors cannot PATCH or DELETE by stored comment ID,
current authors and Managers retain their permitted updates, and archived or
deleted parents deny changes without a persistence delta. Its safety coverage
uses the real `InMemoryCommunicationSafetyGuard` to prove Important-only
`false -> true` updates consume the post window, return
`TASK_COMMENT_RATE_LIMITED` with positive retry metadata when limited, and do
not double-charge a combined body/Important mutation. Mention tests must prove
that revoked Workspace members with stale ProjectMember/GroupMember rows are
absent from candidate search and that an authorized/unauthorized mixed mention
rejects the full command without staged notification work. The conditional
PostgreSQL suite must prove the same denied paths leave Task/comment versions,
AuditLog, Outbox, Notification, and NotificationUserState unchanged.

### TASK-V1-PR07-C Workspace deadline-digest tests

PR07-C tests are tagged `Scope=TaskV1PR07C` and split by the boundary they
prove:

- `TaskDeadlineDigestPolicyTests` pins policy version 1, exactly three
  automatic attempts, exact quarter-hour validation including `00:00` and
  `23:45`, Workspace-local classification for three days/one day/today/
  overdue, timezone conversion, DST gap/fold behavior, and stable daily
  logical identity.
- `TaskDeadlineDigestServiceTests` covers default-off behavior, feature-disable
  fenced claim release, multiple Workspaces/timezones for one user, bounded
  schedule/claim/candidate paging, one normal in-transaction candidate-page
  enumeration plus bounded lock/rechecks, fence-retry re-evaluation only when
  required, timezone
  change, zero-candidate success, logical Notification identity, failure
  transitions, and cancellation propagation.
- `TaskDeadlineDigestWorkerTests` executes public `RunOnceAsync` through scoped
  DI. It proves Tenant and per-claim failure isolation, cancellation before
  not-yet-started work, immediate concurrent start of every claim in the
  bounded batch, Tenant page bound 100, schedule bound 500, claim/concurrency
  fan-out bound 100, and structured-log privacy with no exception details or
  Tenant/user/Workspace/Task/job/claim IDs. This is application scheduling
  coverage only; it does not establish PostgreSQL lock compatibility or
  database-level parallel progress.
- `DbNotificationDigestStagingTests` verifies the generic null-body digest,
  minimal recipient-only signal, no implicit save, and logical retry dedupe.
- `TaskDeadlineDigestAdminServiceTests` covers active system-administrator and
  Tenant scope, bounded reason validation, restart outcome mapping, delegated
  audit inputs, and process diagnostic accounting.
- `TaskV1Pr07CDeadlineDigestPostgreSqlTests` is provider-authoritative for the
  focused fresh/upgrade/Down/re-upgrade migration, five-field uniqueness,
  due/claim-expiry `EXPLAIN (ANALYZE, BUFFERS)` partial-index selection, bounded
  candidate list pages and their current-state fence rechecks, integrated DST
  gap/fold scheduling and uniqueness, concurrent `SKIP LOCKED` claims, claim expiry/token fencing,
  exact third automatic terminal failure, feature-disable claim release and
  old-token fencing, schedule-upsert no-op/meaningful-write behavior, and
  append-preserved audited operator restart.
- `TaskV1Pr07CDigestCandidateAtomicityPostgreSqlTests` uses real PostgreSQL for
  current candidate relevance and commit atomicity. It distinguishes current
  Creator/Primary-Assignee/Reviewer/Collaborator/manual Watch from opt-out,
  visibility-only, Team Queue-only, and restricted-group unauthorized cases;
  preserves current authorized roles and non-archived Project states; removes
  revoked, archived, deleted, completed, cancelled, opted-out, or
  relationship-lost candidates; verifies final-evaluation races for membership
  revoke, Workspace/Project archive, Task completion, Watch opt-out, and
  relationship removal; and proves that stale Notification, Outbox, and state
  advances cannot commit. It also covers all four categories producing one
  generic Notification/state/minimal user Outbox signal and `Succeeded` ledger,
  zero-candidate no-op, logical-key retry, post-save rollback, and concurrent
  same-user digests across Workspaces and timezones with serialized state
  versions. Its PostgreSQL gate/interceptor cases additionally prove
  `DifferentUsersInSameTenantGenerateConcurrently`,
  `DifferentUsersInSameWorkspaceDoNotShareExclusiveFence`,
  `DifferentWorkspacesInSameTenantDoNotShareExclusiveFence`,
  `SlowFirstClaimDoesNotExpireLaterSameTenantClaims`,
  `SameRecipientStillSerializesNotificationStateVersion`,
  `SlowFirstSameRecipientClaimDoesNotExpireQueuedClaim`,
  `SameRecipientWaitingClaimIsSkippedByExpiryScanner`,
  `SameRecipientQueuedClaimKeepsAutomaticAttemptBudget`, and
  `ClaimLostBeforeTransactionFenceStagesNothing`,
  `ConcurrentTenantMutationWaitsForGenerationFence`,
  `ConcurrentFeatureDisableWaitsOrPreventsDigestCommit`, and
  `MissingWatchRowOptOutInsertCannotBypassFence`. These tests must observe
  candidate evaluation and commit completion across real PostgreSQL locks;
  the same-recipient lease cases additionally prove that B has locked its own
  Job/Attempt before reaching the shared User lock, that a post-expiry
  `FOR UPDATE SKIP LOCKED` probe returns no claim and leaves B `Claimed` with
  its original token and one automatic attempt, and that release of A lets
  both units succeed at state versions 1 and 2. Asserting only that two `Task`
  instances began, or only their final state, is insufficient.
- The feature-disable gate exercises TenantSettings, active Subscription, and
  Plan feature sources (including an absent-TenantSettings insert); the absent
  Watch gate inserts through `SaveChanges` to exercise the stable Task pivot.
- Every PR07-C concurrency test carries `Trait("Scope", "TaskV1PR07C")` and
  `Trait("Category", "PostgreSQLIntegration")`; the required-test manifest
  may name only the tests that exist in that fixture.
- `TaskV1Pr07CNotificationVersionConcurrencyPostgreSqlTests` makes the existing
  `NotificationUserState.Version` an asserted EF concurrency token and races a
  digest with an immediate Task Notification. One version-1 unit of work
  commits, one rolls back with `DbUpdateConcurrencyException`, and a clean
  logical-key retry leaves exactly two Notifications/signals at versions 1/2.

Run the focused scope locally with:

```powershell
dotnet test tests/Coglatas.Tests/Coglatas.Tests.csproj `
  --filter "Scope=TaskV1PR07C"
```

Provider evidence requires a disposable PostgreSQL connection:

```powershell
$env:POSTGRES_TEST_CONNECTION_STRING = '<disposable PostgreSQL connection string>'
$env:ConnectionStrings__DefaultConnection = $env:POSTGRES_TEST_CONNECTION_STRING

dotnet test tests/Coglatas.Tests/Coglatas.Tests.csproj `
  --configuration Release `
  --filter "Scope=TaskV1PR07C"
```

If `POSTGRES_TEST_CONNECTION_STRING` is absent outside CI, the PostgreSQL cases
are reported as skipped. A green run with those skips proves only pure/service/
worker behavior. It is not evidence for migration, PostgreSQL DST/idempotency,
locking, index plans, current repository authorization, or transaction
atomicity. The small candidate fixture proves bounded command shape, not a
production-volume Task-deadline index plan; no speculative deadline index was
added. Exact worktree evidence and limitations are recorded in
`docs/verification/task-v1-pr07-c-deadline-digest.md`.

PR07-C changes no Angular or SignalR route behavior. Mocked Playwright cannot
substitute for the PR07-D real-backend dispatch/open/reconciliation work, and
PR07-C tests must not be described as proving that later scope.

CI runs the same scope after the Release build against its PostgreSQL 18
service, writes `task-pr07c-acceptance.trx`, and validates zero failure/error/
timeout/abort/not-executed/skipped outcomes plus every active name in
`scripts/ci/task-pr07c-required-tests.txt`. The manifest is a coverage guard;
its active and matched counts must be taken from the immutable final-HEAD TRX,
not from this source record. The same-recipient lease additions raise the
active required-test set from 72 to 76 names; the strict verifier must match
all 76 after the final PostgreSQL run.

Historical source records reference an earlier PR07-C candidate
`8545ae7ab8ecc3feb6d0bbe278ecfe81f217ba31`. That evidence predates the
same-Tenant concurrency remediation and is not acceptance evidence for this
worktree. Do not carry its test counts, manifest counts, branch SHA, or hosted
check state forward. The final immutable HEAD and every result must be recorded
only after this remediation's required commands run.

### TASK-V1-PR07-D authorized delivery and Angular reconciliation tests

PR07-D tests carry `Trait("Scope", "TaskV1PR07D")`. The focused server suite
exercises current Task/digest, Artifact/Project, and Message/recursive-
Conversation target resolution for NotificationCreated,
recipient-only read-state routing, Task invalidation routing, Task/digest
open results, unavailable/read-state ordering, state-version advancement, and
metadata-only read-state Outbox staging. The required name manifest is
`scripts/ci/task-pr07d-required-tests.txt`; CI runs the scoped suite after the
Release build, writes `task-pr07d-acceptance.trx`, and its strict verifier
rejects missing, duplicate, failed, skipped, aborted, or not-executed required
tests.

The permanent Artifact and Message regressions prove authorized routes and
content first, then revoke Workspace/Project or ancestor-Conversation access
and assert list/total/unread exclusion, mutation/open denial, no protected
state mutation, and created/read-state delivery suppression. PostgreSQL
coverage proves EF translation for batched Artifact visibility and the
set-based recursive Message boundary. The strict PR07-D manifest contains 34
required names.

Authoritative PR07-D completion also requires provider-backed PostgreSQL and
hosted HTTP/dispatcher evidence for revoke-before-delivery, replay-after-revoke,
notification ownership/tenant isolation/soft delete, Outbox terminal
suppression, Workspace archive rollback atomicity, and the open endpoint.
In-memory tests do not establish those database/host guarantees.

Angular unit coverage validates reference-only Notification list refetch
coalescing, stale version rejection, backend-authoritative open/unavailable
behavior, digest Workspace context handoff, authorization clear-before-catch-up,
and server-authoritative preference conflict/validation behavior. It must be
supplemented by the normal production build, architecture/type checks,
Storybook, Playwright smoke, and Linux Docker screenshot run. Mocked browser
responses are not backend authorization evidence.

### Browser UI tests

Root Playwright infrastructure remains under `tests/ui`, but the legacy static-SPA specs have been marked obsolete after the MVP-A P0 Angular migration.

Angular component coverage for Message settings verifies the separate global
and conversation endpoints, stale response rejection, confirmation before a
global save, Escape cancellation/focus behavior through the shared dialog, and
browser-only unread-badge presentation. This mocked coverage does not replace
the PostgreSQL preference-store or server authorization tests.

- `tests/ui/serve-static.mjs` serves Angular build output from `frontend/dist/coglatas-web` by default;
- legacy vanilla-SPA mocked API fixtures were removed;
- run desktop and mobile Chromium projects;
- use axe for accessibility checks.

The current skipped legacy spec is not acceptance evidence. Add new Angular-facing specs after frontend dependencies, routes, and selectors are intentionally defined.

They do **not** verify:

- the ASP.NET Core host;
- authentication cookies;
- CSRF integration with the real backend;
- serialized DTO compatibility;
- controller routes or authorization;
- PostgreSQL behavior.

### Static Angular Playwright suite

Run the frontend/static suite with:

```powershell
npm.cmd run test:ui:angular
```

`npm test`, `npm run test:ui`, and `npm run test:ui:angular` run
`angular-smoke.spec.ts`, `message-mobile-navigation.spec.ts`,
`message-actions.spec.ts`, `message-thread-context.spec.ts`, and `app.spec.ts`
against the static Angular test server. The Issue #357 responsive Task
execution-scope scenario is included in
`angular-smoke.spec.ts`, so it runs in both desktop and 320-pixel mobile
projects. Their API responses are mocked. They intentionally do not discover
or execute `real-backend-smoke.spec.ts`.

### Issue #587 browser-engine compatibility

The ordinary static Angular suite remains Chromium desktop/mobile coverage.
The separate COMPAT-01 PR matrix enables `chromium-desktop`,
`firefox-desktop`, and `webkit-desktop` only for the small COMPAT-04
`browser-engine` profile:

```bash
npm run test:ui:compat-critical -- --profile browser-engine -- --project=firefox-desktop
npm run test:ui:compat-critical -- --profile browser-engine -- --project=webkit-desktop
```

The common runner forces `TZ=UTC`, the deterministic compatibility browser
context, and `--retries=0`. GitHub Actions executes each engine as an
independent job and stores an engine-named Playwright HTML/JUnit artifact. A
successful Chromium job cannot override a Firefox or WebKit failure. These
static browser runs use mocked API contracts and require no protected secret or
Syncfusion license activation; they do not establish ASP.NET Core/PostgreSQL
integration.

WebKit coverage is engine compatibility evidence only. It is not real Safari,
Apple-device, or iOS certification.

### MVP0 real-backend browser smoke

Run the self-contained real-host smoke with:

```powershell
npm.cmd run test:ui:real-backend
```

The runner validates Compose, starts an isolated PostgreSQL volume, applies EF
Core migrations, builds and starts the ASP.NET Core image with the production
Angular build, enables deterministic synthetic seed data, waits for
`/health/ready`, and runs Playwright inside the Compose network against
`http://coglatas-backend:8080`. The alias intentionally avoids the HSTS-preloaded
`.app` hostname used by the Compose service name. The legacy regression phase
preserves traces, screenshots, videos, HTML reports, and the smoke
error-context attachment on the host when it fails. Migrated Functional owners
currently emit list/JUnit output with their high-risk browser artifacts disabled;
FCI-09 owns the later sanitized artifact policy. Containers, networks, and the
isolated test volumes are removed afterwards.

The runner prefers `docker compose` on Windows, Linux, and macOS, and falls
back to legacy `docker-compose` only when necessary. Do not point this suite at
the static server on port 4173.

The default browser phase invokes migrated `tests/functional/` owners with
`playwright.functional.config.ts`, then invokes the legacy
`tests/ui/real-backend-smoke.spec.ts` regression with the root Playwright
config. Keeping those discovery roots separate is required: Playwright file
arguments are scoped to the active `testDir`. Neither invocation uses
`--pass-with-no-tests`, so a missing owner selection is a failure.

For an already-running real backend only, direct execution requires the marker,
URL, and synthetic credentials explicitly:

```powershell
$env:COGLATAS_REAL_BACKEND_SMOKE = "1"
$env:PLAYWRIGHT_BASE_URL = "http://127.0.0.1:8080"
$env:COGLATAS_BROWSER_SMOKE_EMAIL = "e2e-user@example.test"
$env:COGLATAS_BROWSER_SMOKE_PASSWORD = "E2eSmoke!23456"

node tests/ui/run-real-backend-playwright.mjs
```

The normal `dotnet run` launch profile uses port 5098; the Compose application
uses internal port 8080. Direct execution does not start ASP.NET Core for you.

### Public HTTPS production Golden Path

Issue #481 adds a separately protected release gate for a deployed public
origin. It is not part of the static or Compose suites and must not be pointed
at localhost, Kestrel, or a private proxy port. See
`docs/verification/p0-public-https-golden-path.md` for the full synthetic
fixture contract and security behavior.

After deployment, run the manual **Public HTTPS Production Golden Path** GitHub
Actions workflow. It reads only protected `public-https-gate` environment
values and fails closed when the public HTTPS endpoint or its fixture is not
available. The test checks `/health/ready` only as a bounded preflight before
the real browser journey; readiness never constitutes a pass on its own.

The equivalent controlled operator command is:

```bash
npm run test:ui:public-https
```

The command requires `COGLATAS_PUBLIC_HTTPS_SMOKE=1`,
`COGLATAS_PUBLIC_SMOKE_SYNTHETIC_FIXTURE=1`, a root public
`COGLATAS_PUBLIC_SMOKE_URL`, synthetic `@example.test` credentials, and the seven
authorized/denied fixture IDs documented in the verification record. Public
mode disables Playwright trace, screenshots, video, HTML, JUnit, and test
attachments; no credentials, cookies, CSRF values, response bodies, or fixture
identifiers are written to a CI artifact.

### Node DEP0205 note

Historical static-suite evidence shows `[DEP0205] module.register()` only after
Playwright starts its workers. It is not emitted by the installed Playwright
CLI version command, and no project source calls `module.register()`; it is
therefore inferred to be a Playwright worker loader or transitive dependency,
not an API/backend failure. Leave the warning visible and do not suppress it
globally. Capture a deprecation trace under the supported Node version before
changing a direct dependency.

## CI

`.github/workflows/ci.yml`:

- starts PostgreSQL 18;
- restores and builds .NET;
- applies EF migrations;
- sets `POSTGRES_TEST_CONNECTION_STRING`;
- runs .NET tests;
- installs Node dependencies;
- runs Angular Playwright smoke in the pinned Linux Docker runner;
- runs Gitleaks, .NET package reports, Compose validation, Docker build, and Trivy.

This is configuration evidence. Check the actual GitHub Actions run before claiming a branch is green.

### Issue #590 OS portability matrix

`.github/workflows/os-portability.yml` runs one bounded toolchain/build/test contract on GitHub-hosted Ubuntu, Windows, and macOS. Each matrix leg uses `global.json`, Node 24, the repository-declared npm version, root and active-frontend lockfiles, Release .NET and Angular builds, the explicit DB-independent `Portability=CrossPlatform` .NET subset, Angular unit/helper tests, and COMPAT-04 `os-portability` discovery.

The portable TRX verifier rejects zero, below-minimum, skipped, failed, or incomplete .NET selections. Matrix legs are not ignored with `continue-on-error`, and `fail-fast: false` preserves a distinct outcome/evidence artifact for every OS. PostgreSQL, Docker, real-backend, and browser-engine execution remain in their Linux or COMPAT-01 owning lanes; they are intentionally not multiplied into an OS x browser x database product. See `docs/ci/os-portability.md` for the exact support boundary and shell/path/case/line-ending classification.

Angular screenshot baselines remain strict. Local Windows/macOS Playwright runs
are diagnostic only; baseline approval and CI-parity reruns must use the pinned
Linux Docker runner via `npm run test:ui:angular:docker`.

## Commands

All .NET tests:

```bash
dotnet test Coglatas.slnx
```

Tenancy-focused:

```bash
dotnet test Coglatas.slnx --filter 'FullyQualifiedName~Tenancy'
```

PostgreSQL category:

```bash
POSTGRES_TEST_CONNECTION_STRING='<test connection string>' \
dotnet test Coglatas.slnx --filter 'Category=PostgreSQLIntegration'
```

UI:

```bash
npm ci
npx playwright install --with-deps chromium
npm --prefix frontend ci
npm --prefix frontend run build
npm run test:ui
```

Runner helper tests:

```bash
npm run test:ui:real-backend:runner
```

Linux screenshot parity:

```bash
npm run test:ui:angular:docker
```

Issue #344 Audit filter evidence is concentrated in the AuditGrid tenancy
tests, `frontend/src/app/features/admin/admin-ui.spec.ts`,
`frontend/src/app/features/admin/audit-view-preference.service.spec.ts`, and the
320px keyboard/axe Audit-filter flow in `tests/ui/angular-smoke.spec.ts`. A
mocked browser filter response demonstrates UI behavior only; server predicate,
count, and negative-authorization claims require the backend tests.

Use the Linux runner for screenshot baseline approval. Do not approve
Windows/macOS host-native screenshots as authoritative baselines.

Compose syntax:

```bash
docker compose -f infra/compose/dev/db.yml config --quiet
docker compose -f infra/compose/dev/full.yml config --quiet
docker compose -f infra/compose/dev/playwright.yml config --quiet
docker compose -p coglatas-real-backend-smoke-config -f infra/compose/test/real-backend-smoke.yml config --quiet
DB_PASSWORD=validation_only docker compose config --quiet
docker compose -f infra/compose/dev/local.yml config --quiet
DB_PASSWORD=validation_only docker compose -f deploy/onprem/compose.yml config --quiet
DB_PASSWORD=validation_only docker compose -f deploy/onprem/compose.yml -f deploy/onprem/compose.ci.yml config --quiet
```

CI also starts the on-prem PostgreSQL and one-shot `migrate` services under an
isolated Compose project and requires the migration container to exit
successfully. Its CI-only override uses Docker's built-in bridge and a local
loopback connection, so cancelled jobs do not consume another per-project
subnet. It does not alter the production on-prem topology, build, or start the
licensed production app image; the full TLS-proxy startup remains environment
evidence. That CI-only check uses a dummy database and does not start the app;
the normal cleanup trap removes its scoped containers and volumes, while a
force-cancelled runner can still leave those non-network resources behind.

## Coverage gaps

High priority:

- scoped announcement visibility across workspace, group, public-channel, private-channel, and confidential-channel scopes;
- search authorization parity with normal project and comment authorization;
- PostgreSQL conversation creation and message attachment persistence;
- EF-backed post update/delete/pin persistence;
- assignee-filtered task queries against a real scoped `DbContext`;
- file cleanup when database/audit/notification persistence fails;
- conversation read-state message ownership;
- distinct My Tasks rows for users with multiple assignment roles;
- notification title/body boundary handling;
- HTTP status-code and shared error-response contract tests;
- first-admin/bootstrap workflow;
- successful invite acceptance creating tenant/workspace membership;
- frontend/backend DTO contract tests;
- cookie-authenticated tenant isolation against PostgreSQL;
- clean-volume on-prem app startup behind the intended TLS/reverse-proxy topology;
- reverse-proxy/forwarded-header behavior;
- object storage when implemented;
- backup/restore rehearsal.

Medium priority:

- route parent-child mismatch rejection;
- explicit nullable-field clearing for PATCH requests;
- concurrent artifact-version allocation and event-capacity enforcement;
- maximum-length, invalid-enum, empty-GUID, null-collection, malformed-JSON, and reversed-date-range tests;
- missing physical-file behavior;
- DI scope-validation smoke tests resolving all controllers and services;
- real API smoke execution from `docs/API_SMOKE_TESTS.http`;
- feature/platform configuration enforcement;
- forms/events/workspaces/groups/channels browser workflows;
- file MIME rejection at service level;
- API error contract/status consistency;
- accessibility coverage beyond four mocked UI scenarios.

## Interpreting results

- “123 tests passed” is not equivalent to “PostgreSQL tests executed.”
- “UI tests passed” is not equivalent to “frontend integrates with backend.”
- “Compose config validates” is not equivalent to “containers start.”
- “Readiness is healthy” is not equivalent to “a user can log in.”
- “CI configuration includes a check” is not equivalent to “the latest run passed.”
