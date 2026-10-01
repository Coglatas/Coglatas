# API Contracts

This document is the active API convention guide. For endpoint examples, use `docs/API_SMOKE_TESTS.http`.

Implementation note: this document describes the intended contract. The current controllers do not consistently follow one error shape or HTTP status mapping. Global exceptions return `ErrorResponse(Code, Message, TraceId)`, while many controller failures return `{ "error": "..." }` and map authorization/not-found failures to `400`. TASK-V1-PR06 adds a narrow safe envelope for Gantt routes. WPC-01 now does the same for Workspace capabilities/create, their authentication/model-binding/CSRF/exception boundary, Project activation-transition conflicts, disabled legacy Project create, and masked Project detail. Neither change resolves the repository-wide mismatch. Track that broader mismatch in `docs/KNOWN_ISSUES.md`; exact controller/service findings are in `docs/BACKEND_LOGIC_AUDIT.md`.

## Transport request-line limit

Kestrel rejects oversized request lines before routing, authentication, or
application middleware. The default limit is 8,192 bytes. HTTP 414 (URI Too Long)
can therefore occur on any API operation, including authenticated routes, with
an empty body and no application JSON error envelope. The generated OpenAPI
contract documents this transport response without promising JSON content.
Clients must handle an empty 414 response. This does not change the server limit
or relax validation, authentication, or authorization.

## Announcement HTTP failures

The `/api/announcements` endpoints preserve the existing `{ "error": "..." }`
body while returning 401 for authentication failures, 403 for explicit permission
denials (including publication audience authorization), and 404 for the existing
redacted "Announcement not found." result. Missing and hidden resources remain
indistinguishable. Input validation and unclassified failures retain 400; success
responses are unchanged. Clients must accept these status codes instead of
assuming every application failure is 400. The draft workflow below retains its
separate error mapping.

## File upload form contracts

Artifact-version and attachment uploads require `multipart/form-data` and a
`File` part. Missing files remain invalid (400); unsupported media types return
415. Artifact endpoints retain their error text while returning 404 for the
existing redacted missing artifact/version/project result and 403 for an
explicit artifact-creation permission denial.

## #378 durable announcement draft delivery

The Announcement editor’s production create path uses the durable
`/api/announcement-drafts` workflow. These routes retain the legacy
announcement controller’s redacted error style, so callers must not render
raw `error` text. Missing, cross-Tenant, other-author, or currently revoked
drafts are deliberately indistinguishable from not found.

| Route | Request | Success |
| --- | --- | --- |
| `POST /api/announcement-drafts` | exact target/content plus required `Idempotency-Key` | 201 Draft response |
| `PUT /api/announcement-drafts/{draftId}` | `expectedVersion`, target/content | 200 Draft response |
| `POST /api/announcement-drafts/{draftId}/publish` | `expectedVersion` plus required `Idempotency-Key` | 200 **Scheduled** response due immediately |
| `POST /api/announcement-drafts/{draftId}/schedule` | `expectedVersion`, local wall-clock time, IANA zone, optional valid overlap offset, and `Idempotency-Key` | 200 Scheduled response |
| `GET /api/announcement-drafts` / `/{draftId}` | none | current authorized author’s Draft/Scheduled/Published response |

The accepted lifecycle is `Draft -> Scheduled -> Published`. In particular,
the publish route does not return an Announcement or claim delivery has
completed. The due worker creates the Announcement after it reauthorizes the
persisted selected target. Idempotency is Tenant/actor/operation scoped;
unchanged lost-response replay reconciles one logical draft or transition,
while payload/key mismatch and version conflicts return 409.

## WPC-02B canonical Workspace creation

WPC-01 established the retry-safe transaction and error boundary. WPC-02B
completed the production contract with delegated capability evaluation and a
canonical Conversation-backed `WorkspaceGeneral` initializer. Workspace
creation is therefore live; it is not a test-only or unavailable route.

`GET /api/workspaces/capabilities` returns the backend-owned current-Tenant
projection through the canonical success envelope:

```json
{
  "requestId": "...",
  "data": { "canCreate": true },
  "warnings": []
}
```

The value combines current create authority with required-initialization
availability. Authority requires an active current-Tenant Owner/Admin
membership or a current active Tenant-scoped `workspace.create` grant.
Ordinary Tenant membership and Platform/SystemAdmin alone do not grant the
command. A missing initializer still fails closed with `canCreate=false`.

`POST /api/workspaces` requires an `Idempotency-Key` containing 8–128
printable ASCII characters and keeps the approved minimal body:

```json
{
  "name": "Workspace name",
  "description": null,
  "icon": null
}
```

Only `name` is required in the JSON body. `description` and `icon` may be
omitted or explicitly null; the service normalizes both as optional metadata.
The generated OpenAPI contract preserves these nullable string types and marks
only `name` required. This describes existing request processing; it does not
change validation, authorization, idempotency, or persistence.

The authenticated actor and current Tenant are server-owned scope. A successful
command commits one relational transaction containing the idempotency claim,
active Workspace, active creator Owner membership, canonical
`WorkspaceGeneral` Conversation with public-within-scope visibility and
creator participation,
`WorkspaceCreated` audit, and authorization Outbox events. Initialization or
Outbox staging failure returns `DependencyUnavailable` without committing a
partial resource. Replaying the same normalized request with the same scoped
identity reconciles one logical resource; another actor/Tenant cannot recover
it, current membership is rechecked, and reuse for another payload is HTTP 409.
The generated Workspace-create OpenAPI contract includes the canonical error
envelope for HTTP 409 conflicts and HTTP 503 dependency failures.

Workspace-create failures use the full WPC error envelope. Exact cases include
`MalformedJson`, `ValidationFailed`, `MissingIdempotencyKey`,
`InvalidIdempotencyKey`, `AuthenticationRequired`, `CapabilityDenied`,
`CsrfRejected`, masked `NotFound`, `IdempotencyConflict`,
`UnsupportedMediaType`, `DependencyUnavailable`, and `UnexpectedServerError`.
Changed activation, recovery, and archive-read-only conflicts add 409
`InvalidStateTransition`. Every invalid Project lifecycle transition produced
by `ProjectService.UpdateAsync` returns that typed HTTP 409 with target
`body.status` and the fixed safe public message. Restore and read-only
conflicts retain their approved Project-targeted equivalents. WPC successes
carry `requestId`, `data`, and `warnings`; errors carry `requestId`,
`error.code`, `error.message`, `error.target`, `error.details`,
`error.redactionApplied`, `traceId`, and `status`.

Project responses preserve nullable `groupId` and expose `versionNo`.
`POST /api/projects` is a deprecated compatibility route and now always
returns 503 without mutation because its body-owned Workspace scope, legacy
authority, required Group, missing Visibility, and missing idempotency cannot
safely approximate the canonical command.
WPC-02C and WPC-02D subsequently implemented
`POST /api/workspaces/{workspaceId}/projects` and
`POST /api/projects/{projectId}/activate`. The deprecated unscoped
`POST /api/projects` route remains disabled and must not be used as a fallback.
Generic `Planning -> Active`, `Suspended -> Planning`, and
`Suspended -> Active` return 409 `InvalidStateTransition`. `Planning -> Suspended`,
`Suspended -> Archived`, `Active -> Review`, and `Review -> Active` remain
valid. Same-state metadata-only Active or Suspended updates remain valid, and
no generic mutation introduces another route to Active. Archived/Deleted
restore cannot safely select a prior lifecycle state, so an otherwise-authorized
request returns the same typed 409 without lifecycle/deletion mutation,
success audit, invalidation, or save. Archived/Deleted Projects are read-only
through the generic update path, and the ordinary archive path cannot produce
a second success side effect; an otherwise-authorized explicit Project manager
receives typed 409 on repetition. Planning/Suspended discovery and subordinate
resources require explicit Project membership.

For non-Archived rows, Project list and Project-derived Search share the
current Project detail read predicate. It covers Project, Task, Artifact,
ActivityLog, Comment, and project-bound Message results. A grouped operational Project therefore remains
hidden from an ordinary Workspace member outside its Project and Group, while
explicit Project members, Group members, Workspace Owner/Admin actors, ordinary
members viewing ungrouped operational Projects, and current-policy
SystemAdmins retain their existing access. SystemAdmin is not a global bypass.
Planning/Suspended still require Project membership. Non-deleted Archived
history is list-only for an active Workspace member who is also an explicit
Project member; it remains hidden from detail, Search, and subordinate reads,
while Deleted rows remain hidden. Project-bound Conversation detail, list totals, unread/update polling,
and Message Search use the same depth-bounded recursive Thread read boundary.
Missing ancestor identity, inconsistent Workspace/Project/root scope, a cycle,
or more than 32 Thread edges fails closed before protected content or count
metadata is returned. Send, moderate, and Thread-create authorization first
requires that structural read boundary; Thread creation cannot persist a child
outside it. On PostgreSQL, matching Messages are constrained by that
authoritative recursive readable-Conversation ID query before deterministic
`CreatedAt DESC, Id ASC` ordering and the final result bound. Tenant, membership,
Workspace/Project/root consistency, cycle rejection, Project visibility, and
the 32-level ceiling remain inside the shared relation. Non-PostgreSQL
providers retain the bounded fail-closed fallback. WPC-02A persists canonical
Project Visibility and activation provenance while leaving migrated legacy
rows explicitly unclassified. WPC-02C owns Workspace-scoped create authority
and idempotency; WPC-02D owns canonical `ProjectGeneral` provisioning and
activation-time Task workflow mapping. Clients must use those canonical
commands; the deprecated unscoped Project-create route remains disabled.

Issue #409 adds `GET
/api/workspaces/{workspaceId}/projects/create-options`. Its WPC success
envelope data is `workspaceId`, `canCreateUngrouped`, `allowedVisibilities`,
and `groups`, where each Group option contains only `id` and `name`. A current
Workspace member with no available create scope receives a valid 200 with
`canCreateUngrouped=false` and empty arrays. The response never exposes Groups
where the actor cannot create. The dashboard separately appends
`canOpenProjectCreate`; `canCreateProject` remains the ungrouped Quick Create
capability.

`ProjectListQuery` appends optional `workspaceId`, and the repository applies
that filter inside the current authorized Project read scope before paging.
`ProjectResponse.uiPermissions` appends `canActivate`. The full Angular create
flow accepts only the strict 201 canonical response, confirms the created
Project with an authoritative GET, and treats later opening failure as
GET/navigation-only recovery. The Draft Overview performs no operational
subresource reads. Activation sends only `{ "expectedVersion": versionNo }`,
accepts only the strict HTTP 200 WPC envelope with the matching `projectId`,
and refetches Project state before exposing operational views.

## General Rules

- REST APIs are the source of truth for the bundled frontend.
- Controllers stay thin and call Application services.
- APIs return DTOs, never EF entities.
- Request DTOs must not expose server-managed fields unless the use case explicitly requires them.
- Use async I/O for database and file operations.
- Keep broad list APIs paginated, filtered, or otherwise bounded.
- Avoid leaking whether records exist across tenant/resource boundaries.

## Request Conventions

- Use JSON request bodies for create/update commands.
- Use route IDs for target resources.
- Use query parameters for paging, search text, filters, and sorting.
- Use server-side current tenant context; do not accept `TenantId` from normal tenant endpoint bodies.
- Use generated storage keys and server-managed ownership for uploads.

## Response Conventions

- Return response DTOs shaped for the current use case.
- Keep response fields explicit; do not expose hashes, raw tokens, secrets, storage credentials, internal file paths, or EF navigation graphs.
- Return raw API token values only once during token creation.
- Include compact metadata where useful, such as IDs, display names, status, timestamps, and current user's role/permissions.

## Errors

Target the shared error response shape from `src/Coglatas.Web/Models/ErrorResponse.cs`. Existing endpoints still need migration to this contract.

Error responses should include:

- safe message
- trace ID or correlation ID when available
- validation details for bad input when safe

Production errors must not expose stack traces, connection strings, SQL, secrets, file paths, raw request bodies, or internal exception details.

## Validation

Validate input before executing use cases:

- Required fields and length limits.
- Enum/status values.
- Date/time ordering.
- Paging bounds.
- File size, extension, and MIME type.
- Feature flag availability.
- Quota availability.

Current implementation gaps include inconsistent enum and GUID validation, missing persistence-length checks, malformed JSON escaping as server errors, nullable collection assumptions, and query date ranges that are not always validated. See `docs/BACKEND_LOGIC_AUDIT.md`.

Use Application-level validation for rules that need database state or authorization context.

## Authorization Expectations

- Authenticate protected endpoints.
- Enforce tenant access before resource access.
- Enforce resource authorization in Application services.
- Platform APIs live under `/api/platform/*` and require PlatformAdmin.
- Tenant administration APIs apply only to the current tenant.
- File download endpoints must authorize before returning bytes or storage redirects.
- Search, notifications, audit logs, exports, integrations, webhooks, and API tokens must be tenant-scoped.

## Pagination And Filtering

Use `PagedResponse<T>` for potentially large result sets.

List APIs should define:

- page number or cursor
- page size with a maximum
- allowed sort fields
- allowed filters
- tenant/resource scope

Never return unbounded tables to the browser UI.

## Message notification preference scopes

The private current-user routes are:

- `GET /api/me/message-notification-preferences`
- `PATCH /api/me/message-notification-preferences`

They require cookie authentication and an active membership in the current
Tenant. The Tenant and user are derived server-side; neither is accepted from
the request body. Missing, inactive, cross-Tenant, and platform scope fail with
the same generic unavailable response.

GET and a successful PATCH return:

```json
{
  "messageNotificationsEnabled": true
}
```

PATCH accepts the same Boolean field. This switch controls Message notification
row creation for all conversations in the current Tenant, including mentions.
Each conversation's existing `isMuted` participant state is evaluated
independently and can suppress that conversation even when the global value is
enabled. Neither preference suppresses Message persistence, unread state, or
realtime conversation activity.

The browser-only unread-badge display setting is not part of this API. It is
namespaced by Tenant and user in local storage and has no authorization or
delivery effect.

## Issue #355 Conversation inbox views

`GET /api/conversations` remains paginated and accepts `page`, `pageSize`, and
one `view` value: `All`, `Unread`, `Mentions`, or `Later`. Its additive response
keeps `items`, `page`, `pageSize`, and `totalCount`, and adds the selected
string `view` plus exact Conversation counts:

```json
{
  "items": [],
  "page": 1,
  "pageSize": 20,
  "totalCount": 0,
  "view": "All",
  "counts": {
    "all": 0,
    "unread": 0,
    "mentions": 0,
    "later": 0
  }
}
```

Every item and count is evaluated from the current user's authoritative,
depth-bounded readable-Conversation relation before filtering or paging. A
nonparticipant contributes no row, title, last Message, count, or state bit.
The four counts count Conversations, not Messages:

- `Unread` means at least one non-deleted Message from another author is after
  the current user's server read cursor.
- `Mentions` means an unread recipient-owned Mention Notification currently
  resolves to a non-deleted Message in that readable Conversation.
- `Later` is the current active participant's private `isLater` Boolean.
- `All` is the complete currently readable Conversation count.

`PATCH /api/conversations/{conversationId}/state` accepts additive nullable
`isLater`. Only the current active participant's row can be read or changed.
Changing `isLater` does not change `ReadState`, `lastReadMessageId`, unread
counts, Mention Notification state, another participant's state, or Message
delivery. The successful state and list item responses include `isLater`.
Existing rows default to `false`; no prior read, mute, or archive value is
reclassified.

This `isLater` value defers an entire Conversation in the inbox. It is not a
saved-Message or reminder contract: it carries no Message ID, completion state,
due time, or notification schedule. Issue #368 owns those distinct per-Message
follow-up semantics and must not infer them from `ConversationMember.IsLater`.

## Issue #368 saved Message follow-ups

The current participant owns these private routes:

- `GET /api/me/message-follow-ups?page=1&pageSize=20`
- `PUT /api/me/message-follow-ups/{messageId}`
- `DELETE /api/me/message-follow-ups/{messageId}`

PUT and DELETE are idempotent after the Message is reauthorized through its
current readable Conversation. PUT returns `isSaved: true` and the saved UTC
timestamp; DELETE returns `isSaved: false`. A missing, deleted, cross-Tenant,
nonparticipant, removed, or otherwise unreadable Message is the same generic
failure and reveals neither whether a private marker exists nor target data.

GET returns a normal paged response. Each row contains the Message and
Conversation IDs needed for navigation, Workspace ID, Conversation type and
safe title, optional `threadRootMessageId`, current authorized author display
name/body, Message creation time, and saved time. Current Conversation
readability is applied before count, ordering, and paging. Deleted or revoked
rows contribute neither an item nor `totalCount`.

`GET /api/conversations/{conversationId}/messages` accepts additive
`anchorMessageId`. After the existing Conversation boundary, the target must
be a current non-deleted Message in that exact Conversation. The response
includes the target timeline Message plus recent context even when it is older
than the default page; for a thread reply the timeline anchor is its root and
the client opens `GET /api/messages/{rootMessageId}/thread` with
`anchorReplyMessageId={savedReplyId}`. That optional reply anchor must be a
current non-deleted reply in the same Conversation whose exact
`threadRootMessageId` is the route root. A valid anchor is returned with the
latest 99 other replies, preserving the 100-row bound even when the selected
reply is older than the ordinary latest window. Deleted, missing,
cross-Conversation, mismatched-root, and cross-Tenant anchors all return the
same generic thread-not-found failure without counts or target metadata.
Anchor loading, save, and complete never update read state.

No reminder field or endpoint exists. Adding a reminder requires a separate
scheduler, delivery, timezone, retry, and cancellation contract; clients must
not infer one from `savedAt`.

## Issue #367 advanced Message filters

`GET /api/search` accepts additive Message-only fields when `type=Message`:
`authorUserId`, `fromDate`, exclusive `toDateExclusive`, `messageRead` (`All`,
`Read`, or `Unread`), and `messageAttachment` (`All`, `With`, or `Without`). A
text query is optional when any filter is active. `toDateExclusive` cannot be
combined with legacy inclusive `toDate`, and invalid UUID, enum, or date query
binding returns fixed `SearchRequestInvalid` HTTP 400 without reflecting input.

Search date bounds (`fromDate`, inclusive `toDate`, and exclusive
`toDateExclusive`) accept valid DateTimeOffset values with UTC offsets. The
service normalizes them to UTC before PostgreSQL queries, preserving the same
instants and the inclusive/exclusive boundary behavior. Model binding and
existing range validation remain unchanged.

`GET /api/search/message-authors` accepts either a trimmed `q` of 2 to 120
characters with `limit` capped at 20, or `selectedUserId` with `limit=1` for
validated route replay. It returns only `{ userId, displayName }` and no count,
email, role, lifecycle, Conversation identity, or Message content. Missing,
cross-Tenant, and unauthorized selected IDs all return the same empty success
projection.

All facets compose before ordering/limiting with the current user's recursive
readable-Conversation relation and exact Message/Conversation Tenant and
Workspace consistency. Read coverage uses an exact same-scope cursor Message
and `(CreatedAt, Id)` ordering even when a legacy sequence is zero. A null
cursor ID retains the established `LastReadAt` compatibility fallback; a
non-null invalid or mismatched cursor fails closed. `With` means a clean,
classified, active, non-deleted, exact Message-owned Attachment/FileObject
relationship. `Without` is its negation; no file metadata or invalid-link
detail is projected. This does not enable Message attachment upload or resolve
BE-004. The full contract is in
`docs/contracts/message-advanced-filters-v1.md`.

## Same-Conversation Message threads

The canonical Message-thread routes are:

- `GET /api/messages/{messageId}/thread`
- `POST /api/messages/{messageId}/thread/messages`

`messageId` is the canonical root Message, not a legacy Thread Conversation.
The root must have no `threadRootMessageId`, and every reply stores that root ID
while remaining in the root's Conversation. `ConversationType.Thread` and
`ParentConversationId` remain supported by their existing APIs only; the
service never guesses a Message anchor for them. Main Conversation Message
lists return roots only and attach an authorized summary when replies exist.

GET inherits the current Conversation read boundary before projecting any
body, count, timestamp, or display name. Without an anchor it returns the
pinned root and latest at most 100 replies in stable chronological order. With
an authorized `anchorReplyMessageId`, it returns that exact non-deleted reply
plus the latest at most 99 other replies, still in stable chronological order.
Both forms return the exact durable reply count, latest reply timestamp, at
most three distinct display names, `hasMore`, and `maximumReplies: 100`. There
is no older-reply cursor in this version; `hasMore: true` means additional
replies were not loaded. Deleted roots and ordinary projected replies are
bodyless/attachment-free tombstones; deleted replies remain in the order and
count but cannot themselves be selected as an anchor.

The main Conversation list retains a deleted main-timeline root only while it
has at least one durable same-Conversation reply. That root is projected as a
bodyless, attachment-free tombstone with its authorized summary, keeps its
timeline ordering, and can reopen the exact thread GET; its reply composer is
disabled. Deleted replies continue to count, so deleting every visible reply
does not remove the anchor. An ordinary deleted Message with no durable replies
remains omitted. This is the narrow thread-continuity rule; general zero-reply
main-timeline tombstone presentation remains outside Issue #362.

POST accepts only:

```json
{
  "body": "Reply text",
  "clientRequestId": "00000000-0000-4000-8000-000000000001",
  "mentionedUserIds": []
}
```

Unknown properties are rejected. Current posting authority is always
required, and the first durable reply additionally requires the member's
current `CanCreateThread`; later replies do not. A deleted root cannot receive
a reply. Idempotent replay is scoped to the same current Conversation, author,
and root target: a `clientRequestId` previously used for a main-timeline
Message or another root is rejected rather than retargeted. The PostgreSQL
client-request unique constraint is also the concurrent commit boundary. If
two independent contexts race with the same key, one transaction commits and
the exact losing constraint violation reloads that Message after clearing its
rolled-back audit/notification/outbox work. Other database errors are not
reconciled. The caller then rechecks the committed root target before returning
it.

Successful creation emits the ordinary `Messaging.MessageCreated.v1` with
`threadRootMessageId` so consumers keep it out of the main timeline, plus a
metadata-only `Messaging.ThreadChanged.v1`. The latter contains the root ID,
count/latest metadata, change kind, and `requiresRefetch: true`; it contains no
Message body or participant names. Clients must invalidate/refetch the
authorized projection so participant summaries cannot become stale or leak.
Its `aggregateVersion` is null because reply count is not a durable monotonic
thread version (updates, deletes, and concurrent creates can share a count).
The realtime stale guard therefore delivers every ThreadChanged refetch hint.
Thread reply audit metadata records identifiers and decisions only, never the
reply body.

## TASK-V1-PR07-A task notification preferences

The following private, current-user routes are implemented by PR07-A. They do
not enable Task notification production or digest delivery.

- `GET /api/me/workspaces/{workspaceId}/task-notification-preferences`
- `PATCH /api/me/workspaces/{workspaceId}/task-notification-preferences`

Both routes require an authenticated user, an active current Tenant, an active
Workspace, and that user's active membership in that Workspace. A guessed,
cross-Tenant, inactive, or non-member Workspace returns the same safe typed
404 `TASK_NOTIFICATION_PREFERENCE_NOT_FOUND`; it does not reveal which check
failed. Authentication failure is 401.

GET and a successful PATCH return the private DTO below and a matching ETag of
the form `"{version}"`:

```json
{
  "deadlineDigestLocalTime": null,
  "effectiveDeadlineDigestLocalTime": "08:00",
  "workspaceTimeZoneId": "Asia/Tokyo",
  "version": 1
}
```

`deadlineDigestLocalTime` is the stored nullable override. `null` means
inherit the Workspace's `08:00` (or subsequently configured) local-time
default, and `effectiveDeadlineDigestLocalTime` is therefore always non-null.
`workspaceTimeZoneId` is resolved server-side from the Workspace with the
existing Tenant/UTC fallback; browser and Project timezone values are not
authoritative. Neither field is included in broad Workspace or member DTOs.

PATCH accepts:

```json
{
  "deadlineDigestLocalTime": "08:15",
  "expectedVersion": 1
}
```

The local time is a timezone-free, exact `HH:mm` value from `00:00` through
`23:45` at 15-minute intervals. `null` restores inheritance. The server never
rounds, coerces, or substitutes an invalid supplied value. Invalid format,
minute, or range returns HTTP 400 with
`TASK_NOTIFICATION_PREFERENCE_INVALID_LOCAL_TIME`.

`expectedVersion` is mandatory and must match the stored private preference
version. When the JSON request binds to the DTO, an omitted, zero, negative, or
stale numeric version returns HTTP 409
`TASK_NOTIFICATION_PREFERENCE_VERSION_CONFLICT`, leaves the stored preference
unchanged, and returns only safe `currentVersion` plus a matching ETag for a
refetch/retry. The implementation performs the update as one tenant- and
membership-scoped conditional database update.

Malformed JSON or an incompatible JSON value type is rejected before the
service by the shared `[ApiController]` model-validation contract. For example,
`"expectedVersion": "abc"` returns its safe HTTP 400 model-validation
response. It makes no mutation and returns neither retry metadata nor protected
preference state. PR07-A deliberately does not add a custom JSON parser or
model binder to turn that binding failure into a typed 409.

The default-disabled `tasks.notificationsV1` registry key is deliberately not
an authorization, privacy, preference, or dedupe gate. PR07-A introduces no
Task notification producer, digest worker, notification-open API, SignalR
route, or Angular preference UI.

## Issue #350 structured Task Brief

The existing Task create, subtask-create, versioned update, and detail
boundaries support three additive optional Task-specific fields in this fixed
order: `goal`, `deliverable`, and `constraints`.

- `POST /api/projects/{projectId}/tasks` accepts all three fields.
- `POST /api/tasks/{taskItemId}/subtasks` accepts all three fields.
- `PATCH /api/tasks/{taskItemId}` accepts all three fields under the existing
  `expectedVersion` command boundary.
- `GET /api/tasks/{taskItemId}` returns them inside canonical `task.brief`.

Each field is plain text, trimmed by the server, nullable, and limited to
4,000 characters. For PATCH only, omission preserves the current field while
explicit JSON `null` (or a whitespace-only supplied value) clears it. Older
clients that send only `description` continue to round-trip their free-form
Task notes unchanged. `description` is not replaced, parsed, or synthesized
from the structured fields.

```json
{
  "title": "Prepare launch review",
  "description": "Legacy free-form notes remain supported.",
  "goal": "The release is ready for an approval decision.",
  "deliverable": "A review-ready release package.",
  "constraints": null,
  "priority": 2,
  "plannedStartDate": "2026-08-24",
  "plannedEndDate": "2026-08-28",
  "progressPercent": 20,
  "expectedVersion": 7
}
```

Canonical detail reports value provenance per field. The only current source
values are `taskSpecific` and `notSet`:

```json
{
  "brief": {
    "goal": { "value": "The release is ready for review.", "source": "taskSpecific" },
    "deliverable": { "value": null, "source": "notSet" },
    "constraints": { "value": null, "source": "notSet" }
  }
}
```

Project context remains a separately authorized parent projection. The server
does not map `Project.Description` to a Task Brief field and has no Project
Brief-default contract. Project Task-list responses remain compact and do not
include Brief bodies. Detail exposure uses the existing Project-read boundary;
denial returns the same safe Task-not-found behavior and does not disclose a
Brief value.

An oversized field returns HTTP 400 `TASK_BRIEF_FIELD_TOO_LONG` with the
specific `goal`, `deliverable`, or `constraints` target. Audit and realtime
metadata contain field names/version hints only, never Brief values. The
reusable Angular fields are integrated into existing Task detail/edit and the
Issue #410 canonical Task-create candidate. The create boundary records a Task
and never starts a runtime.

## Issue #410 canonical Task create

The legacy compatibility command remains unchanged:

- `POST /api/projects/{projectId}/tasks`

The Project-aware browser create flow instead uses these canonical,
side-by-side routes:

- `GET /api/projects/{projectId}/tasks/create-options`
- `POST /api/projects/{projectId}/tasks/create`

Both routes use the canonical `{ requestId, data, warnings }` envelope and
the ordinary authenticated Project-read / Task-create boundaries. The GET
response is an authorized, advisory projection; it returns the Project and Workspace IDs and
title, `canCreateTask`, `canManageProject`, current non-deleted Milestone
options, manager-visible eligible-assignee options, and the Project source
scope:

```json
{
  "projectId": "<project id>",
  "workspaceId": "<workspace id>",
  "projectTitle": "Launch",
  "canCreateTask": true,
  "canManageProject": true,
  "milestones": [{ "id": "<milestone id>", "title": "MVP" }],
  "assignees": [{ "userId": "<user id>", "displayName": "Example User" }],
  "projectScope": {
    "policy": { "webEnabled": false, "projectFilesEnabled": false },
    "version": 0,
    "canSetTaskOverride": true
  }
}
```

When no Project policy row exists, the scope projection is the fail-closed
`false`/`false` default with version `0`; it is not a stored Task override or
source inventory. Assignee choices are omitted for a non-manager. The GET
response never grants authority to POST.

The POST requires a printable ASCII `Idempotency-Key` header of 8 through 128
characters, CSRF protection under cookie authentication, and a strict JSON
body. `title` and string `sourceScopeMode` (`Inherit` or `TaskOverride`) are
required. The optional Task data are `description`, numeric `priority`,
`milestoneId`, `startDate`, `dueDate`, `goal`, `deliverable`, `constraints`,
and `primaryAssigneeUserId`.

```json
{
  "title": "Prepare launch review",
  "description": "Optional free-form notes.",
  "priority": 1,
  "milestoneId": null,
  "startDate": "2026-08-25",
  "dueDate": "2026-08-29",
  "goal": "Reach an approval decision.",
  "deliverable": "A review-ready release package.",
  "constraints": null,
  "primaryAssigneeUserId": null,
  "sourceScopeMode": "Inherit",
  "taskOverridePolicy": null
}
```

Unknown members are rejected. `taskOverridePolicy` is forbidden for `Inherit`
and required for `TaskOverride`; when present it is the complete two-boolean
object `{ "webEnabled": false, "projectFilesEnabled": false }`. The request
contains neither server-owned scope/version identifiers nor a run, source,
provider, or URL field.

The server rechecks the current Project, Task-create capability, selected
Milestone, and selected member at the transaction's creation boundary. A
current Task creator may create an unassigned inheriting Task. Only a current
Project manager may select an initial primary assignee or a Task override. A
missing, cross-Tenant, deleted, unreadable, or wrong-Project resource uses the
same safe not-found behavior rather than revealing its existence.

The idempotency-owned transaction stages the Task and initial workflow
placement, automatic watches, optional complete Task override, required audit
entries, durable invalidations, and any initial-assignment notification. A
successful create returns HTTP 201 with `data.taskId`, Project/Workspace and
selection IDs, title, priority, status, workflow stage ID, version,
`sourceScopeMode`, and the optional complete override policy. It does not
create a `TaskExecutionRun`, capture a run snapshot, or invoke a runtime.

The same key and normalized request recheck current Task-create authority and
return the current authoritative persisted Task response. A later mutable Task
or override change therefore need not match the original request fields. A
different normalized request under the same key returns the safe HTTP 409
idempotency conflict. All other canonical validation, authorization, and
availability failures use the standard safe envelope.

## Issue #354 advisory pre-create quality checklist

Issue #354 adds no request or response contract. The maintained Task-create
browser form locally reviews the optional trimmed `goal`, `deliverable`, and
`constraints` values already bound to the canonical create command, plus the
effective authorized source policy already returned by create-options or
selected as a manager-authorized complete Task override.

The checklist is advisory only: an empty optional Brief field remains valid,
and selecting its missing-item action only moves focus to the matching existing
form control. It does not change the strict POST body, `Idempotency-Key`, CSRF,
server validation, or whether the actor can create a Task. A fail-closed
effective policy with both `webEnabled` and `projectFilesEnabled` set to false
is an explicit covered policy, not an absent source-scope value. The checklist
does not infer a Brief default from `Project.Description` and adds no runtime,
provider, Web, source-content, or source-inventory contract.

## Task progress and Activity detail

The canonical Task detail response continues to carry the current configured
Workflow Stage ID, display name, and fixed category on `task`. That current
Stage is the Task-detail phase authority. The Activity projection is separate
so an Activity dependency failure cannot suppress an otherwise authorized
current phase.

The read-only Activity route is:

- `GET /api/tasks/{taskItemId}/activity?page=1&pageSize=20`

This route exposes only Task-linked `ActivityLog` records that already exist.
It does not add an Activity writer, and current Task commands must not be
interpreted as producing Activity execution history through this change.

The service first resolves the current, non-deleted Task through the existing
Project read boundary. Missing, cross-Tenant, deleted, and unauthorized Tasks
use the existing metadata-safe Task-not-found response and do not query
Activity rows. After authorization, the repository filters by both the
authorized `ProjectId` and `TaskItemId`, while the normal Tenant query filter
remains active. Results use `OccurredAt DESC, Id DESC` ordering, `page` is
normalized to at least 1, and `pageSize` is clamped from 1 through 50.

```json
{
  "items": [
    {
      "id": "<activity id>",
      "activityType": "StatusUpdate",
      "body": "Implementation is ready for review.",
      "occurredAt": "2026-08-24T03:00:00+00:00",
      "author": {
        "userId": "<author id>",
        "displayName": "Example author"
      }
    }
  ],
  "page": 1,
  "pageSize": 20,
  "totalCount": 1,
  "hasMore": false
}
```

The persisted Activity vocabulary remains `Note`, `StatusUpdate`, `Decision`,
and `Issue`. `StatusUpdate` receives visual emphasis in the browser, while
`Issue` is labelled `Needs attention`. Neither value changes the current Task
Stage. In particular, Task has no `Failed` category: the browser does not map
an Activity issue or a transport failure to a persisted `Failed` Task state.

The browser requests page one only when the secondary Activity disclosure is
opened. It preserves independently authorized phase/detail data when an
Activity request has a transient failure, retains already loaded Activity
while a later page or refresh fails, and retries the exact failed page. A Task
realtime event remains only an invalidation hint: after Activity has been
opened, the browser refetches both canonical detail and Activity page one from
HTTP with authorization and route-generation guards. A 401/403 reauthorizes
and clears protected Task state; the Activity route's safe 404 clears it
immediately. Historical Workflow Stage transitions are not available in the
current ActivityLog contract and are not synthesized from current state or
generic Activity rows.

## Issues #357 / #461 Task execution source scope and runtime contract

The foundation is a server-authorized policy and immutable next-run snapshot
boundary. It exposes no source inventory or browser execution capability:

- `GET /api/projects/{projectId}/execution-scope`
- `PUT /api/projects/{projectId}/execution-scope`
- `GET /api/tasks/{taskItemId}/execution-scope`
- `PUT /api/tasks/{taskItemId}/execution-scope-override`
- `DELETE /api/tasks/{taskItemId}/execution-scope-override`
- `POST /api/tasks/{taskItemId}/execution-runs`

Current Project readers may use the GET routes; missing, cross-Tenant, deleted,
and unauthorized resources use the same generic 404 contract. Only the
server's current Project-management authority may change the Project default,
set/clear a complete Task override, or request a run. The write request bodies
reject unknown JSON members and require a non-negative `expectedVersion`:

```json
{ "webEnabled": false, "projectFilesEnabled": false, "expectedVersion": 1 }
```

Clearing an override sends `{ "expectedVersion": 1 }`; `0` is the creation or
already-inherit token when no Task override exists. A run request sends an
empty `{}` body and a required `Idempotency-Key`; the key represents the same
Task request even if a later policy edit changes the current effective policy.
The accepted row retains its original immutable policy snapshot plus the
server-owned `FirstPartyProjectFilesRuntimeV1` provider identity and runtime
contract version. Snapshot schema version 2 additionally retains the optional
same-Task `ResearchPlanRevision` identifier and positive revision number that
were current inside the accepted idempotent creation stage. PostgreSQL binds
that identifier, number, and Tenant/Workspace/Project/Task scope to one
revision row. It stores no copy
of plan content. A successful POST returns 201 only to mean that a logical run
was durably accepted; its initial status is `Accepted`, not execution success.
Replaying the same key returns the same logical run and plan revision.

Responses expose only effective/origin/version booleans, a safe latest-run
policy snapshot/provider/version/lifecycle projection, the optional authorized
plan revision reference/number, and
`changesApplyTo: "nextRun"`. They never expose a URL, host, source/file
identifier or name, count, raw content, credential, provider configuration,
prompt, or output. Version/idempotency conflicts use safe 409 responses. The
browser sends CSRF protection for unsafe cookie-authenticated methods.

The selected V1 runtime disables all Web/network execution and fails closed if
the immutable scope requires Web input. It accepts no browser source list,
file ID, path, object key, filename, content, credential, or provider setting.
Issue #462 owns post-commit server materialization of authorized clean Project
Files; Issue #463 owns durable result storage and retrieval. The `Accepted`
state alone is not evidence that any input was consumed or a result exists.

## Issue #364 Task Research Plan

`GET /api/tasks/{taskItemId}/research-plan` returns the current authorized
Task-owned Research Plan revision (or a null current revision when no plan has
been saved). `PUT /api/tasks/{taskItemId}/research-plan` accepts only a complete
ordered step list and `expectedVersion`. It creates the next immutable revision
atomically; it never updates or deletes an earlier revision or accepts ownership,
source, provider, or execution identifiers from the client.

Steps are bounded to 100 per revision. Each has a required title (240
characters), optional objective and scope summary (4,000 characters each), and
one of `Planned`, `Ready`, `Blocked`, or `Deferred`. Current Project readers may
read; only current `CanManageProject` authority may save. Missing,
cross-Tenant, deleted, and unauthorized Tasks use the same safe not-found
response. A stale aggregate version returns 409 `RESEARCH_PLAN_STALE_VERSION`.

The response returns the current immutable revision and safe Task-scoped fields
only. It returns no historical raw sources, storage details, credentials,
provider configuration, or execution outcome. The execution request captures
this exact current revision in snapshot schema version 2 inside its existing
immutable run snapshot; its identifier and number are one database-enforced
scoped provenance identity. The Research Plan introduces no separate execution
snapshot system. Both plan revision fields are null when the Task has no saved
plan. Existing version-1 runs remain readable with null plan provenance.

## TASK-V1-PR07-B hard deadline mutation

The existing canonical versioned Task detail mutation is extended in place:

- `PATCH /api/tasks/{taskItemId}`

`deadlineAt` is optional independently of the existing required Task-body and
`expectedVersion` fields. Omission preserves the persisted hard deadline,
explicit JSON `null` removes it, and an ISO 8601 timestamp adds or replaces it.
The server preserves the requested instant but normalizes every non-null value
to UTC before classification, PostgreSQL persistence, and response
serialization. An incompatible value uses the shared safe HTTP 400
model-validation response.
Unknown request members are rejected; clients cannot supply
`isMajorDeadlineChange`, a deadline classification, or an equivalent field to
force or suppress server behavior.

```json
{
  "title": "Task",
  "description": null,
  "priority": 1,
  "plannedStartDate": "2026-08-02",
  "plannedEndDate": "2026-08-04",
  "progressPercent": 25,
  "expectedVersion": 7,
  "deadlineAt": "2026-08-04T08:00:00+09:00"
}
```

The server compares the persisted old value with the requested new value once,
using the Workspace timezone and one command-time instant. The outcomes are
`Added`, `Removed`, `ShiftAtLeast24Hours`, `CrossedUrgencyBoundary`, and
`None`. Added/removed values are major; an absolute shift of exactly 24 hours
or more is major; a smaller shift is major only when it crosses the local
Overdue/Today urgency boundary. The safe classification may appear in Audit
metadata, but no Task title/body, review/comment content, Watch state, or
recipient relationship set is recorded with it.

`PATCH /api/tasks/{taskItemId}/schedule` continues to own only
`plannedStartDate` and `plannedEndDate`; it rejects `deadlineAt` and never runs
hard-deadline classification.

When `tasks.notificationsV1` is enabled, qualifying Task commands stage the
authorized recipient Notification, its logical key, the approved business
Outbox event, the minimal recipient-only Notification refetch signal, and the
AuditLog in the mutation's single database save. The key remains disabled by
default. It never bypasses authorization, privacy, actor suppression, or
dedupe. Digest, notification-open, SignalR-routing, and Angular contracts are
outside PR07-B.

The existing `/api/tasks/{taskItemId}/assignments` collection remains a
compatibility adapter. Its `Assignee`, `Reviewer`, and `Support` roles map to
the canonical Primary Assignee, Reviewer, and Collaborator relationships and
use the same transaction and notification producer. A row that merely mirrors
the already-canonical relationship creates no second semantic event or
notification. New or changed-to `Owner` roles, invariant violations, and
operations that would ambiguously alter canonical state fail before mutation;
historical same-role `Owner` metadata updates/removal and mismatched-row removal
remain non-canonical compatibility cleanup.

## TASK-V1-PR07-C Workspace deadline digest

PR07-C adds internal generation and one operator command. It does not add a
recipient digest-list API, notification-open API, SignalR route, or frontend
navigation contract.

The in-process worker derives one daily identity from the server-owned current
Tenant, Workspace, user, Workspace-local date, and policy version. It reads the
private member preference established by PR07-A, inheriting the Workspace
default when that value is null. The accepted local-time contract remains an
exact 15-minute value from `00:00` through `23:45`; clients cannot submit a
timezone or Project-specific time through a digest-generation route.

When the final current-state recheck finds at least one eligible Task, the
existing Notification APIs expose one generic row. Its relevant fields are:

```json
{
  "notificationType": 5,
  "title": "Task deadline digest",
  "body": null,
  "relatedEntityType": "TaskDeadlineDigest",
  "relatedEntityId": "<digest job id>"
}
```

`notificationType: 5` is the existing numeric `TaskDueSoon` enum contract;
PR07-C does not change repository-wide enum serialization.

This shape intentionally contains no Task list, Task title, Project name,
comment/review content, Watch state, or private preference. The transactionally
staged `Notifications.NotificationCreated.v1` signal contains only:

```json
{
  "notificationId": "<notification id>",
  "stateVersion": 42,
  "requiresRefetch": true
}
```

There is exactly one logical Notification identity per recipient, Workspace,
local date, and digest policy version. A zero-candidate recheck succeeds with
no visible Notification and no Outbox signal. PR07-D remains responsible for
current-authorized delayed dispatch/replay, notification opening, and Angular
reconciliation; PR07-C does not claim those paths.

### Operator restart

The current Tenant-scoped administrator route is:

- `POST /api/admin/task-deadline-digests/{jobId}/restart`

The controller requires the existing `PlatformAdmin`/deprecated
`SystemAdmin` role boundary, and the Application service rechecks the current
active system administrator and current Tenant scope. The body is:

```json
{
  "reason": "Operator verified a transient dependency outage."
}
```

`jobId` must be non-empty. `reason` is trimmed, required, and limited to 500
characters. The command accepts only a terminal `Failed` job with no active
attempt. Success returns the controller's existing `{ "status": "OK" }`
shape. Validation, cross-Tenant/not-found, non-failed, and active-attempt
outcomes use the existing Admin controller `400 { "error": "..." }` mapping;
PR07-C does not standardize that pre-existing API mismatch.

A successful restart appends one linked `OperatorRestart` attempt and an
AuditLog record. It never resets the three automatic attempts and it grants
only one operator attempt. The reason is audit input, not Notification or
realtime content.

### Aggregate health

`GET /health/task-deadline-digests` returns aggregate, platform-scope
operational state only:

- ledger due/claimed/succeeded/failed counts and oldest due/claimed times;
- process-local scheduled/claimed/succeeded/zero-candidate/failure/terminal
  failure/claim-loss/invalid-timezone/invalid-preference/operator-restart
  counters.

The response contains no Tenant, Workspace, user, Task, claim, or Notification
identifier. Like the other mapped health endpoints it currently has no route
authorization requirement; deployments must treat health-endpoint exposure as
an operational boundary. It is not part of `/health/ready` and does not by
itself prove worker progress.

`tasks.notificationsV1` is still default off. Because it is a database-backed
per-Tenant flag, the hosted worker enumerates active Tenants before checking it;
a disabled Tenant performs no digest schedule upsert, claim, or generation.

## TASK-V1-PR07-D notification opening

`POST /api/notifications/{notificationId}/open` is the navigation authority for
protected Task/TaskItem, deadline-digest, Artifact, and Message Notifications.
It is recipient owned: a Notification belonging to another user, a
cross-Tenant identifier, and a missing Notification all return the same
metadata-safe not-found result and do not disclose whether the row or target
exists.

The use case resolves the current target and authorization before it changes
read state. For a current authorized Task target it returns:

```json
{
  "outcome": "Opened",
  "route": "/projects/{projectId}/tasks/{taskId}",
  "stateVersion": 42
}
```

It never turns a persisted legacy `/tasks/{taskId}` list value into authority.
For a current authorized `TaskDeadlineDigest` target, the route is `/tasks` and
the optional typed context contains only the authorized current `workspaceId`.
The response contains no digest Task list or protected display data.
An authorized Artifact resolves through the current Project read boundary to
`/artifacts/{artifactId}`. An authorized Message resolves through the current
depth-bounded recursive Conversation boundary to `/messages/{messageId}`.
Artifact and Message open responses do not add Workspace context.

All stale, deleted, archived, revoked, inaccessible, inconsistent, unsupported,
and unknown targets return the uniform success response below. They do not
change read state and do not reveal a reason, Task title, Project/Workspace
name, comment body, review reason, membership state, or authorization detail.

```json
{
  "outcome": "Unavailable",
  "route": null,
  "stateVersion": 42
}
```

On `Opened`, an unread Notification is marked read, its recipient state version
is advanced, and the recipient-only `Notifications.NotificationReadStateChanged.v1`
Outbox signal is staged in the same transaction. Reopening an already-read
Notification does not advance the version or create another read-state signal.
The same current target resolution filters list results, total and unread
counts, read/delete mutations, and created/read-state delivery. Revocation,
Project archive, Artifact deletion, or loss of any required Conversation
ancestor therefore hides the protected row and prevents mutation or delayed,
retried, or replayed delivery.

Task and digest `Notifications.NotificationCreated.v1` signals remain
reference-only:

```json
{
  "notificationId": "<notification id>",
  "stateVersion": 42,
  "requiresRefetch": true
}
```

Clients must refetch their authorized HTTP projection and must not infer a
route, title, body, relationship, or digest list from that event.
Artifact and Message created events retain the legacy embedded recipient
payload shape, but that payload is never authorization: first delivery,
delay, retry, and replay all resolve the current Project or Conversation target
before dispatch. Protected target checks are batched for list/count operations;
Message batches invoke one bounded recursive Conversation authorization query
rather than one query per Notification.

## Project Kanban

TASK-V1-PR05 defines one vendor-neutral board over canonical Project Tasks:

- `GET /api/projects/{projectId}/kanban`
- `PUT /api/projects/{projectId}/kanban/config`
- `POST /api/tasks/{taskId}/kanban-move`

The snapshot is Project-authorized, excludes deleted records, defaults Done to
the most recent 30 days, and is bounded to at most 500 cards. Filters and
swimlanes are presentation constraints, never authorization. WIP limits
produce structured warnings rather than command denial.

Configuration and moves require current persisted versions. A move supplies
canonical Stage and neighboring Task IDs; unknown and cross-Project neighbors
return the same safe error. The HTTP response or a subsequent conflict refetch
is authoritative. Realtime events carry invalidation metadata only.

See `docs/TASK_V1_PR05.md` for ordering, transition, rollback, and permission
details.

## Canonical Project Gantt

TASK-V1-PR06 upgrades the existing Schedule route in place:

- `GET /api/projects/{projectId}/gantt`
- `PATCH /api/tasks/{taskId}/schedule`
- `PATCH /api/tasks/{taskId}/progress`
- `GET /api/tasks/{taskId}/dependencies`
- `POST /api/tasks/{successorTaskId}/dependencies`
- `DELETE /api/tasks/{successorTaskId}/dependencies/{dependencyId}?expectedVersion={version}`

The snapshot is a vendor-neutral, read-only projection. It does not persist a
Gantt row, vendor Task, date copy, progress copy, or dependency copy.

### Snapshot response

```json
{
  "projectId": "00000000-0000-0000-0000-000000000000",
  "projectTitle": "Project",
  "projectVersion": 1,
  "workflowVersion": 1,
  "calendarVersion": null,
  "calendar": {
    "timeZone": "Asia/Tokyo",
    "workingDays": [],
    "holidaysAvailable": false,
    "limitations": [
      "Workspace working-day configuration is not available in the canonical runtime.",
      "Workspace holiday data is unavailable; no holidays were inferred."
    ]
  },
  "scheduledItems": [],
  "unscheduledItems": [],
  "milestones": [],
  "dependencies": [],
  "warnings": [],
  "permissions": {
    "canEditSchedule": false,
    "canEditProgress": false,
    "canManageDependencies": false,
    "canClearSchedule": false,
    "canOpen": true
  },
  "maximumItems": 500,
  "totalItems": 0
}
```

Each item includes:

- `taskId`, `kind`, `parentTaskId`, `milestoneId`, and `title`;
- nullable `plannedStartDate`, `plannedEndDate`, and `milestoneDate` as
  day-precision `yyyy-MM-dd` values;
- `progressPercent` and `progressIsDerived`;
- `workflowStageId`, `workflowStageName`, `stageCategory`, `priority`, and
  `isBlocked`;
- nullable `primaryAssignee`;
- positive `version`;
- server-projected `scheduleEditPermissions`; and
- structured non-blocking `warnings`.

Tasks with at least one planned date appear in `scheduledItems`. Tasks with
neither date appear exactly once in `unscheduledItems` with `UNSCHEDULED`; the
server does not infer dates. Parent Task bounds/progress are derived and carry
`PARENT_DERIVED`. Compatibility Milestones appear in `milestones` with
`kind=Milestone`, one `milestoneDate`, 0/100 progress, and zero-duration
semantics. A legacy missing date carries `MILESTONE_DATE_REQUIRED`.

Each dependency includes `dependencyId`, predecessor/successor Task IDs,
string-enum `type`, `editable`, successor aggregate `version`, and warnings.
Finish-to-Start is the only authorable type. Legacy non-FS rows can be returned
as read-only inventory with `LEGACY_DEPENDENCY_TYPE`.

PR #259 was merged before its numerical limit decision was formally recorded.
On 2026-08-01, after the merge, the owner approved the existing safeguards as
the temporary PR06 full-snapshot contract: 500 combined canonical Task-kind
WorkItems and Milestones, and 2,000 active same-Project dependencies whose
endpoints are active canonical Tasks. The same item count gate applies to
snapshot, schedule, progress, and dependency paths.

When either limit is exceeded, the server MUST reject the complete snapshot
request with repository-standard typed HTTP 400
(`GANTT_ITEM_LIMIT_EXCEEDED` or `GANTT_DEPENDENCY_LIMIT_EXCEEDED`). It MUST fail
closed and MUST NOT silently truncate items or dependencies, return a partial
item set or dependency graph, or return a successful partial snapshot. The
repository rechecks the combined item bound after its bounded reads so inserts
racing the preliminary counts cannot produce an oversized response. A
successful response has `totalItems` equal to the number of rows across the
three item collections; the Angular parser rejects a mismatch.

These limits are temporary PR06 full-snapshot safety limits. They are not
permanent Project capacity limits, database storage limits, or
general-availability scalability guarantees. Paginated and virtualized
large-project Gantt delivery is deferred to
[`TASK-V1-PR06B` issue #270](https://github.com/NYGsatoshi/Coglatas/issues/270).

### Schedule and progress commands

Schedule request:

```json
{
  "plannedStartDate": "2026-08-01",
  "plannedEndDate": "2026-08-10",
  "milestoneDate": null,
  "expectedVersion": 4
}
```

Both `plannedStartDate` and `plannedEndDate` keys are required but their values
may be `null`; omitting either key is an invalid request. Clearing both moves
the Task to `unscheduledItems`. `milestoneDate` is valid only when the route ID
resolves to the canonical Milestone aggregate, in which case Task planned
dates must be `null` and the Milestone date is required. Parent-derived
schedule cannot be written. End-before-start is invalid. The command owns no
Stage, priority, Blocked, assignment, dependency, or `DeadlineAt` field.
The maintained compatibility Milestone update route likewise requires a
positive `expectedVersion`, will not activate or complete an undated
Milestone, and maps a stale Milestone revision to safe HTTP 409.

Progress request:

```json
{
  "progressPercent": 60,
  "expectedVersion": 4
}
```

`progressPercent` is required; omission is rejected rather than defaulting to
zero. Task progress is an integer from 0 through 100 and is directly writable
only for a leaf. Done Tasks remain 100; Cancelled Task progress is immutable.
Milestone progress is 0 or 100 and requires a Milestone date. Parent progress
is derived.

Parent derivation uses direct, non-deleted canonical Task-kind children only;
Milestones and non-Task rows do not contribute. The shared Task transition
contract also enforces:

- no new subtask beneath a Done/Cancelled parent until that parent is reopened;
- no terminal-child reopen while its parent remains terminal;
- parent Done only when every direct canonical Task child is Done or Cancelled
  and canonical derived progress is 100; and
- rejection of an all-cancelled child set because its derived progress is 0.

A successful schedule or progress response contains the authoritative
`taskId`, `kind`, planned/Milestone dates, progress, new `version`, and
non-blocking `warnings`. A stale expected version returns HTTP 409; clients
must refetch the HTTP snapshot before retrying. The Schedule UI preserves only
the safe edit intent across that refetch and then offers explicit Retry against
the latest version or Discard; it never silently replays a stale command.
Review-override completion applies the same parent completion guard. Restore
and delete of a child are rejected while its parent remains terminal.

### Dependency commands

Add request:

```json
{
  "predecessorTaskId": "00000000-0000-0000-0000-000000000000",
  "dependencyType": "FinishToStart",
  "expectedVersion": 4
}
```

The route Task is the successor. `predecessorTaskId` and `dependencyType` are
required, and the type is the canonical JSON string `"FinishToStart"`, not a
numeric enum. The expected version is the successor Task version. Unknown JSON
members are rejected, including `lag` and `lead`. Add rejects self, duplicate,
cycle, non-FS, cross-Project, deleted, unknown, hidden, and unauthorized
neighbors without exposing hidden metadata. Delete requires the same
successor-version check and refuses legacy non-FS rows as read-only. Neither
command moves either Task's dates. Bounded dependency inventory/cycle/warning
reads filter to active same-Project canonical Task neighbors. Visible rejected
attempts are audited with metadata-safe reason codes only. Successful
dependency and Task-lifecycle writes also advance the shared Project revision;
this makes a concurrent neighbor deletion versus dependency-add race fail
optimistically instead of committing a stale edge.

### Warnings and safe failures

Snapshot and successful command warnings are separate from blocking errors.
The vendor-neutral warning shape is:

```json
{
  "code": "DEPENDENCY_VIOLATION",
  "message": "The predecessor is planned to finish after the successor starts. No dates were changed automatically.",
  "severity": "Warning",
  "targetType": "Dependency",
  "targetId": "00000000-0000-0000-0000-000000000000",
  "field": "plannedStartDate",
  "blocking": false
}
```

PR06 warning codes are `DEPENDENCY_VIOLATION`,
`MISSING_ACTIVE_PLANNED_END`, `PARENT_DERIVED`,
`LEGACY_DEPENDENCY_TYPE`, `MILESTONE_DATE_REQUIRED`, and `UNSCHEDULED`.
A warning alone does not reject a valid command or trigger cascading movement.
Where a dependency endpoint references a parent Task, date warnings use the
canonical derived parent dates.

PR06 snapshot/command/dependency failures use:

```json
{
  "requestId": "trace-id",
  "error": {
    "code": "GANTT_STALE_VERSION",
    "message": "Work item has changed. Refetch and retry.",
    "target": null,
    "details": [],
    "redactionApplied": false
  }
}
```

The routes distinguish authentication `401`, authorization `403`, safe
not-found `404`, validation/invalid dependency `400`, and optimistic conflict
`409`. Redacted failures do not include hidden titles/neighbors, Tenant
internals, stack traces, SQL, or raw exceptions.

Request-aborted cancellation propagates and is not converted into a 500.
Unexpected snapshot exceptions return the same safe envelope with
`GANTT_REQUEST_FAILED`; unexpected schedule/progress or dependency exceptions
use their safe command failure code.

HTTP is authoritative. SignalR payloads are invalidation/version hints and are
never accepted as schedule, progress, or dependency commands. If realtime
Project subscription reauthorization is denied, the client synchronously
clears protected Kanban and Gantt state and invalidates in-flight request
generations before starting authoritative HTTP revalidation.

## Uploads

Upload endpoints must:

- Authorize the user and target resource.
- Check feature flags and quotas.
- Validate file size, extension, and MIME type.
- Store metadata in PostgreSQL.
- Store bytes through `IFileStorageService`.
- Generate tenant-namespaced storage keys.
- Return metadata DTOs, not raw filesystem paths or permanent object URLs.

Storage and metadata are separate failure domains. Upload implementations must clean up newly written bytes if metadata/audit persistence fails, and local storage should publish completed files atomically rather than writing directly to the final path.

Messaging must reference canonical, authorized file or attachment IDs. It must not accept client-supplied storage keys, stored filenames, or internal file paths as proof of an uploaded file.

## CSRF And Browser Calls

When cookie auth and `Security:EnableCsrfProtection` are enabled, unsafe browser requests must include the `X-CSRF-Token` header obtained from `GET /api/security/csrf-token`.

Safe `GET` requests do not require a CSRF token.

## Testing API Changes

For API changes, update or add tests according to risk:

- Unit/service tests for Application authorization and validation.
- HTTP integration tests for auth, CSRF, tenant resolution, and route behavior.
- Tenant isolation tests for tenant-owned resources.
- Upload tests for validation and authorization.
- API smoke examples in `docs/API_SMOKE_TESTS.http` when endpoint behavior changes.
