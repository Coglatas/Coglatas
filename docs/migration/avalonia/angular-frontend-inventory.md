# Angular → Avalonia production inventory (AV-MIG-01 / #765)

Pinned source: `4e6a10903a5a472ca89f833aba993a29e1b2cf73` (`main`)  
UI/Interaction baseline: `Coglatas_Avalonia_Frontend_Panel_Reorganization_v5_8_1_Review_Closure`

This document is a human-readable companion to:

- `angular-frontend-inventory.json` — pinned Angular source facts: routes, source state owners, integrations, dependencies and existing tests.
- `angular-target-surface-map-v5.8.1.json` — **authoritative current target** PNL/mode, migration disposition and Avalonia execution owner.
- `angular-persisted-state-inventory.json` — browser/session persisted-state families and cutover disposition.
- `angular-feature-freeze-matrix.json` — the original #765 freeze snapshot. Its source classification remains useful, but any target owner/target naming that conflicts with the v5.8.1 target map is superseded by `angular-target-surface-map-v5.8.1.json`.

The route set is pinned to `frontend/src/app/app.routes.ts`. It contains 38 redirect/screen/shell/fallback entries. Route parity alone is not migration completion: embedded WorkSurface surfaces and browser/session persisted state are inventoried separately.

## Source-of-truth boundary

| Concern | Authority |
|---|---|
| Program principles / ownership hierarchy | #764 |
| Panel / WorkSurface / interaction architecture | v5.8.1 DOCX |
| Wave / WIP / merge ordering | #798 |
| Actual Angular source inventory / migration disposition | #765 + these files |
| Implementation/evidence owner | child AV-MIG issues |
| Terminal cutover | #797 |

## Corrected route-owner decisions

| Source route/surface | Target | Owner | Decision |
|---|---|---:|---|
| `/register/invite` | PNL-00 Invite Registration | #780 | Public invite registration belongs to AppShell/Auth. Admin invite management remains PNL-17/#791. |
| `/messages` | PNL-40 Team Chat entry + PNL-41 DM Inbox | #785 | Intentionally changed; do not recreate a mixed canonical Conversation list/state. |
| `/conversations/:conversationId` | PNL-40 Team Chat | #785 | Legacy `conversationId` is a source implementation detail. |
| `/dm/:conversationId` | PNL-42 Direct Message Detail | #785 | Target state owner is DM-specific. |
| `/messages/settings` | PNL-35 Message Settings | #791 | Account/settings owner; communication integration stays related to #785. |
| `/workspaces/:workspaceId/research/new` | PNL-13 Research Quick Create | #782 | Project/Workbench creation flow, not a standalone Workspace implementation owner. |
| all Project/Task report version routes | PNL-47 Artifact/Report Reader | #817 | #788 no longer owns the reader. |
| `/artifacts/:artifactId` | PNL-47 Artifact/Report Reader | #817 | Version/evidence/TemporalContext owner. |
| Graph/RelationGraph (no current route) | RelationGraph projection | #816 | Avalonia-first shared renderer/capability. |
| PNL-16 Calendar/Schedule | shared semantic ownership | #782 + #784 | No current Calendar/Scheduler production surface; create a dedicated Issue only when promoted. |

## Embedded production surfaces

`/projects/:projectId` is not just a Project detail page. The pinned implementation imports and drives `TaskTableComponent`, `AipKanbanComponent`, and `AipGanttComponent`.

| Embedded source surface | Target | Freeze class | Execution owner | Supporting owner(s) |
|---|---|---|---:|---|
| Project Task Table/List | PNL-03 + PNL-14 | November Required | #782 | #776, #814 |
| Project Kanban | PNL-03 Board WorkSurface projection | Maintenance Only | #782 | #776, #784, #814 |
| Project Gantt/Schedule | PNL-15 Timeline/Gantt | Maintenance Only | #787 | #777, #782, #814 |
| AppShell / right panel / continue-working | PNL-00/01 + notification/deep-link integration | November Required | #780 | #788, #770, #774, #775 |
| Realtime transport/subscription lifecycle | cross-cutting | November Required | #778 | #773, #793, #794 |

Graph (#816) and Dock (#815) have no canonical Angular route to migrate and are therefore Avalonia-first capabilities rather than hidden Angular routes.

## Angular state model

The pinned source declares NgRx packages but contains no production `@ngrx/*` imports, reducers, effects, or ComponentStore implementation. The real source state model is primarily Angular Signals plus RxJS facades/services. Do not invent a reducer/effect migration wave.

Important source → target ownership:

- `AuthSessionFacade` → #773.
- `ActiveWorkspaceFacade`, `WorkspaceSelectionFacade`, `WorkspacesFacade` → #781.
- `RealtimeFacade` → #778.
- `MessagingFacade` is recorded as an Angular source owner only. Its target is deliberately split into Team Chat and Direct Message ownership under #785.
- `ProjectDetailFacade` → #782, with embedded Gantt integration #787/#777.
- `MyTasksFacade` + `WorkViewPreferenceService` → #784.
- `FilesFacade` / `FileFolderStore` → #786 with platform I/O #779.
- Audit facades / saved views → #790.
- Account/Admin state → #791.
- Right Panel / Continue Working → #780 with #788 deep-link/notification integration.

## Persisted-state registry

No auth token/session authority was identified in browser `localStorage`/`sessionStorage`. Persisted entries are UI/presentation state and still require current-scope validation when they carry resource IDs.

| Source | Storage key/family | Target owner | Cutover disposition |
|---|---|---:|---|
| Theme | `aipsite.ui.theme.v1` | #775 | Reset or import; never authority |
| Locale | `aip.locale` | #775 | Reset or import |
| Last Workspace | `aip.workspace.last-used:<tenant>:<user>` | #781/#774 | Import only with account/scope/entity revalidation; otherwise safe reset |
| My Work projection | `aipsite.work-view.v1.<tenant>.<user>.my-tasks` | #784 | Intentionally changed to renderer-local preference |
| My Work saved filters | `aipsite.work-view.saved-filters.v1:<tenant>:<user>:my-tasks` | #784/#814 | Preserve semantic descriptors if import is possible; never persist rows/permissions |
| Message display settings | `aip.messaging.global-settings.v2.<tenant>.<user>` | #791/#785 | Import or reset; server notification state remains authoritative |
| Message drafts | `aip.messaging.draft:<tenant>:<user>:<workspace\|dm>:<conversationId>` | #785 | Session-only source; reset at Desktop cutover; target Team Chat and DM draft owners are separate |
| Message list scroll/focus | `aip.messaging.list-*` session keys | #785 | Retire/rebuild as renderer-local navigation state |
| Right Panel mode | `aipsite.rightPanel.mode` | #780 | Session presentation; safe reset |
| Audit saved views | `aipsite.audit.saved-views.v1:<scope>:<user>` | #790 | Import semantic filter snapshots if possible; reauthorize data query |
| Continue Working | `aipsite.continue-working.v1:<tenant>:<user>:<workspace>` | #780/#788 | Import only after current-scope revalidation; otherwise safe reset |

## Communication migration rule

Angular source routes and APIs use `conversationId` and a `MessagingFacade`. That is a source fact, not the target architecture.

Target canonical owners under #785 remain separate for:

- Team Chat channel directory, selected channel, draft, unread/read, mute and deep-link.
- Direct Message roster, selected DM/group-DM, draft, unread/read, mute and deep-link.
- Saved Messages keeps a typed source origin and does not become a third mixed conversation root.

Shared primitives may include timeline/composer/attachments/presence and #778 transport.

## Platform policy boundary

Platform Tier/support classification is owned exclusively by #767. This inventory records source browser/platform dependencies and migration constraints, but it does not infer Avalonia Desktop/Mobile/Browser support tiers from Angular behavior. Until #767 closes, platform classification here is explicitly provisional and downstream cutover evidence must consume #767 as the authority.

## Dependencies

The pinned source contains Angular 22.1.5, Angular CDK 22.1.5, RxJS 7.8.2, browser SignalR 10.0.11, declared-only NgRx 22.0.0, Syncfusion Angular 34.2.6, AG Grid 36.1.0, Lucide Angular, Storybook, Vitest/jsdom and Playwright/axe.

Target dependency policy is #804. In particular:

- Dock.Avalonia is allowed only behind #815 mechanics-only adapter.
- MSAGL Core is allowed only behind #816 compute-only adapter.
- Coglatas.UI.Core remains Pure C# and cannot depend on Avalonia, Dock.Avalonia, MSAGL or generated backend DTOs.
- Browser UI libraries do not define target product semantics.

## Tests and evidence

Existing representative evidence includes Angular/real-backend smoke, messaging, files, workspace, task-execution and audit Playwright suites. Target ownership is:

- #793 — unit/ViewModel/headless/application and adapter-contract tests.
- #794 — UI/E2E, input, accessibility and fallback tests.
- #795 — CI architecture/supply-chain/provenance gates.
- #805 — performance budgets.
- #797 — same-candidate-SHA terminal cutover evidence.

## Current completion status

At this pinned snapshot:

- 38/38 route entries are classified in the v5.8.1 target map.
- All route entries have a target PNL/mode and an Avalonia execution owner.
- Artifact/Report ownership is #817.
- RelationGraph ownership is #816.
- Invite Registration ownership is #780; Invite Admin remains #791.
- Embedded Project Kanban/Gantt/Table surfaces are explicitly classified.
- 11 browser/session persisted-state families are classified.
- No active production surface remains `owner TBD` in the target map.
- PNL-16 Calendar and architecture-only deferred PNLs remain non-active until explicit promotion.

This does **not** by itself close #765. The PR must pass the strengthened machine assertions and review against the same pinned source before #765 is marked complete.

## Source ownership verification

The source checker parses `app.routes.ts` with TypeScript and compares the exact
route set with the inventory, legacy freeze snapshot and v5.8.1 target map. It
checks component source paths, directly injected registered state owners, duplicate
and missing entries, and November flags. Message Settings uses its component and
`MessageGlobalSettingsService`; Invite Admin is component-local with direct HTTP;
Session Expired is a stateless presentation component. The freeze owner column
now follows those source facts.

This checks direct ownership plus the explicit embedded/freeze classifications
required by #765. Cross-cutting owners retain explicit non-route status. Exhaustive
P0 API/DTO and class-3/4 backend-authority reconciliation remains owned by #766;
it is not duplicated as an additional #765 completion gate. The v5.8.1 target map
remains authoritative for future owners. Angular primary-demo acceptance and
Avalonia core technical preview require separate scope decisions under #798; no
additional November scope is inferred. Platform Tier/support remains authoritative
in #767.
