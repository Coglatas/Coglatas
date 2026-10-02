# Operations

This is the active runbook for smoke tests, backup, restore, production checks, and incidents. Deployment setup lives in `docs/DEPLOYMENT.md`.

Fresh environments can bootstrap the first administrator by setting `COGLATAS_SEED_ADMIN_ENABLED=true` plus the `COGLATAS_SEED_ADMIN_EMAIL`, `COGLATAS_SEED_ADMIN_USERNAME`, and `COGLATAS_SEED_ADMIN_PASSWORD` variables for startup. `PlatformAdminSetupMode` still does not create an administrator.

## Readiness Rule

Do not treat an environment as pilot-ready until these pass in that environment:

- App starts.
- `/health/live` and `/health/ready` pass.
- Migrations have applied.
- Login works.
- Tenant resolution works.
- TenantA/TenantB isolation is verified.
- File upload/download authorization works.
- Backup and restore drill is recorded.

## Smoke Test

Run this before a local demo, internal pilot handoff, or on-prem school demonstration.

1. Start PostgreSQL and the app.
2. Apply migrations.
3. Check `/health/live` and `/health/ready`.
4. Sign in as an already provisioned PlatformAdmin. On a fresh installation, first run the explicit startup seed and then disable it unless continued reconciliation is intentional.
5. Create or verify a tenant.
6. Add a tenant owner/admin.
7. Sign in as tenant admin.
8. Confirm tenant admin cannot call `/api/platform/*`.
9. Confirm a TenantA user cannot access TenantB records by URL or API.
10. Invite/register a normal user.
11. Verify a current Tenant Owner/Admin or an explicitly delegated
    Tenant-scoped `workspace.create` user receives `canCreate=true` and can
    create one Workspace. Confirm the creator is its Owner, exactly one
    canonical `WorkspaceGeneral` Conversation exists, and replaying the same
    normalized command with the same `Idempotency-Key` does not duplicate it.
    Confirm an ordinary Member and Platform/SystemAdmin alone remain denied.
12. Verify canonical Workspace-scoped Project create and explicit Project
    activation. Confirm deprecated unscoped `POST /api/projects` still returns
    503 without mutation.
13. Using authorized Workspace/Project data, create or verify a group, channel,
    post, task, and comment.
14. Upload a valid file and download it through an authorized user.
15. Try invalid extension, invalid MIME type, oversized upload, and unauthorized download.
16. Trigger a notification and verify another user cannot mark it read.
17. Stop and restart the app; verify login, tenant context, uploaded files, and project/task data persist.

OnPremSingleTenant checks:

- `Tenancy:AppMode=OnPremSingleTenant`.
- Configured default tenant exists.
- Tenant switching is hidden/disabled.
- Local file storage root is writable and backed up.
- Plans/subscriptions are treated as license/configuration data, not payment.

SaaS checks:

- Tenant resolution uses host/subdomain/session outside development.
- Development tenant header is disabled in production.
- PlatformAdmin and TenantAdmin separation works.
- Suspended tenants cannot resolve normal tenant routes.

## Production Checklist

- HTTPS enabled at reverse proxy or hosting layer.
- Secure cookies enabled in production.
- CSRF protection enabled for cookie-authenticated unsafe browser requests.
- Data Protection keys persisted outside the container/process.
- Production connection string configured through environment variables or secrets.
- File storage path, volume, or bucket configured and backed up.
- Database backup configured.
- Restore tested.
- Evidence of the exact administrator provisioning method and confirmation that bootstrap credentials were not committed.
- Default passwords removed.
- Allowed upload extensions and MIME types reviewed.
- Upload size reviewed.
- Audit logs enabled.
- Error responses do not expose stack traces.
- CORS reviewed.
- Rate limiting reviewed for login, invite, file upload, search, and token endpoints.
- Server firewall and reverse proxy configured.
- Raw passwords, tokens, invite tokens, signed URLs, and message/file contents excluded from logs.

## Durable announcement publication operations

The #378 `AnnouncementPublisherWorker` is an in-process hosted worker. It is
the only component that changes a due `AnnouncementDraft` from `Scheduled` to
`Published` and creates the actual `Announcement`; an accepted **Publish now**
request is first a due-now Scheduled record. PostgreSQL provides the durable
claim boundary, so do not add a separate queue service for this workflow.

The worker pages active Tenants, establishes each Tenant scope explicitly,
claims a bounded batch with an expiring version-fenced lease, and processes
each claim in a fresh DI scope. It reauthorizes the persisted author and
audience immediately before the publish mutation. Lost authorization, invalid
content, or an expired announcement remains Scheduled with a bounded retry
code and delay; it must not create a partial Announcement or emit recipient
notifications.

`AnnouncementPublisher` configuration defaults are:

| Setting | Default | Accepted range |
| --- | ---: | --- |
| `PollSeconds` | 30 | minimum 1 |
| `TenantPageSize` | 25 | 1–100 |
| `ClaimBatchSize` | 10 | 1–50 |
| `ClaimTimeoutSeconds` | 120 | minimum 1 |
| `RetrySeconds` | 300 | minimum 1 |

Do not diagnose a delayed scheduled announcement from client UI state. Inspect
the durable record under its authorized Tenant boundary, verify current
author/audience authority, then check safe worker logs. Logs intentionally use
fixed messages and omit draft IDs, names, recipients, bodies, claim tokens,
and exception details.

## TASK-V1-PR07-C deadline-digest operations

The deadline digest is an in-process hosted worker. It uses the dedicated
digest ledger for schedule, claim, retry, and terminal generation state; the
Transactional Outbox begins only after a non-empty generic Notification is
staged. Diagnose these state machines separately.

### Configuration and rollout

`tasks.notificationsV1` is a database-backed per-Tenant feature and remains
default off. Use the existing reviewed tenant-feature process to enable it;
`Features:*` appsettings are not its source of truth. A disabled Tenant receives
no digest schedule upsert, claim, or generation, although the hosted worker
continues bounded active-Tenant discovery so it can evaluate each Tenant's
flag. Disabling the feature does not remove existing ledger rows and does not
stop dispatch of Outbox rows that were already committed.

If a generator observes the flag disabled after it has claimed a job, it uses
the claim token to release that claim immediately. An automatic claim returns
the job to `Pending`, clears both claim records, restores `AttemptCount` and
`AutomaticAttemptCount`, and completes that attempt as `Deferred`. An operator
restart returns the same audited attempt to `Pending` without adding another
restart row or changing the automatic budget. The release creates no
Notification or Outbox row. Treat an old token after release as claim loss;
never try to complete, defer, or fail it manually.

Current `TaskDeadlineDigest` defaults are:

| Setting | Default | Runtime boundary |
| --- | ---: | --- |
| `PollSeconds` | 60 | minimum 1 second |
| `TenantPageSize` | 25 | 1-100 |
| `SchedulePageSize` | 100 | 1-500 |
| `ClaimBatchSize` | 20 | 1-100; bounded per-Tenant claim fan-out, not a database-concurrency guarantee or ceiling |
| `CandidatePageSize` | 100 | 1-500 |
| `ClaimTimeoutSeconds` | 120 | minimum 1 second |
| `RetrySeconds` | 60 | minimum 1 second |

Do not increase these values as an ad hoc backlog fix. Candidate-index and
production-volume plan evidence is still environment-specific, and the worker
is intentionally bounded.

After a Tenant's bounded claim query completes, the worker starts every leased
claim immediately in one `Task.WhenAll`. Each claim receives a separate DI and
Tenant scope, so one slow recipient does not leave later leases idle and an
ordinary per-user/Workspace failure does not stop its peers. Increasing
`ClaimBatchSize` therefore raises application fan-out and potential database
load, but `Task.WhenAll` alone is not evidence that PostgreSQL generations
run concurrently. The actual database behavior is governed by the generation
fence: shared reader locks allow different recipients in one Tenant or
Workspace to progress together, while the recipient User `FOR UPDATE` lock
serializes only same-recipient Notification-state work. Shared host cancellation
is propagated to every claim.

### Generation fence and lock waits

The digest never takes a Tenant-wide or Workspace-wide exclusive generation
lock. At transaction start it locks the claimed Job `FOR UPDATE`, then the
claimed Attempt `FOR UPDATE`, and verifies original token/status plus
Tenant/User/Workspace/Job/trigger identity. Before final recheck and commit it
then takes `FOR SHARE` locks in this fixed order: Tenant, TenantSettings,
active Subscription(s), Plan source(s), TenantUser, Workspace,
WorkspaceMember, Project/Group membership rows, and candidate
Task/WorkflowStage/Watch/Collaborator rows. It locks recipient User `FOR
UPDATE` between Plan and TenantUser. IDs within a resource type are ordered
ascending. PostgreSQL shared readers coexist; normal lifecycle, authorization,
and feature-source writers wait on conflicting update/delete locks until the
digest commits.

The Job/Attempt locks remain held while a same-recipient transaction waits for
the User row. Claim-expiry recovery retains `FOR UPDATE SKIP LOCKED`, so it
skips that live queued claim rather than consuming an attempt. Only process
crash, connection loss, or rollback releases the lock and permits normal
expiry recovery; do not add a heartbeat, lease extension, or longer timeout.

The writer side also takes a stable parent `FOR UPDATE` pivot for optional-row
mutations, so an absent child cannot bypass the reader fence: Tenant for
TenantSettings/Subscription, Workspace for WorkspaceMember, Project for
ProjectMember, Group for GroupMember, and Task for Watch/Collaborator. This is
the phantom policy; do not replace it with a digest-only advisory lock.

`tasks.notificationsV1` is evaluated from the shared-fenced TenantSettings,
active Subscription, and Plan sources immediately before a visible commit. A
feature change either waits for the digest or makes the generator retry/release
without committing stale Notification, Outbox, or user-state work. Ordinary
shared-lock waiting is not a generation failure. Do not hide contention by
reducing the batch to one, serializing `Task.WhenAll`, extending
`ClaimTimeoutSeconds`, or increasing polling intervals. A later same-Tenant
claim that expires merely because an unrelated generator holds its fence is a
correctness regression to investigate.

Before enabling a Tenant, verify its Workspace timezones and stored/inherited
quarter-hour preferences. Invalid timezone identities fall back through the
implemented Workspace -> Tenant -> UTC chain and increment an aggregate
diagnostic; invalid stored local times are skipped and increment their own
diagnostic. Investigate and repair the source data rather than treating those
counters as normal delivery.

### Health and diagnosis

`GET /health/task-deadline-digests` reports aggregate ledger counts and oldest
due/claimed timestamps plus process-local worker counters. It contains no
Tenant, Workspace, user, Task, Notification, job, or claim IDs. It is not part
of `/health/ready`; a 200 response proves that the diagnostic query completed,
not that backlog or lag is acceptable. PR07-C defines no alert threshold.

Use this split when diagnosing:

1. A `Pending`, overdue, or `Failed` ledger row is a generation problem. Check
   feature state, preference/timezone diagnostics, claim age, bounded safe error
   code, and append-preserved attempt history.
2. A claim that observes feature disable should be fenced-released immediately:
   automatic counters return to their pre-claim values, while an operator
   restart retains its one pending audited attempt. It is not a generation
   failure and must not create a Notification or Outbox row.
3. A long-lived `Claimed` row may belong to a live worker or a
   cancelled/crashed worker that never observed the flag. Do not clear its
   owner/token manually. A live generation that is waiting on its recipient
   User still holds its Job lock, so `FOR UPDATE SKIP LOCKED` must leave it
   unchanged even after `ClaimExpiresAt`. After crash, connection loss, or
   rollback releases the Job lock, a later enabled scheduler cycle fences the
   old token, marks that attempt `Expired`, and either creates the next
   automatic attempt or reaches terminal `Failed` when its budget is
   exhausted.
4. A `Succeeded` job with no `NotificationId` is the approved zero-candidate
   no-op, not a delivery failure.
5. A `Succeeded` job with a Notification but delayed/missing realtime behavior
   is an Outbox/dispatch problem. Diagnose the existing Outbox pending/retry/
   dead-letter state; do not restart digest generation to replay delivery.

Automatic failures and expired claims share the exact budget of three
automatic attempts. Feature-disabled claim release does not consume that
budget. The third terminal transition sets `Failed`; there is no separate
digest dead-letter table.

`NotificationUserState.Version` is an optimistic-concurrency token. If a
digest and an immediate Task Notification stage the same next recipient
version, only one unit of work can commit; the other Notification, Outbox row,
state update, and digest transition roll back together. A digest-side conflict
is first retried inside the claimed attempt in up to three completely recreated
generation transactions. The repository exposes only a safe persistence-
conflict marker, never provider SQLSTATE or exception text. If those retries
are exhausted, the worker records the safe `DigestPersistenceConflict` outcome
through the normal failure path. A clean retry reads the committed version,
while logical keys reuse any already-committed intent, so the final recipient
versions remain distinct rather than duplicating a signal.

Worker warnings contain only fixed text and safe error codes. Do not add
exception messages or Tenant/user/Workspace/Task/job/claim IDs while
investigating. Do not put TaskComment bodies, review/Blocked reasons,
restricted titles, private preferences, Watch state, tokens, secrets, license
material, or file contents into logs or incident notes.

### Audited operator restart

There is no broad digest administration UI. A current Platform/System
administrator in the affected Tenant may call:

```http
POST /api/admin/task-deadline-digests/{jobId}/restart
Content-Type: application/json
X-CSRF-Token: <token when CSRF is enabled>

{
  "reason": "Operator verified a transient dependency outage."
}
```

Use this only after the job is terminal `Failed` and the root cause is
understood. The reason is required, trimmed, and limited to 500 characters;
keep it metadata-safe. Each call appends one linked, requested-by-user pending
attempt and a `TaskDeadlineDigestRestarted` AuditLog entry. It grants exactly
one operator attempt and preserves the three automatic attempts. An operator
restart must not reset attempt counters, delete attempt rows, rewrite the
original failure as automatic, or clone the five-field identity to force
delivery. The separate feature-disable release may only reverse its
just-claimed fenced automatic attempt; it is not an operator restart or a new
attempt.

### Worker drain and rollback

There is no explicit drain endpoint. For maintenance:

1. Disable `tasks.notificationsV1` for affected Tenants so no new claims are
   taken.
2. Allow an already-started generator to observe the disabled flag and perform
   its fenced release before stopping the process. This restores an automatic
   attempt budget or preserves the same pending operator-restart attempt; it
   does not wait for claim expiry.
3. Treat a claim interrupted before it can observe the flag as fenced state.
   It may remain `Claimed`; after re-enable, ordinary claim-expiry processing
   will recover it safely. Do not bypass `ClaimExpiresAt` with manual updates.
4. Inspect digest and Outbox state independently before resuming.

For an application rollback, first disable the feature and drain as above,
then deploy the prior binary while leaving the additive digest migration in
place. That binary does not use the new tables. Apply the migration Down only
after backing up and explicitly accepting loss of all digest job/attempt
history; Down drops both tables. It does not roll back or delete already
committed Notification, Outbox, preference, or Audit rows. Never use Outbox
deletion as a digest rollback mechanism.

## TASK-V1-PR07-D delivery and opening operations

Outbox retry and operator replay use the same current dispatch authorizer as
first delivery. Operators must not replay an event to a historical group or add
payload detail to diagnose a denied recipient. A current authorization denial
is completed as the existing metadata-safe `NoAuthorizedRecipient` terminal
outcome; it is neither a transient retry nor a DeadLetter condition.

When archiving a Workspace, confirm that the archive, audit entry, and one
metadata-only `Security.AuthorizationStateChanged.v1` recipient event per
active affected member commit together. A rollback must leave no authorization
invalidation Outbox row. The event contains no member list, role, Task,
Project, Notification, or display content; delivery is allowed only long
enough for the recipient browser to clear protected state.

For a report that a Notification cannot be opened, use the normal notification
list/open flow and record only safe outcome codes. Do not inspect or expose a
deleted/archived/revoked reason, protected title/body, digest Task list,
preference, SQL, raw exception, route history, cookie, CSRF token, or session
identifier. `Unavailable` intentionally remains indistinguishable across those
target states.

The Task/Digest created signal is reference-only. A healthy Angular client
coalesces a bounded authorized HTTP refresh through the single
`RealtimeFacade`; feature-specific sockets, manually constructed group names,
and client-side routes persisted from old Notification rows are unsupported.
Artifact and Message Notifications retain their legacy embedded created-event
shape, but the server resolves Artifact through current Project visibility and
Message through current recursive Conversation visibility before initial,
delayed, retry, or replay delivery. A user denied by the authoritative current
Project or Conversation policy must see the row disappear from list/unread,
receive `Unavailable` on open, and be unable to read/delete it. Treat any
embedded payload delivered after that loss as an authorization incident.
If realtime is degraded, use the existing manual HTTP refresh fallback rather
than treating a durable signal as data authority.

## Backup

Coglatas Portal recovery has two layers:

- Full-system backup and restore for operators.
- Tenant metadata export for school-by-school portability and future migration.

The MVP implements tenant metadata export only. It does not implement full tenant restore.

Back up:

- PostgreSQL database.
- File storage root, NAS path, MinIO bucket, S3/object bucket, or Docker upload volume.
- Non-secret configuration.
- Secrets through the approved vault/secret-manager recovery process.
- Docker Compose files, reverse proxy config, TLS renewal config, and operator runbooks.

PostgreSQL backup example:

```bash
pg_dump --format=custom --file=coglatas.backup "$COGLATAS_DATABASE_URL"
```

Recommended SaaS schedule:

- Database: daily full backup plus point-in-time recovery if available.
- Object storage: versioning or daily bucket backup.
- Configuration: on every deployment change.
- Secrets: secret-manager recovery enabled and tested.
- Audit logs: retain according to contract and platform policy.

## Restore Drill

Untested backups are not backups. Each pilot environment must record at least one successful restore drill before real school data is relied on.

1. Create an isolated restore environment.
2. Restore PostgreSQL from the backup.
3. Restore file storage from the same recovery point.
4. Restore configuration and secrets.
5. Confirm the app version and apply pending migrations only when appropriate.
6. Start the app.
7. Check `/health/live` and `/health/ready`.
8. Sign in as an admin test account.
9. Verify tenant selection, project lists, file metadata, authorized download, and audit continuity.
10. Run manual TenantA/TenantB isolation checks.
11. Record restore time, operator, failures, and follow-up actions.

Future tenant-level restore must never overwrite another tenant, must import into a staging area before merge, and must audit every restore operation.

## Incident Notes

Capture:

- Time and tenant.
- Actor user if known.
- Affected resources.
- TraceId or correlation ID.
- Audit/security event IDs.
- Operator actions taken.

Do not paste passwords, raw tokens, invite token values, API token raw values, webhook secrets, signed URLs, or sensitive message bodies into incident notes.

## Known Operational Gaps

- First-user/PlatformAdmin bootstrap is startup-seed based and must be explicitly controlled per environment.
- `deploy/onprem/compose.yml` runs its one-shot migration service before the app and binds the origin to loopback by default; a fresh production-profile startup through the intended TLS proxy still needs recorded runtime evidence.
- Forwarded-header trust requires an explicit operator IP/CIDR boundary and fails closed when proxy mode has none. See `docs/DEPLOYMENT.md` for the required external TLS proxy topology and readiness check.
- Production object storage adapter is not implemented.
- Full tenant restore is not implemented.
- Backup/restore must be rehearsed per environment.
- Outbox and digest aggregate health endpoints exist, but they are not
  readiness gates and have no repository-defined alert thresholds; background
  worker health/alerting remains incomplete.
- API smoke examples are placeholders until run against a seeded target environment.
