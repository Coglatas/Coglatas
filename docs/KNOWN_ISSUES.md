# Known Issues

Last WPC creation remediation audit: 2026-08-24.

This list records confirmed implementation/documentation mismatches and major unknowns. It is not limited to defects already filed in GitHub.

## WPC canonical creation status

The original WPC-01 items are no longer active backend blockers:

- **WPC-01-001 resolved by WPC-02A:** canonical Project Visibility and
  activation provenance are persisted. Legacy rows remain explicitly unknown;
  no migration inferred or broadened their authorization.
- **WPC-01-002 resolved by WPC-02C:** Workspace-scoped Project creation has an
  explicit server-authorized, idempotent command. The deprecated unscoped
  compatibility route remains disabled.
- **WPC-01-003 and WPC-01-004 resolved by WPC-02B:** current Tenant-scoped
  `workspace.create` grants are persisted and evaluated, and Workspace create
  atomically provisions the canonical `WorkspaceGeneral` Conversation.
- **WPC-01-005 resolved by WPC-02D:** the explicit activation command owns
  ProjectGeneral and Task-workflow provisioning under one guarded persistence
  boundary. Generic lifecycle update remains unable to bypass activation.

The provider-backed WPC-Final01 through Final03 verification records remain
the source of truth for those merged backend boundaries. Frontend integration
Issues such as Workspace create (#408) and Project create/activation (#409)
must consume them without duplicating authority in the browser.

## Backend application logic audit findings

The detailed controller, service, validation, error-handling, file, project, messaging, announcement, DI, and HTTP-status audit is maintained in `docs/BACKEND_LOGIC_AUDIT.md`.

The highest-severity confirmed findings are:

- **BE-001, resolved by the current WS-01-BE candidate:** Announcement list, detail/read-status, Search, and the Workspace dashboard unread aggregate now share the SQL-translatable `AnnouncementReadScope`. Its global, Workspace-only, Group, and Channel audience branches are mutually exclusive; public/announcement channels use Group membership, while private/confidential channels require explicit Channel membership.
- **BE-002, resolved by the current PR #281 candidate:** Project-derived Search,
  the non-Archived Project list, project-bound Messaging, and the Project
  boundary used by My Tasks now share one SQL-translatable form of the current
  `CanViewProject` policy. The list alone adds narrowly authorized Archived
  history for current Workspace members with explicit Project membership. This closes
  disclosure of Project, Task, Artifact, ActivityLog, Comment, and
  project-bound Message content without claiming canonical Visibility
  persistence. Production PostgreSQL Conversation list/count and Message Search
  now share one depth-bounded recursive ancestry relation and fail closed on
  missing, inconsistent, cyclic, or deeper-than-32 hierarchies. Message Search
  composes that relation over every matching Conversation before deterministic
  Message ordering and the final result bound; it no longer authorizes an
  arbitrary first 100 Conversation IDs. A real-PostgreSQL regression covers 125
  authorized matching Conversations, the newest authorized Message outside the
  former first 100, tied timestamps, and an unauthorized nested Thread with a
  revoked ancestor whose title/body remain absent. Thread creation cannot
  persist a child beyond that readable boundary.
- **BE-003, resolved for direct-message MVP:** direct conversation creation now derives an active shared workspace server-side instead of writing `WorkspaceId = Guid.Empty`; broader PostgreSQL coverage for all conversation creation modes is still recommended.
- **BE-004, critical:** message attachment requests trust client storage metadata and create records without required workspace/file-object relationships.
- **BE-005, high:** post update/delete/pin operations mutate entities loaded with `AsNoTracking`, so successful responses may not correspond to persisted changes.
- **BE-006, high:** assignee task filtering launches parallel EF operations on one scoped `DbContext`.
- **BE-007, high:** file and artifact uploads can leave orphaned or partial files when metadata persistence fails.
- **BE-008 through BE-012, high:** conversation read-state integrity, task notification targets, duplicate My Tasks rows, notification length limits, and HTTP/error mapping require focused patches.

Use the backend audit document for exact files, methods, impact, patch suggestions, test cases, validation gaps, and the issue-ready list. These findings supersede any broader status label that calls the affected workflow fully implemented without qualification.

## Frontend UI audit findings

Audit scope: browser rendering, navigation, dashboard behavior, login/register/admin UI, Japanese localization, responsive CSS, accessibility, broken links, and client-side JavaScript. These UI-001 through UI-024 findings describe the removed legacy vanilla-JavaScript SPA that lived under `src/Coglatas.Web/wwwroot`.

Status after the MVP-A P0 Angular migration: obsolete as active frontend defects. Do not treat the listed legacy routes, DOM selectors, mocked fixtures, JavaScript entrypoints, CSS bundles, screenshot behavior, or `wwwroot/scripts` files as P0 acceptance contracts. Re-open only defects that are reproduced against the Angular frontend under `frontend/`.

### UI-001: API-provided module routes do not match SPA routes

- Priority: high.
- Status: confirmed mismatch.
- Evidence: seeded module routes include `/dashboard`, `/messages`, `/files`, `/production`, and `/admin`; `main.js` implements `/`, `/dm`, `/artifacts`, `/projects`, `/tenant-admin`, and `/platform-admin`. `navigation.js` prefers `module.defaultRoute`, so its route aliases are ignored when the API supplies a route.
- Reproduction: seed UI-shell modules, authenticate, and select Dashboard, Messaging, Files, Production Tracking, or Admin.
- Expected: the implemented page opens.
- Actual: a placeholder page opens, or the wrong route is marked active.
- Files: `wwwroot/scripts/main.js`, `wwwroot/scripts/components/navigation.js`, `AppDbContextSeed.cs`.
- Suggested issue: **Canonicalize server-provided module routes before rendering navigation**.

### UI-002: Navigation listeners accumulate and duplicate history entries

- Priority: high.
- Status: confirmed client-side bug.
- Evidence: `renderNavigation` attaches click handlers, then every `renderRoute` call attaches another handler to every `[data-route]` link.
- Reproduction: navigate between several pages and use the browser Back button.
- Expected: one history entry and one render per click.
- Actual: duplicate `pushState` calls, repeated API loads, and multiple Back operations can be required.
- Files: `wwwroot/scripts/main.js`, `wwwroot/scripts/components/navigation.js`.
- Suggested issue: **Use one delegated SPA navigation handler**.

### UI-003: Mobile header overflows narrow viewports

- Priority: high.
- Status: confirmed CSS layout defect.
- Evidence: the brand has `min-width: 150px` and search has `min-width: 160px`; notification, language, logout, and navigation controls remain visible below 760px.
- Reproduction: open an authenticated page at a 320px to 393px viewport.
- Expected: all header controls remain reachable without horizontal scrolling.
- Actual: controls can overflow, become compressed, or be clipped.
- Files: `wwwroot/styles/app.css`, `wwwroot/scripts/components/shell.js`.
- Suggested issue: **Add a compact mobile-header layout**.

### UI-004: Mobile sidebar remains open after navigation

- Priority: medium.
- Status: confirmed behavior and accessibility defect.
- Evidence: the toggle only changes `is-sidebar-open`; route changes do not close it. The toggle lacks `aria-expanded` and `aria-controls`. Playwright helpers manually close the sidebar after navigation.
- Reproduction: open the sidebar on mobile and select Projects.
- Expected: the sidebar closes and focus moves to the new page.
- Actual: the sidebar remains over the rendered page.
- Files: `wwwroot/scripts/components/shell.js`, `wwwroot/styles/app.css`, `tests/ui/app.spec.ts`.
- Suggested issue: **Close and announce the mobile navigation drawer correctly**.

### UI-005: Navigation exposes placeholder-only pages

- Priority: medium.
- Status: confirmed missing UI; overlaps `KI-008`.
- Evidence: Workspaces, Groups, Channels, Tasks, Artifacts, Calendar, Forms, and search results render placeholder content.
- Reproduction: select any affected primary-navigation item.
- Expected: a functional page, or a clearly disabled/preview-labelled navigation item.
- Actual: normal-looking navigation leads to an unimplemented screen.
- Files: `wwwroot/scripts/components/navigation.js`, `wwwroot/scripts/pages/placeholders.js`.
- Suggested issue: **Hide or label placeholder navigation until workflows are implemented**.

### UI-006: Notification links target unsupported frontend routes

- Priority: high.
- Status: confirmed broken-link behavior.
- Evidence: notification routes can be `/events/{id}`, `/forms/{id}`, `/tasks/{id}`, `/artifacts/{id}`, `/messages/{id}`, or `/posts/{id}`; the SPA does not implement those detail routes.
- Reproduction: open a task, event, form, artifact, message, or post notification.
- Expected: the related detail page opens.
- Actual: the SPA renders a placeholder.
- Files: `wwwroot/scripts/pages/notifications.js`, `DbNotificationService.cs`, `wwwroot/scripts/main.js`.
- Suggested issue: **Only render notification links for supported routes**.

### UI-007: Login trims passwords before submission

- Priority: medium.
- Status: confirmed login UI bug.
- Evidence: the login handler calls `.trim()` on the password field.
- Reproduction: authenticate with a valid password containing a leading or trailing space.
- Expected: the exact entered password is submitted.
- Actual: whitespace is removed and authentication fails.
- Files: `wwwroot/scripts/components/shell.js`.
- Suggested issue: **Preserve password input exactly as entered**.

### UI-008: Login localization flashes Japanese and exposes English API errors

- Priority: low.
- Status: confirmed localization gap.
- Evidence: `index.html` contains a hardcoded Japanese login form before JavaScript applies the stored locale. API `{ error }` strings are displayed directly and are commonly English.
- Reproduction: store `coglatas.locale=en-US` and reload on a slow connection, or fail login while using Japanese.
- Expected: one consistent selected language.
- Actual: a Japanese login flash or mixed Japanese/English error text.
- Files: `wwwroot/index.html`, `wwwroot/scripts/api.js`, `wwwroot/scripts/components/shell.js`.
- Suggested issue: **Render the initial login state from the locale layer and define UI error localization rules**.

### UI-009: Invite registration has no browser page

- Priority: medium.
- Status: confirmed missing UI.
- Evidence: the backend exposes `POST /api/auth/register-by-invite`, but `AuthApi` and the SPA router have no invite-registration flow. Unauthenticated routes always display login.
- Reproduction: visit an invite-registration URL while signed out.
- Expected: a token-aware registration form.
- Actual: the login form is displayed.
- Files: `AuthController.cs`, `wwwroot/scripts/api.js`, `wwwroot/scripts/main.js`.
- Dependency: invite membership behavior remains incomplete under `KI-002`.
- Suggested issue: **Define the supported invite URL and add registration UI after membership semantics are fixed**.

### UI-010: Date-only values can render one day early

- Priority: high.
- Status: confirmed timezone defect.
- Evidence: `formatDate` parses `YYYY-MM-DD` with `new Date(value)`, which treats it as UTC before formatting in the browser timezone.
- Reproduction: in `America/Los_Angeles`, render the date `2026-06-01`.
- Expected: June 1, 2026.
- Actual: May 31, 2026 can be shown.
- Affected pages: dashboard, projects, tasks, milestones, and Gantt.
- Files: `wwwroot/scripts/utils.js`.
- Suggested issue: **Format date-only values without UTC conversion**.

### UI-011: Dashboard links inserted asynchronously miss SPA routing

- Priority: medium.
- Status: confirmed rendering-order defect.
- Evidence: `renderDashboard` starts asynchronous section loads and returns; `renderRoute` binds existing links before task/project links are inserted.
- Reproduction: populate dashboard tasks or projects and select one.
- Expected: client-side navigation using `pushState`.
- Actual: the browser performs a full document navigation and rebuilds the shell.
- Files: `wwwroot/scripts/pages/dashboard.js`, `wwwroot/scripts/main.js`.
- Suggested issue: **Use delegated routing for asynchronously rendered links**.

### UI-012: Messaging uses the wrong current-user ID property

- Priority: high.
- Status: confirmed DTO mismatch; expands `KI-007`.
- Evidence: `CurrentUserResponse` serializes `userId`, while messaging reads `currentUser.id`. Playwright fixtures incorrectly mock `id`.
- Reproduction: use the real `/api/auth/me` response and open a direct conversation.
- Expected: the other member is used as the title, own messages are styled as mine, and the current user is excluded from the picker.
- Actual: titles and own-message styling can be wrong, and the current user can remain selectable.
- Files: `CurrentUserResponse.cs`, `wwwroot/scripts/pages/messaging.js`, `tests/ui/app.fixtures.ts`.
- Suggested issue: **Align current-user DTO usage and fixtures**.

### UI-013: Conversation creation sends an incompatible enum representation

- Priority: high.
- Status: confirmed frontend/backend integration mismatch.
- Evidence: the UI sends `"Direct"` or `"Group"`; MVC uses default `System.Text.Json` enum handling because no `JsonStringEnumConverter` is configured.
- Reproduction: submit New Conversation against the real ASP.NET Core API.
- Expected: the conversation is created.
- Actual: JSON model binding rejects the request before the controller action completes.
- Files: `wwwroot/scripts/pages/messaging.js`, `Extensions/DependencyInjection.cs`, `MessagingDtos.cs`.
- Suggested issue: **Send numeric conversation enum values from the UI and add a contract test**.

### UI-014: Messaging polling destroys drafts and continues after leaving

- Priority: high.
- Status: confirmed client-side lifecycle bug.
- Evidence: every eight seconds `loadConversationDetail` replaces the complete detail DOM, including the composer. The interval is cleared only when `renderMessaging` runs again, not when another page is rendered.
- Reproduction: type an unsent message and wait eight seconds; then navigate away from the DM detail page.
- Expected: the draft and focus are preserved, and polling stops after leaving.
- Actual: the draft is erased; after leaving, polling accesses a missing detail element and can produce unhandled promise rejections.
- Files: `wwwroot/scripts/pages/messaging.js`, `wwwroot/scripts/main.js`.
- Suggested issue: **Add route-aware messaging polling that preserves composer state**.

### UI-015: New Conversation is a dead end for most users

- Priority: medium.
- Status: confirmed UI/API availability mismatch.
- Evidence: the user picker calls PlatformAdmin-only `GET /api/admin/users`, while the New button is shown to all authenticated users.
- Reproduction: sign in as Staff, Teacher, or a normal user and select New in Messaging.
- Expected: a permitted tenant/workspace member picker, or no creation action.
- Actual: the form opens and reports that user search is unavailable.
- Files: `wwwroot/scripts/pages/messaging.js`, `AdminController.cs`.
- Suggested issue: **Hide the unusable creation action until a scoped member-picker API is available**.

### UI-016: Announcement create/edit sends incompatible priority values

- Priority: high.
- Status: confirmed frontend/backend integration mismatch.
- Evidence: the UI sends `"Normal"`, `"Important"`, or `"Urgent"` while MVC expects numeric enums. The edit selector compares localized `enumLabel` output with English strings.
- Reproduction: create an announcement against the real API, or edit an Important/Urgent announcement in Japanese.
- Expected: the request binds successfully and existing priority remains selected.
- Actual: model binding can reject the request; in Japanese the selector defaults to Normal.
- Files: `wwwroot/scripts/pages/announcements.js`, `Extensions/DependencyInjection.cs`, `AnnouncementDtos.cs`.
- Suggested issue: **Use numeric announcement priority values and locale-independent selection logic**.

### UI-017: Announcement author and role checks are incorrect

- Priority: medium.
- Status: confirmed UI visibility bug.
- Evidence: author checks use `user.id` instead of `user.userId`. Numeric role `4` is PlatformOperator, but the UI treats it as announcement-capable while omitting numeric PlatformAdmin role `5`.
- Reproduction: view an authored announcement as its normal author, a PlatformOperator, or a numeric PlatformAdmin.
- Expected: edit/create controls match known capabilities.
- Actual: controls can be missing or incorrectly displayed.
- Files: `wwwroot/scripts/pages/announcements.js`, `wwwroot/scripts/enums.js`.
- Constraint: this is a UI visibility issue; backend authorization remains authoritative and must not be weakened.
- Suggested issue: **Correct announcement UI identity and role constants without changing authorization**.

### UI-018: Japanese localization is incomplete on announcements and admin pages

- Priority: medium.
- Status: confirmed localization gap.
- Evidence: announcement forms, read status, badges, admin dashboards, quota labels, tenant state text, and onboarding checklists contain hardcoded English. Project task selectors expose enum identifiers such as `NotStarted` and `WaitingReview`.
- Reproduction: select Japanese and visit Announcements, Tenant Admin, Platform Admin, onboarding, or a task editor.
- Expected: Japanese interface text except user-generated content.
- Actual: mixed English and Japanese UI.
- Files: `wwwroot/scripts/pages/announcements.js`, `wwwroot/scripts/pages/admin.js`, `wwwroot/scripts/pages/projects.js`, `wwwroot/scripts/i18n/locales/ja-JP.js`.
- Documentation mismatch: this violates `docs/FRONTEND_I18N.md`.
- Suggested issue: **Complete Japanese localization for implemented pages**.

### UI-019: Task mutations leave stale UI and can raise unhandled rejections

- Priority: high.
- Status: confirmed client-side behavior bug.
- Evidence: task create/update awaits the API without `try/catch`, then only writes “Saved.” The task list is not reloaded. Assignment and comment mutations have similar unhandled-error paths.
- Reproduction: create or edit a task, then mock a POST/PATCH failure.
- Expected: the task list refreshes after success and an inline error appears after failure.
- Actual: the list remains stale after success; failures can produce console errors without user feedback.
- Files: `wwwroot/scripts/pages/projects.js`.
- Suggested issue: **Refresh project task state and handle mutation errors inline**.

### UI-020: Project comments display raw user IDs

- Priority: medium.
- Status: confirmed rendering/contract mismatch.
- Evidence: the UI expects `authorDisplayName`, but `CommentResponse` only supplies `AuthorUserId`.
- Reproduction: open a project or task containing existing comments.
- Expected: a recognizable author name.
- Actual: a GUID is displayed.
- Files: `wwwroot/scripts/pages/projects.js`, `ProjectDtos.cs`.
- Suggested issue: **Render a documented comment-author representation**.

### UI-021: Project tabs and list layouts have accessibility gaps

- Priority: medium.
- Status: confirmed accessibility defect.
- Evidence: ARIA tabs only handle click events and do not support ArrowLeft, ArrowRight, Home, or End. Positional list grids have no column headers, and active navigation links have no `aria-current`.
- Reproduction: navigate project tabs and list rows with a keyboard or screen reader.
- Expected: standard ARIA tab behavior and announced current-page/column context.
- Actual: click-only tab activation and unlabeled positional values.
- Files: `wwwroot/scripts/pages/projects.js`, `wwwroot/scripts/components/navigation.js`, `wwwroot/styles/app.css`.
- Suggested issue: **Add keyboard tab behavior and accessible list metadata**.

### UI-022: Tenant Admin upload limit always appears 100% used

- Priority: high.
- Status: confirmed dashboard calculation bug.
- Evidence: `fileUploadLimitBytes` is passed as both the used value and the limit value to `quotaRow`.
- Reproduction: configure any file upload limit and open Tenant Admin.
- Expected: a standalone maximum-upload value or meaningful usage calculation.
- Actual: a red/danger 100% quota is displayed.
- Files: `wwwroot/scripts/pages/admin.js`.
- Suggested issue: **Render file upload limits separately from consumable quota usage**.

### UI-023: Invalid date values can be interpreted as HTML

- Priority: low.
- Status: confirmed escaping defect with limited exploitability under typed backend contracts.
- Evidence: `formatDate` returns an invalid input unchanged, and callers insert that value through `innerHTML`.
- Reproduction: mock a notification or project date as `<b>invalid</b>`.
- Expected: literal escaped text.
- Actual: HTML markup is rendered.
- Files: `wwwroot/scripts/utils.js` and its date-rendering callers.
- Suggested issue: **Escape invalid date fallbacks or return text-only values**.

### UI-024: Initial and mutation browser tests hide real integration defects

- Priority: medium.
- Status: confirmed test gap; expands `KI-009`.
- Evidence: fixtures use `id` instead of `userId`, return no UI modules, accept string enum payloads, use English only, manually close the mobile sidebar, and do not test failed task mutation submissions or DM polling.
- Impact: route, DTO, enum, localization, mobile navigation, and polling regressions can pass.
- Files: `tests/ui/app.fixtures.ts`, `tests/ui/app.spec.ts`.
- Suggested issue: **Make UI fixtures contract-accurate and add targeted navigation, Japanese, mutation, and polling tests**.

### Frontend audit verification

- Legacy JavaScript syntax checks applied only to the removed static SPA under `wwwroot/scripts`.
- Release build completed successfully.
- All 123 .NET tests passed.
- Playwright dependencies and Chromium downloaded, but browser execution was blocked in the audit container because `libatk-1.0.so.0` was unavailable. The reported Playwright failures were browser-launch infrastructure failures, not application assertions.
- No broad refactor or backend, authentication, database, or service change was made during the audit.

## High priority

### KI-001: First-administrator bootstrap requires explicit startup seed control

- Status: implemented with operational constraints.
- Evidence: `Program.cs` reads `COGLATAS_SEED_ADMIN_ENABLED`; `AppDbContextSeed.cs` creates or updates a platform administrator through `IPasswordHasher` and default-tenant owner membership.
- Constraint: `PlatformAdminSetupMode` still does not create an administrator, and bootstrap credentials must come from deployment environment variables or secret management.
- Suggested issue: **Add an audited operator runbook for first-PlatformAdmin bootstrap and post-bootstrap disablement**.

### KI-002: Invite registration does not create membership

- Status: partially implemented.
- Evidence: `AuthService.RegisterByInviteAsync` creates `User` and `Session` and marks the invite accepted, but creates no `TenantUser` or `WorkspaceMember`.
- Impact: the new cookie can be rejected on tenant API routes because active tenant membership is required.
- Suggested issue: **Complete invite acceptance with tenant and workspace membership transaction**.

### KI-003: Feature and platform configuration switches are not enforcement gates

- Status: confirmed mismatch.
- Evidence: `FeatureOptions` is only bound; most `PlatformOptions` values are never read outside binding. Tenant feature checks use `FeatureFlagService`.
- Impact: operators can believe webhooks, API tokens, platform admin, forms, or other modules are disabled when routes remain available to authorized users.
- Suggested issue: **Wire app configuration switches into route/service authorization or remove them**.

### KI-004: Object storage is configuration-only

- Status: planned.
- Evidence: object provider names resolve to `UnsupportedObjectStorageService`; readiness returns false for them.
- Impact: SaaS example configurations cannot store or read files.
- Suggested issue: **Implement and test an S3-compatible object-storage adapter**.

### KI-005: On-prem Compose fresh-stack startup requires runtime evidence

- Status: migration path verified under Issue #465; full app startup verification pending.
- Evidence: `deploy/onprem/compose.yml` now uses a one-shot SDK `migrate` service and makes the app wait for successful completion. An isolated clean PostgreSQL volume applied all current migrations successfully; the app itself still does not auto-migrate.
- Remaining impact: the intended TLS/reverse-proxy topology, production build-secret availability, and a clean-volume startup still require recorded execution evidence.
- Tracking issue: **#465 — Make on-prem Compose migrate a fresh database before app startup**.

### KI-006: Reverse-proxy HTTPS behavior still requires deployment-specific verification

- Status: configuration boundary and repeatable deployment gate implemented; target-host execution pending.
- Evidence: `Program.cs` enables forwarded headers only after an operator opts in and supplies explicit IP/CIDR trust boundaries; it retains loopback defaults rather than trusting all peers. `deploy/onprem/compose.yml` binds the origin to loopback by default and fails startup when proxy mode lacks a boundary. Focused Kestrel coverage verifies secure CSRF-cookie issuance through a trusted forwarded HTTPS request. Issue #481 adds `tests/ui/public-https-golden-path.spec.ts` and a protected manual workflow that requires the real public HTTPS route, HTTP-to-HTTPS redirect, HSTS, Secure cookies, CSRF, authenticated Task execution, reload, logout, and re-login.
- Remaining impact: an operator can still misstate the trusted proxy peer or public DNS/TLS route, and the gate cannot execute until its protected synthetic fixture is configured. The exact target topology remains unverified until a successful recorded public-gate run; `/health/ready` alone is not sufficient.
- Tracking issues: **#467 — Decide and verify the on-prem TLS/reverse-proxy topology**; **#481 — Public HTTPS Production Golden Path**.

## Medium priority

### KI-007: Frontend current-user contract uses the wrong ID property

- Status: confirmed mismatch.
- Evidence: backend serializes `CurrentUserResponse.UserId`; frontend messaging/announcement logic reads `user.id`.
- Impact: own-message detection, conversation titles, and author edit checks can be wrong.
- Test gap: Playwright mocks return `id`, hiding the mismatch.
- Suggested issue: **Align frontend auth DTO usage and add a real contract test**.

### KI-008: Browser navigation overstates implemented UI

- Status: confirmed mismatch.
- Evidence: `/workspaces`, `/groups`, `/channels`, `/tasks`, `/artifacts`, `/calendar`, `/forms`, and search results render placeholders.
- Backend APIs exist for many of these areas.
- Suggested issue: **Label or hide placeholder navigation until workflows are implemented**.

### KI-009: Playwright does not test the real backend

- Status: partially resolved for MVP0 smoke coverage.
- Evidence: `tests/ui/real-backend-smoke.spec.ts` runs through the isolated
  `infra/compose/test/real-backend-smoke.yml` stack against ASP.NET Core, PostgreSQL,
  cookie auth, and CSRF using synthetic seeded data. The regular Angular suite
  remains static and mocked by design.
- Remaining impact: the MVP0 smoke is intentionally narrow and does not replace
  broad frontend/backend contract, authorization, or PostgreSQL regression
  coverage.

### KI-010: API error contract is inconsistent

- Status: confirmed mismatch.
- Evidence: global exceptions use `ErrorResponse(Code, Message, TraceId)`; many controllers return `{ error }`; authorization/not-found failures frequently return 400.
- Mismatch: `docs/API_CONTRACTS.md` described one shared error shape.
- Expanded evidence: backend audit `BE-012` documents inconsistent success codes, error categories, and controller-local mappings.
- Suggested issue: **Standardize API problem/error responses and status mapping**.

### KI-011: PostgreSQL execution requires live-environment evidence

- Status: implementation safeguard added; execution evidence remains environment-dependent.
- Evidence: `PostgreSqlFactAttribute` explicitly skips locally without `POSTGRES_TEST_CONNECTION_STRING`, while `PostgreSqlTestEnvironment.RequireConnectionString()` fails when `CI=true` or `GITHUB_ACTIONS=true` and it is absent.
- Impact: unconfigured tests no longer become false passes, but a green local non-PostgreSQL run is still not PostgreSQL evidence.

### KI-012: Tenant export is not backup or restore

- Status: partial feature.
- Evidence: metadata ZIP is generated in memory; file bodies are excluded; no import exists.
- Suggested issue: **Define tenant export completeness and implement a separately reviewed restore design**.

### KI-013: Admin and tenant-management browser UIs are mostly read-only

- Status: partial UI.
- Evidence: admin pages show summaries, tenants, plans, usage, and features, but do not expose most mutation APIs.
- Suggested issue: **Define supported admin UI scope and implement the minimum safe management flows**.

### KI-014: Tenant “invite” endpoint only adds an existing user

- Status: naming mismatch.
- Evidence: `POST /api/tenant/users/invite` accepts `UserId` and calls `AddCurrentTenantUserAsync`.
- Impact: it is not an email/token invitation workflow.
- Suggested issue: **Rename tenant user add endpoint or implement a real tenant invitation flow**.

## Low priority and compatibility debt

### KI-015: `SystemAdmin` is a compatibility alias

- Status: deprecated naming.
- Evidence: `SystemRole.SystemAdmin = PlatformAdmin`.
- Impact: mixed naming persists in controller role strings and services.
- Suggested issue: **Normalize PlatformAdmin naming while preserving serialized compatibility**.

### KI-016: Dockerfile used .NET 10 preview image tags

- Status: resolved.
- Evidence: Dockerfile and Compose migration services use `mcr.microsoft.com/dotnet/*:10.0`.
- Impact: older deployments built before this change may still run preview images until rebuilt.
- Suggested issue: **Rebuild deployed containers after switching to stable .NET 10 image tags**.

### KI-017: File scan records exist without a scanning pipeline

- Status: foundation only.
- Evidence: `FileScanResult` and scan status fields exist; no scanner or background worker was found.
- Suggested issue: **Define malware-scanning behavior before accepting untrusted pilot uploads**.

### KI-018: Archived status reports contain resolved and stale claims

- Status: documentation mismatch.
- Evidence: `docs/archive/status/PILOT_STATUS.md` and reports describe older test counts and missing controls that were later added.
- Resolution: keep them archived and use `docs/archive/README.md` for context.

## Planned features, not current capabilities

- Password reset delivery.
- API token authentication middleware and request metering.
- Outbound webhook delivery.
- Object storage and signed URLs.
- Full tenant restore.
- Email delivery.
- SSO/MFA.
- Realtime messaging/notifications.
- Background jobs and scheduled reminders.
- Task automation, server Project File materialization, durable Task results,
  and the contest end-to-end execution path. Issue #461 fixes the provider to
  the no-Web `FirstPartyProjectFilesRuntimeV1` contract; #462 and #463 own
  materialization and durable result implementation respectively. Outbound Web
  retrieval remains out of scope and disabled.
- Full billing/payment integration.
- Advanced Gantt/resource planning.
- Full free-form docking/radial UI.

## Unknowns requiring audit evidence

- Latest GitHub Actions result for the docs branch.
- Successful startup of each Compose profile.
- First-user provisioning used by any existing deployment.
- Real target-environment tenant isolation.
- Reverse-proxy scheme and host behavior.
- Backup retention and a successful restore drill.
- Production PostgreSQL and storage versions.
- Real-data performance and query plans.

## Documentation still missing

- A truthful first-administrator bootstrap runbook for the explicit startup seed.
- A generated or maintained endpoint inventory/OpenAPI contract.
- A frontend/backend DTO compatibility guide.
- A canonical frontend route and UI-module mapping.
- A supported-page matrix distinguishing implemented, placeholder, and unavailable browser routes.
- Frontend enum serialization rules and contract-accurate mock examples.
- Date-only, timestamp, browser-time-zone, tenant-time-zone, and locale display rules.
- Invite-registration and password-change browser entry-point documentation.
- Mobile header and navigation-drawer behavior.
- Messaging polling, refresh interval, focus, and draft-preservation behavior.
- A policy for translating API-originated validation and error messages.
- Notification target-route support and fallback behavior.
- A production reverse-proxy example with trusted proxy configuration.
- A target-environment deployment record showing exact versions and settings.
- A completed backup/restore drill record.
- Data classification, retention, deletion, and privacy requirements for real school data.
- An incident-response ownership/escalation matrix.
- A supported-browser and accessibility conformance statement.

## Recommended follow-up audits

1. Run the full GitHub Actions workflow and attach results to the documentation PR.
2. Exercise each Compose profile from an empty volume and record startup failures.
3. Threat-model and document the explicit first-admin startup seed before pilot use.
4. Trace invite acceptance through user, tenant membership, workspace membership, and session validation.
5. Compare every frontend API call and mocked fixture with the real DTOs/controllers.
6. Audit all `IgnoreQueryFilters` calls and all tenant-owned entities.
7. Run cookie-authenticated cross-tenant tests against PostgreSQL.
8. Test deployment behind the intended TLS reverse proxy.
9. Execute and record PostgreSQL plus file-storage backup/restore.
10. Decide which placeholder UI routes should be hidden, labeled, or completed.
11. Run Playwright in an environment with the documented Chromium native dependencies.
12. Add a real-host frontend contract profile covering current-user DTOs and enum request payloads.
13. Exercise Japanese desktop and mobile workflows for every implemented page.
14. Run the critical and high-priority regression plan in `docs/BACKEND_LOGIC_AUDIT.md` against PostgreSQL.
