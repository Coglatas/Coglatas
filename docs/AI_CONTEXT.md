# AI Context

This is the primary entry point for future Codex work on Coglatas.

Last broad repository audit: **2026-08-02**. WPC-02B Workspace-create backend
status update: **2026-08-24**. WS-02 active-Workspace/context-header and WS-03
Workspace-create UI update: **2026-08-24**. Issue #342 Task state-list
candidate update: **2026-08-24**. Issues #350 structured Task Brief and #369
Task progress/activity candidate updates: **2026-08-24**. Issues #410
Task-create and #354 advisory quality-checklist candidate updates:
**2026-08-27**. Announcement audience-contract, #378 immediate-publish,
and #382 local-preview candidate updates:
confirmation candidate update: **2026-08-26**.
Audit UI keyboard/focus/status and #389 initial-load/manual-retry candidate
updates: **2026-08-27**.
P0 authorization-recovery candidate update: **2026-08-27**.
Issue #330 Workspace Continue-working candidate update: **2026-08-28**.
Issue #341 Files search and Filter Chips candidate update: **2026-08-30**.
Issue #348 Files batch action bar candidate update: **2026-08-31**.
Issue #360 File sharing-state candidate update: **2026-09-01**.
Issue #344 Audit filters and saved views candidate update: **2026-08-30**.
Issue #339 Task context summary candidate update: **2026-08-30**.
Issue #333 Workspace sharing-header candidate update: **2026-08-31**.

## Documentation authority

Use this order when claims conflict:

1. Current implementation and configuration under `src/`, `tests/`, `.github/`, and the root deployment files.
2. Active status documentation: this file and `docs/KNOWN_ISSUES.md`.
3. Focused active documentation under `docs/`.
4. `docs/ROADMAP.md` for intended future direction.
5. `docs/archive/` only as historical context.

Do not infer that an entity, configuration property, controller route, or archived plan proves a complete product workflow.

## Status labels

- **Implemented**: wired into the application with direct source evidence.
- **Partially implemented**: some layers exist, but an important workflow, UI, adapter, test, or enforcement layer is missing.
- **Planned**: no current implementation.
- **Deprecated**: compatibility-only or historical.
- **Needs verification**: environment or runtime evidence is missing.
- **Inferred**: conclusion drawn from code, explicitly identified as such.

“Implemented” does not mean production-ready.

## Verified stack

- .NET 10 / ASP.NET Core: project files under `src/`.
- EF Core 10 with Npgsql/PostgreSQL: `src/Coglatas.Infrastructure/Coglatas.Infrastructure.csproj`.
- Cookie authentication: `src/Coglatas.Web/Program.cs`.
- Angular browser UI source: `frontend/`; hosted build artifacts are copied to `src/Coglatas.Web/wwwroot/`.
- xUnit tests: `tests/Coglatas.Tests/`.
- Playwright and axe UI tests: `tests/ui/`, with static Angular/mock coverage,
  a canonical mobile compatibility matrix across Chromium device emulation,
  WebKit device emulation, and an explicit 320-CSS-pixel touch context,
  an isolated Compose-backed MVP0 real-backend smoke for cookie, CSRF, and
  seeded workflow compatibility, and an Issue #481 protected public-HTTPS
  deployment gate. The latter requires target-environment fixture configuration
  and execution evidence; it is not satisfied by a local or Compose run.
- Docker and Docker Compose: `Dockerfile`, `docker-compose*.yml`.

## Architecture

The application is one deployable ASP.NET Core process split into four projects:

- `Coglatas.Domain`: entities, enums, and shared domain types.
- `Coglatas.Application`: service interfaces, use cases, DTOs, authorization, feature/quota logic.
- `Coglatas.Infrastructure`: `AppDbContext`, migrations, repositories, local files, audit, notifications, search, hashing.
- `Coglatas.Web`: startup, middleware, controllers, authentication, tenant resolution, and hosted frontend artifacts.

Project references enforce a conventional dependency direction. See `docs/ARCHITECTURE.md`.

## Implementation status matrix

| Capability | Status | Source evidence and qualification |
| --- | --- | --- |
| Host, controllers, middleware, hosted Angular frontend | Partially implemented | `src/Coglatas.Web/Program.cs`, `Controllers/`, `AngularSpaFallback.cs`, `frontend/`; Angular build artifacts are required in `wwwroot/` for user-facing routes |
| Cookie auth, login/logout, password change | Implemented | `Application/Auth/`, `Web/Controllers/AuthController.cs` |
| Database-backed session revocation/expiry/user-state checks | Implemented | `Auth/UserSessionService.cs`, `Web/Security/DbSessionCookieAuthenticationEvents.cs` |
| Login lockout | Implemented | `Auth/AuthService.cs`; production defaults enable it |
| Password reset | Planned | Admin reset endpoint only records an audit event |
| Initial admin bootstrap | Implemented | `Program.cs` reads `COGLATAS_SEED_ADMIN_ENABLED`; `AppDbContextSeed.SeedLocalAdminAsync` creates or updates a platform administrator through `IPasswordHasher` and default-tenant owner membership |
| Invite registration | Partially implemented | User/session creation exists; tenant/workspace membership creation is missing |
| Tenant resolution | Implemented | Host, subdomain, session, development header, and config-default strategies in `HttpTenantResolver.cs` |
| Tenant query isolation and save stamping | Implemented | Global filters and save rules in `AppDbContext.cs` |
| Tenant isolation confidence | Partially verified | In-memory HTTP tests and conditional PostgreSQL tests exist; target-environment verification is still required |
| Platform and tenant administration APIs | Implemented | `PlatformTenantsController`, `TenantAdministrationController`, application services |
| `Platform:*` configuration switches | Partially implemented | Properties are bound; only setup mode is consulted by startup validation |
| Database tenant feature flags and quotas | Partially implemented | File uploads, exports, integrations, and UI shell use them; broad module gating is incomplete |
| `Features:*` appsettings switches | Documentation mismatch | Bound in DI but not used to gate controllers/services |
| Workspaces/groups/channels/posts | Partially implemented; WS-01 dashboard, WS-02 context header, Issue #333 sharing header, WPC-02B canonical Workspace create, and WS-03 create UI candidate | REST layers exist. WS-02 resolves active Workspace from a valid explicit Workspace route, then a Tenant/user-scoped last-used preference, then the sole authorized active Workspace, otherwise explicit selection; it never selects API row zero. It clears protected feature state and resource subscriptions before activating a different scope. The header separates capability-derived Workspace actions from global Notifications/Account/Logout and presents backend-authorized `Active`/`Review` Project counts as textual Running/Needs review state; legacy `inProgressProjectCount` remains their sum. Issue #333 adds a server-owned boolean aggregate for active Project-scoped users without active Workspace membership, keeps the exact count permission-filtered, and presents an avatar stack plus textual `External` state and capability-derived Workspace sharing action; it never grants Workspace access or infers External state in the browser. WPC-02B makes canonical production Workspace creation available to a current Tenant Owner/Admin or a current active Tenant-scoped `workspace.create` grantee. One transaction commits the idempotency claim, active Workspace, creator Owner membership, canonical `WorkspaceGeneral` Conversation and creator participation, `WorkspaceCreated` audit, and authorization Outbox events. The Issue #408 candidate consumes backend `canCreate`, exposes the capability-gated `/workspaces` dialog, keeps one stable `Idempotency-Key` for an unchanged uncertain retry, and activates a verified 201 resource only through the refreshed authorized WS-02 selection boundary. A committed create whose list/selection step fails uses GET/selection-only recovery and never repeats POST. Local preference, hidden controls, and the dialog capability check are not authorization. See `docs/verification/ws-02-workspace-context-header.md`, `docs/verification/p0-workspace-sharing-header.md`, and `docs/verification/ws-03-workspace-create.md`. |
| WPC Project creation/activation | Backend implemented; Issue #409 full create/activation UI candidate | WPC-02A persists canonical Visibility and activation provenance while leaving legacy rows explicitly unknown. WPC-02C implements idempotent, Workspace-scoped `POST /api/workspaces/{workspaceId}/projects`; WPC-02D implements explicit `POST /api/projects/{projectId}/activate` with ProjectGeneral and Task-workflow provisioning under one guarded persistence boundary. Issue #409 adds a backend-owned create-options projection, Workspace-scoped Project list filtering, a named Group/Visibility/date create dialog, a non-operational Draft Overview, and the separately authorized activation action. The browser keeps the same `Idempotency-Key` for an unchanged uncertain retry, records a strict 201 before follow-up reads, and uses GET/navigation-only recovery after a committed create. It never infers Group, Visibility, create, or activation authority from roles or client state. The deprecated unscoped `POST /api/projects` remains disabled. See `docs/verification/wpc-final01-integration-audit.md` and `docs/verification/p0-project-create-activation.md`. |
| Workspace Continue working | Issue #330 implementation candidate | The Workspace-scoped Projects landing presents at most six recently opened Research/Project and successfully downloaded File records. Browser storage is a strict versioned, Tenant/user/Workspace-partitioned opaque list containing only kind, resource UUID, and local open time; it holds no label, status, capability, grant, token, or authorization result. Up to three exact Project/File detail reads reauthorize current metadata before any card renders. Revoked or mismatched records are pruned; transient failures retain only the opaque entry and render no stale metadata. Authorization/session/identity/Workspace boundaries synchronously clear and cancel the projection, including same-session realtime authorization invalidation. A Task never becomes a card: authorized Task detail advances only its parent Project. File recency advances only after the grant-backed Blob is successfully handed to the browser. See `docs/verification/p0-continue-working.md`. |
| Messaging | Partially implemented; #343 direct actions, #362 canonical Message threads, and #368 private follow-ups | REST, direct-message recipient search, direct conversation creation, browser send/read persistence, and durable realtime message/unread reconciliation exist. Issue #362 adds same-Conversation Message threads through nullable `Message.ThreadRootMessageId`: authorized root summaries appear on the main timeline, replies are excluded from it, and exact bounded read/post routes provide a pinned root plus durable reply tombstones. A deleted root with durable replies remains a bodyless, ordered, reopenable timeline anchor; ordinary deleted zero-reply Messages remain omitted, and delayed Created/Updated events cannot revive a tombstone. Thread root/reply author names and participant names require historical same-Tenant and same-Conversation membership proof; lifecycle state does not erase a legitimate historical name. The first reply requires current `CanCreateThread` and posting authority; later replies require posting authority. The Angular channel and DM surfaces share a separate-draft contextual thread panel/dedicated mobile pane and reconcile metadata-only thread invalidations and delete ordering through the authorized HTTP projection, including stable in-panel focus when deletion disables the composer and timeline focus fallback when its trigger disappears. Legacy `ConversationType.Thread` / `ParentConversationId` rows remain compatibility-only and are never guessed into Message-thread anchors. The Issue #343 action surface keeps Reply and participant-private Save for later as direct accessible controls, while More holds server-authorized Edit, Delete, and generic Report request actions; it does not infer moderator capability. The saved marker is the #368 Tenant/user/Message follow-up identity, independent of read state and Conversation-level Later. React/reactions, Copy, version-precondition/history, a 24-hour sender-delete rule, general zero-reply tombstone presentation, and report evidence/case workflow remain separate contracts. Global Message notification delivery is stored per active Tenant membership, while conversation mute state remains conversation-specific; both suppress ordinary and mention notification rows without suppressing conversation activity or realtime delivery. WPC-01 makes Project-scoped direct-message reuse exact; production PostgreSQL conversation pages/counts, unread/update polling, detail, and Message Search share a depth-bounded recursive legacy Conversation-Thread boundary. Missing/inconsistent/cyclic or deeper-than-32 ancestry fails closed, and creation cannot persist an unreadable level-33 child. Safe attachment ownership remains incomplete. See `docs/verification/p0-message-actions.md`, `docs/verification/p1-message-thread-context.md`, and `docs/verification/p2-message-follow-ups.md`. |
| Announcements | #378 durable announcement delivery candidate; #382 local preview and #387 timezone contract candidates | The server-authorized audience projection returns only an authorized scope, display name, estimated recipient count, and its organizational schedule timezone; it does not disclose recipient identities. The production Angular editor has distinct Save draft, local Preview, and one-shot confirmation actions. Its server-owned `AnnouncementDraft` lifecycle is `Draft -> Scheduled -> Published`: both an immediate Publish and a user-scheduled confirmation first persist Scheduled work, and only the bounded PostgreSQL-coordinated worker may create the normal Announcement after due-time author/audience reauthorization. Scheduling resolves Workspace timezone, then Tenant timezone, then UTC; the UI displays that value during Schedule input and Review, while the server rejects a stale/substituted zone, DST gaps, and unresolved overlaps. The accepted local value, IANA zone/overlap offset, and immutable UTC instant survive server-timezone and later setting/tzdb changes. Create and delivery commands use Tenant/actor/operation idempotency plus optimistic versioning. Browser UI never invents a published item or delivery completion. Draft read/mutation is current-author/current-target reauthorized and cross-scope denial is redacted. CTA/link, attachments, cohorts, analytics, recurring scheduling, and cancellation remain excluded (#382/#383 are not absorbed). See `docs/verification/p0-announcement-publish-confirmation.md` and `docs/verification/p1-announcement-local-preview.md`. |
| Admin audit UI | Partially implemented; #344 filter/saved-view candidate; #349 exact-event metadata candidate; #386 accessibility slice merged; #389 initial-load/manual-retry candidate | The Angular route consumes only server-authorized `AuditGridRowResponse` rows and counts. Issue #344 adds backend-owned global search plus Severity, Type/Action, Actor, Source/entity type, Status/result, and relative UTC time filters; canonical applied-condition chips; Clear all and filtered-empty recovery; shareable URL inputs; and strict browser-local saved views partitioned by authenticated Tenant/platform scope and user. Actor filtering requires independent `audit.sensitive_metadata.view`, ordinary global search excludes Actor names without it, and every URL/view/retry reissues the authorized query. Saved views persist no rows, counts, metadata, capabilities, or grants. Issue #349 separately adds explicit progressive disclosure through `GET /api/admin/audit-grid/{auditId}/sensitive-metadata`; it does not define or consume #340 Claims/Evidence. The accessibility slice and #389 candidate retain keyboard/focus-safe desktop/320px inspection, a structural initial-load skeleton, and manual single-flight retry only for transient failures. Audit Review save, exports, server-shared views, immutable snapshots, duration, and Claims/Evidence remain separate contracts. See `docs/verification/p0-audit-filters-saved-views.md`, `docs/verification/p0-audit-sensitive-metadata.md`, `docs/verification/p1-audit-ui-accessibility.md`, and `docs/verification/p2-audit-load-retry.md`. |
| Projects/tasks/milestones/assignments/comments/Gantt data | Partially implemented; PR06 merged, Issues #342, #346, #354, #369, and #410 candidates, #357 scope foundation and #461 runtime contract, large-project delivery deferred | PR02 adds versioned Task workflow, relationship, review, Claim, and FS-authoring command routes. PR05 adds the canonical Project Kanban snapshot/config/move flow. PR06 upgrades the existing Project Detail Schedule tab and Gantt route with a bounded scheduled/unscheduled projection, manual schedule/progress/FS dependency commands, canonical Task-only parent derivation and terminal parent/child guards, optimistic concurrency, explicit conflict Retry/Discard, structured warnings, accessible/mobile alternatives, lazy vendor isolation, and authoritative realtime refetch. The Issue #342 candidate adds the configured Stage name, string fixed category, independent Blocked state, semantic last-update time, and a backend-authorized boolean-only Task-linked Artifact signal to the maintained desktop and 320-pixel Task list. The Issue #346 candidate adds strict, versioned, browser-local My Tasks saved-filter snapshots scoped by Tenant/user/screen, plus Running, Needs review, and Completed presets. Applying and clearing conditions issue one authoritative list/count pair, preserve explicit Workspace scope, persist no result or authorization data, and present saved opaque Project IDs generically while the backend reauthorizes them. The Issue #369 candidate makes that configured Stage the Task-detail current phase and exposes already-existing Task-linked ActivityLog records through a separate, lazy, bounded page under the existing Project read boundary; it adds no production Activity writer and does not make Task commands produce execution history. Activity failure is independent of the current phase; realtime remains an invalidation hint for authoritative HTTP reads. Activity does not synthesize historical Stage transitions or percentages and `Issue` is presented as `Needs attention`, not as Task `Failed`. Issue #357 supplies the server-authorized Project default, complete Task override/inherit policy, immutable next-run policy snapshot, and Task-detail summary for `WebEnabled` and `ProjectFilesEnabled`. Issue #461 selects `FirstPartyProjectFilesRuntimeV1`: an idempotent request now records an immutable provider/version and durable `Accepted` state, while Web execution remains disabled and fails closed. Acceptance invokes no provider or worker; #462 owns post-commit Project File materialization and #463 owns durable result persistence/retrieval. The policy UI exposes no source inventory, URL, file content, credential, browser runtime authority, or outbound-Web behavior. The Issue #410 candidate adds a server-authorized Project-scoped Task-create options read and strict idempotent side-by-side create command while retaining the legacy Task POST. It supports optional milestone, schedule, and Brief data plus manager-only initial primary-assignee or complete Task source-scope override selection. Issue #354 adds a local, advisory pre-create review of the optional Task Brief and effective source policy: it never blocks Create, does not infer Project Brief defaults, and focuses a missing Brief field on request. One transaction stages the Task, automatic watches, optional override, audit, invalidations, and initial-assignment notification; it creates neither a Task execution run nor an execution-policy snapshot. The browser opens the form from Project Detail, keeps unsent input in the current tab only, and uses an authoritative 201 followed by Task-detail navigation. It offers no Start action, runtime, Web retrieval, raw source persistence, or provider behavior. The fixed Task categories remain Backlog/Todo/InProgress/Review/Done/Cancelled; no Task Failed category exists. Large-project pagination and virtualization remain open under `TASK-V1-PR06B`. See `docs/TASK_V1_PR02.md`, `docs/TASK_V1_PR04.md`, `docs/TASK_V1_PR05.md`, `docs/TASK_V1_PR06.md`, `docs/decisions/issue-461-first-party-project-files-runtime-v1.md`, `docs/verification/p0-task-state-list.md`, `docs/verification/p0-task-progress-activity.md`, `docs/verification/p0-task-execution-scope-foundation.md`, `docs/verification/p0-task-create.md`, and `docs/verification/p0-task-quality-checklist.md`. |
| Structured Task Brief | Implemented in PR #418 (Issue #350); Issue #410 Task-create candidate integration | `TaskItem` has additive nullable Goal, Deliverable, and Constraints storage, each limited to 4,000 characters. Canonical Task detail returns each value with explicit `taskSpecific` or `notSet` provenance; Project context remains separately authorized and is never copied from `Project.Description`. Existing free-form `TaskItem.Description` input and older JSON remain compatible. The maintained Task editor and the Issue #410 Project-aware Task-create candidate reuse the accessible, responsive Brief component and same-order review. The create surface records a Task only; it does not start a runtime or execution. Issue #369's separate Activity read and phase presentation sit alongside this detail contract and do not change Brief fields or provenance. Project Task lists stay compact. See `docs/verification/p0-task-brief.md` and `docs/verification/p0-task-create.md`. |
| Events/attendance/calendar | Backend implemented; browser UI planned | Controller/service/repository/tests exist; calendar route is a placeholder outside dashboard summary |
| Forms/surveys | Backend implemented; browser UI planned | Controller/service/repository/tests exist; `/forms` is a placeholder |
| Notifications | Partially implemented; PR07-D backend/UI foundation present | PR #274 merged the private Workspace digest-preference and logical-key foundation at `c5627eb09ecf19d66146eacdbc3e938c0a1c8563`; PR #275 merged immediate Task Notification production at `93b1c5e260e04c243ff84f7370aca4d869484087`; PR #277 merged the deadline-digest ledger/worker at `8d0b8b20551076ecd73ead06aced4b80c94749e7`. Current target resolution gates Task/digest, Artifact through Project visibility, and Message through recursive Conversation visibility across list/unread/mutation/open and delayed delivery. Task/digest created events alone are reference-only; Artifact/Message retain the legacy embedded shape but are reauthorized before every delivery attempt. The Angular supported-target union still does not bind Artifact/Message navigation. `tasks.notificationsV1` remains default-off. |
| Search | Partially implemented | Project-derived results use the same current SQL-translatable Project read boundary as detail and the non-Archived list scope for Project, Task, Artifact, ActivityLog, Comment, and project-bound Message results. Project list alone preserves current-Workspace explicit-member Archived history; Search, detail, and subordinate reads remain stricter. PostgreSQL Message Search constrains all matching Messages by the shared recursive readable-Conversation relation before deterministic `CreatedAt DESC, Id ASC` ordering and the final bounded result; no arbitrary pre-authorization Conversation cutoff remains. The relation is capped at 32 Thread levels. WPC-02A persisted canonical Visibility is enforced by the shared Project boundary, while legacy `NULL` remains explicitly unclassified. The `/search` UI remains unavailable. |
| Local filesystem files | Partially implemented; Issues #341, #345, #348, #356, and #360 candidates | Authorization, policy, repository, and storage exist. The Workspace inventory is a bounded server-authorized list and projects a per-row `canDelete` presentation capability without per-row authorization queries. The Files search surface uses the same `/api/search` Workspace File boundary for filename plus Type, Modified, and current-uploader facets; backend Search owns filtered membership/count, while a strict browser adapter rejects mismatched File/Workspace records and never consumes snippets or storage paths. Applied facets remain visible as removable accessible chips, and protected query/results clear with Workspace or authorization state. Issue #348 adds a page-local selection action bar plus an opaque server-owned all-search-results snapshot: capture is actor/Tenant/Workspace/query/facet bound, five-minute, and hard-capped at 100 current authorized FileObject identities. Snapshot delete is consumed once, reloads and reauthorizes every item, and reports a best-effort non-atomic outcome; it is not a client-ID authority grant. Issue #360 adds a persisted direct-Workspace `Private`/`Workspace` baseline plus current effective grants. List, search, detail, and Preview use a server-owned `Private`/`Workspace`/`External` projection; only a current Workspace sharing manager receives an external-recipient count or recipient/candidate identity. Grant eligibility is re-evaluated from current Tenant/user, Workspace, and external Project membership for every discovery/read/download path. Sharing changes are versioned, audited, invalidate stale download grants, and refresh visible list/Preview state. No public link or email-as-authority is introduced. Move, rename, folder search, broader uploader enumeration, and an atomic batch-delete contract remain absent. One responsive inspector switches between grant-backed Preview, staged Details, and an explicit unavailable Activity state with accessible tab semantics; it renders only authorized list/search fields, exposes no broad Audit/activity/version query. File-specific activity/version history remains for Issue #363. Upload/database failure cleanup and controlled missing-file handling are still incomplete. See `docs/contracts/file-batch-selection-v1.md`, `docs/verification/p0-files-search-filter-chips.md`, `docs/verification/p0-files-contextual-actions.md`, `docs/verification/p0-files-inspector-tabs.md`, and `docs/verification/p0-files-sharing-state.md`. |
| Object storage | Planned | Unsupported adapter is selected for object-storage provider names |
| Tenant export | Partially implemented | Metadata ZIP only; excludes file bodies; no restore |
| API token records and validator | Foundation only | No request authentication handler, tenant binding, or scope middleware |
| Webhook records and validation | Foundation only | “Test” validates configuration and sends no outbound request |
| UI shell data model | Foundation only | Modules/panels/layouts/commands/radial-menu APIs exist; radial UI control is disabled |
| SignalR and transactional Outbox | Messaging, Project Kanban, and PR06 Schedule integration implemented; exact final-HEAD PR06 real-transport Gate pending | Authenticated `/hubs/app`, server-authorized subscriptions, durable Outbox persistence, dispatcher retry/dead-letter/retention, diagnostics, and Angular reconnect/catch-up exist. PR05 uses committed Task/Project invalidations for Kanban. PR06 transactionally queues Task/Project schedule invalidations and treats them as version hints for authoritative Gantt HTTP refetch, including active-edit queuing, reconnect, degraded HTTP behavior, and synchronous protected Kanban/Gantt clear plus generation invalidation when Project subscription reauthorization is denied. Historical exact `2fc5910` licensed smoke run `30639800642` passed all six scenarios with 0 failed/skipped; it is not final evidence after latest-main integration and scope cleanup. |
| Billing/payments, SSO/MFA, general-purpose/external job orchestration | Planned | In-process Outbox and PR07-C digest hosted workers exist; no general-purpose external job runner was found |

### Current Message discovery candidates

- Issue #359 adds an expanded desktop Messages search and a one-action mobile
  search/filter disclosure. Text matches come only from the existing bounded,
  server-authorized `type=Message` Search projection. Conversation inbox
  conditions remain visibly separate from server Message matches; Issue #355
  upgrades those conditions to the authoritative All/Unread/Mentions/Later
  projection described below. Every condition is explicit and removable, and
  zero results provide Change search and Clear all recovery.
- Issue #367 adds explicit server-evaluated From, local-calendar date range,
  Message Read/Unread, and safe-attachment facets to the existing bounded
  Message Search projection. The sender option endpoint returns only display
  names already present in currently readable Conversations. Applied
  non-sensitive conditions may replay through the URL, while free-text and
  snippets remain memory-only and recognized private query keys are scrubbed.
  The attachment facet recognizes only clean, classified, scope-consistent,
  pre-existing Message-owned file links; malformed, quarantined, or legacy
  metadata-only rows count as `Without`. It does not enable attachment upload
  or resolve BE-004, which remains critical and open. See
  `docs/contracts/message-search-filters-v1.md`,
  `docs/contracts/message-advanced-filters-v1.md`,
  `docs/verification/p1-message-search-filters.md`, and
  `docs/verification/p2-message-advanced-filters.md`.

### Issue #355 Conversation inbox candidate

The Conversation list now has one server-authorized, paginated All/Unread/
Mentions/Later projection. Each category total is a Conversation count over the
same recursive current-readable relation used by production Messaging; rows
outside that relation contribute no count or metadata. Unread remains derived
from the current user's read cursor, Mentions from unread recipient-owned
Message Mention Notifications, and Later from a new private
`ConversationMember.IsLater` Boolean. Updating Later uses the existing
self-participant state route and does not mutate read or Mention state. The
Angular Messages surface consumes these server counts and rows; frontend
visibility is not authorization. This is Conversation-level inbox deferral
only. It does not create a saved-Message identity, completion state, reminder,
or notification schedule; those per-Message follow-up semantics remain owned by
Issue #368.

### Issue #339 Task context summary candidate

The maintained Task detail source-scope component adds a compact summary of
the server-authorized Issue #357 projection. Its count is only the number of
enabled generic source kinds (`Web` and `Project files`), from zero through
two; it is explicitly not a file, site, App, integration, or resource
inventory count. The summary distinguishes Project default from Task override,
moves keyboard focus to the detailed context, and shares the existing
authoritative refetch, generation, and protected-state clearing boundary. It
adds no API, source inventory, execution provider, or runtime behavior. See
`docs/decisions/issue-339-task-context-summary.md` and
`docs/verification/p1-task-context-summary.md`.

### Issue #368 saved Message follow-up candidate

Saved Messages are now participant-private work state, independent from both
`ConversationMember.IsLater` and every read cursor. A unique
`(TenantId, UserId, MessageId)` row supports idempotent save/complete through
`/api/me/message-follow-ups`; list counts and rows compose the current canonical
readable-Conversation relation before paging, so revoked, removed, deleted, or
cross-Tenant targets disclose neither bodies nor metadata. The Angular
`/messages/saved` surface links to an authorized anchor-message load and focuses
the exact Message (or its exact thread reply after opening the root with the
authorized bounded `anchorReplyMessageId` projection, even outside the ordinary
latest 100 replies). No
read-state route is called by save, open, or complete. Reminder scheduling is
explicitly absent because the repository has no Message reminder scheduler or
delivery contract. See `docs/verification/p2-message-follow-ups.md`.

### Current P0 authorization-recovery candidate

- On the existing #410 Task-create route, a same-route authorization reset
  clears all server-owned options, capabilities, request IDs, and mutation
  state. It retains only opaque route intent for the same identity, then
  requires a fresh authorized options response before restoring the form or
  allowing a create command; it never reuses authority or submits
  automatically.
- A connected #378 Announcement client correctly clears protected editor and
  review state on an authorization invalidation. The stale-client P0 proof
  delays only Hub transport in an isolated browser context; cookie, CSRF,
  audience GET, membership DELETE, and final POST remain real and the server
  still independently reauthorizes the selected scope.

## Status groups

### Implemented

- Modular ASP.NET Core host and REST controllers.
- EF Core/PostgreSQL model and migrations.
- Cookie login/logout/password change, CSRF, lockout, and database session validation.
- Tenant resolution, global query filters, tenant stamping, and inactive-tenant write rejection.
- Broad application services for collaboration, projects, forms, events, files, notifications, search, audit, and administration.
- Local filesystem storage and metadata-only tenant export.

### Partially implemented

- Invite registration and tenant user invitation.
- Tenant feature flags and quotas.
- Platform/tenant administration workflows.
- Browser UI outside auth, dashboard, messaging, announcements, notifications, and projects.
- Deployment profiles, object-storage configuration, tenant export, API tokens, webhooks, and UI-shell customization.
- End-to-end tenant isolation and frontend/backend integration verification.

### Planned

- Password reset delivery, object storage, API token authentication, outbound
  webhooks, general-purpose/external job orchestration beyond the existing
  in-process workers, tenant restore, SSO/MFA, billing, automatic/advanced
  planning, and full docking/radial UI.

### Deprecated

- `SystemRole.SystemAdmin` is a compatibility alias for `PlatformAdmin`.
- Documents under `docs/archive/` are historical and may describe superseded behavior.

### Unknown or needs verification

- Provisioning method used by any existing deployment.
- Real Compose startup and target-environment behavior.
- Reverse-proxy scheme/host handling.
- First successful Issue #481 public-HTTPS deployment-gate execution against
  the intended TLS/proxy route.
- Latest CI status.
- Backup retention and successful restore evidence.
- Production data volume, performance, PostgreSQL version, and storage topology.

## Critical current constraints

- A fresh environment can create the first login user or PlatformAdmin only through the explicit `COGLATAS_SEED_ADMIN_*` startup seed.
- Invite acceptance does not create tenant/workspace membership.
- Object-storage examples are not deployable because the adapter is intentionally unsupported.
- `deploy/onprem/compose.yml` now stages a controlled SDK migration before the app, but fresh-stack production-profile startup evidence is still required.
- On-prem proxy configuration now binds the origin to loopback and validates an explicit forwarded-header trust boundary; target-host TLS/proxy evidence is still required.
- Angular browser UI coverage is materially smaller than backend API coverage.
- The regular Playwright suite mocks API contracts and does not prove
  frontend/backend compatibility. PR06 adds a real-backend scenario, but its
  licensed exact-final-HEAD hosted evidence is still pending.
- API errors are not standardized repository-wide despite
  `docs/API_CONTRACTS.md` describing a shared shape. PR06 aligns only its Gantt
  snapshot/command/dependency routes with a narrow safe envelope.
- Critical backend logic defects still affect scoped announcements,
  conversation persistence, and message attachments. The current PR #281
  candidate closes the
  confirmed Project-derived Search authorization mismatch under the current
  Project read policy.

Details and suggested issue titles are in `docs/KNOWN_ISSUES.md` and `docs/BACKEND_LOGIC_AUDIT.md`.

## Testing facts

The 2026-06-18 local audit observed 123 passing .NET tests. This result needs qualification:

- PostgreSQL integration tests are explicitly skipped locally when `POSTGRES_TEST_CONNECTION_STRING` is absent and fail under CI when it is absent; a passing CI run still supplies the required execution evidence.
- HTTP tests use Kestrel but mostly EF Core InMemory.
- Root Playwright legacy static-SPA specs are obsolete after the Angular migration; future Playwright coverage should target Angular build output or a hosted Angular app.
- CI supplies PostgreSQL and runs migrations before `dotnet test`.

TASK-V1-PR07-C is historical merged prerequisite evidence, not the current
worktree. Its dedicated verification record is
`docs/verification/task-v1-pr07-c-deadline-digest.md`. Conditional PostgreSQL
tests are the authority for migration, five-field identity, concurrent/expired
claims, exact attempt accounting, audited restart, integrated DST identity,
current candidate predicates, Notification/Outbox atomicity, and focused
query-plan evidence. An environment-unset run that reports those tests skipped
must not be promoted to PostgreSQL or completion evidence. Later PR07-D/current
source supplies notification-open and dispatch/replay authorization; those
capabilities must not be inferred from the older PR07-C evidence alone.

Historical TASK-V1-PR06 merge-time evidence from 2026-08-01 follows. It is
retained as evidence of the state before PR #259 merged, not as the current
status:

- Draft PR #259 is open and mergeable. Actual latest main
  `33c35cbc873fcdc78b75663d195ca120e2c01520` was incorporated by normal merge
  commit `1abce6c70d9f665b773d35f75d63c0d05a387cc8`. The active frontend manifest
  and lockfile conflicts were reconciled with Gantt 34.1.30 and main's Grid
  34.1.33 retained. Main queue v2/`queue: max`, Qodana/manual-smoke queue v2,
  Angular 21 architect compatibility, Compodoc 2.0.0, ESLint 10.8.0, globals
  17.8.0, lockfile version 3, and latest test tooling were retained.
- Commit `e8bdf47754ca38b6f4d1b3a31c945ae07432f06f` restored
  `messaging.facade.ts` and `messaging-ui.spec.ts` to actual `origin/main` by a
  forward commit. Both files are absent from `origin/main...HEAD`; the backup
  patch SHA-256 is
  `1099E128C2BBBE43D986C29427F82F2CBDB14371320FEC00E1B402DF628844DD`.
  Ahead/behind is 29/0 before the final documentation commit.
- Exact code-bearing candidate `1abce6c70d9f665b773d35f75d63c0d05a387cc8`
  passed .NET restore and Release build with 0 warnings/errors. PostgreSQL 18.4
  passed empty migration apply through
  `20260730120626_AddCanonicalGanttVersions`, PR05 upgrade/data-preservation/
  additive-down coverage, and pending-model check. PR06 was 49/49, PR05 25/25,
  PR04 8/8, and full backend 494/494, all with 0 failed/skipped.
- The same code-bearing candidate passed root and active/inactive frontend
  `npm ci`. Actual `origin/main` tracks no inspection-workspace lockfile, so
  its requested `npm ci` is unavailable; the documented no-lock install and
  full inventory succeeded. Angular passed 323/323 in 42 files, with production build, architecture 4/4,
  Syncfusion license policy 4/4, lazy-bundle analysis, 4 GB Storybook, and
  mocked Playwright 63 passed with 3 pre-existing expected skips. Default
  Storybook failed with an approximately 2 GB JavaScript heap OOM and is not a
  pass. Gantt remained a 5.42 MB lazy chunk; initial bundle was 949.99 kB.
- Local Node is `v24.13.0` and npm is `11.6.2`. Compodoc 2.0.0 itself supports
  Node 24 and executed, but its nested `@angular-devkit/core` 22.0.4 requires
  Node `^24.15.0`; the inactive workspace install therefore emitted an engine
  warning. No downgrade was made; the repository-specified Hosted Node 24
  toolchain is the Acceptance Gate.
- Local npm audit was root 0; active frontend 15 (3 low, 6 moderate, 6 high,
  0 critical); inactive frontend 8 (0 low, 5 moderate, 3 high, 0 critical).
  No affected audit entry referenced Syncfusion, and no forced fix was run.
- Exact-head attempt `5111784e72054db9501135888e72330672a8c975` passed
  Documentation CI, all CI jobs, npm Security Audit, licensed Real Backend,
  and the Qodana job. Code Quality nevertheless failed because the
  lockfile-free inspection install repeated stale `--prefer-offline` metadata
  resolution and reported `eslint@undefined` / `ERESOLVE`; downstream Angular
  quality steps were skipped, so the workflow is not a pass. Focused commit
  `8efa845dec5c553d5ff2107cf6edef7993141a8b` retains cache-first resolution on
  attempt one and refreshes online metadata on attempt two. No dependency,
  lockfile, queue, Qodana-policy, or test change was made.
- Documentation CI, CI, Code Quality, npm Security Audit, licensed Real Backend
  Browser Smoke, final artifact secret scan, and final review-thread check must
  rerun after the documentation commit on the exact final HEAD. Run IDs will be
  recorded in the PR body without another self-referential source commit.

Historical pre-remediation evidence remains in
`docs/verification/task-v1-pr06-gantt.md`; it is not the current status.

Post-merge resolution on 2026-08-01: PR #259 is merged at
`d5de01cf303c914c2b390346575a22cadb8b4443`. The decision was unresolved at
merge time. The owner subsequently approved the existing 500 combined-item /
2,000 active-dependency / typed HTTP 400 fail-closed safeguards as the
temporary PR06 full-snapshot contract. No successful partial snapshot or
silent truncation is permitted. These are not permanent Project or database
capacity limits; paginated and virtualized large-project delivery remains open
as [`TASK-V1-PR06B` issue #270](https://github.com/NYGsatoshi/Coglatas/issues/270).

Read `docs/TESTING.md` before using “tests pass” as evidence.

## What to read by task

Always start with:

- `docs/AI_CONTEXT.md`
- `docs/KNOWN_ISSUES.md`
- `docs/CODING_RULES.md`

Then add:

- Backend controllers, services, validation, files, or business logic: `docs/BACKEND_LOGIC_AUDIT.md`
- Architecture or module boundaries: `docs/ARCHITECTURE.md`
- Local work: `docs/DEVELOPMENT.md`
- Deployment/configuration: `docs/DEPLOYMENT.md`
- Auth, authorization, tenancy, secrets, files: `docs/SECURITY_MODEL.md`
- Schema, migrations, persistence: `docs/DATABASE.md`
- Test changes or verification claims: `docs/TESTING.md`
- Detailed entity fields: `docs/DATA_MODEL.md`
- API conventions: `docs/API_CONTRACTS.md`
- Active Workspace selection/context header:
  `docs/verification/ws-02-workspace-context-header.md`
- Workspace creation dialog/API/retry integration:
  `docs/verification/ws-03-workspace-create.md`
- PR07-C deadline-digest implementation/evidence:
  `docs/decisions/task-v1-pr07-c-deadline-digest-decisions.md` and
  `docs/verification/task-v1-pr07-c-deadline-digest.md`
- Canonical Gantt implementation/evidence:
  `docs/TASK_V1_PR06.md` and
  `docs/verification/task-v1-pr06-gantt.md`
- Operations and recovery: `docs/OPERATIONS.md`
- Intended future scope: `docs/ROADMAP.md`

Only read `docs/archive/` when historical decisions or earlier claims are relevant. Start at `docs/archive/README.md`.

## Rules for future audits

- Verify a feature across controller, application service, persistence, configuration, UI, and tests as applicable.
- Mark code-derived behavior as **inferred** when it has not been run.
- Mark environment claims as **needs verification** without deployment evidence.
- Treat configuration properties as inert until a code reader is found.
- Treat a route as backend-only unless the bundled UI actually exposes a working flow.
- Treat mocked UI tests as frontend behavior tests, not API integration tests. Do not treat removed legacy static-SPA selectors or mocks as UI contracts.
- Do not call an export a backup or restore mechanism.
- Do not call an archived status snapshot current.
