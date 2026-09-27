# AV-MIG-02 Backend / API Contract Boundary

Status: migration boundary baseline for Issue #766  
Parent: #764  
Depends on: #765  
Downstream: #769, #772, #773, #778, #779, #780-#792  
Machine-readable sentinel inventory: `docs/migration/avalonia/p0-api-boundary.json`

## Purpose

This document defines the client-independent boundary that an Avalonia client may depend on without reading Angular source code. It does **not** make Angular implementation details part of the backend contract.

The contract source order is:

1. generated OpenAPI 3.1 artifact (`artifacts/openapi/coglatas-openapi.json`) for HTTP path, method, request/response schema and security metadata;
2. this document for cross-cutting semantics that OpenAPI cannot fully express;
3. backend Application/Web code and focused contract tests for authorization/redaction/idempotency/realtime semantics;
4. `docs/API_CONTRACTS.md` and feature-specific contract documents for detailed use-case rules.

`docs/frontend/api-binding-verification.md` remains evidence about the current Angular consumer. It is an inventory input, not a normative client contract.

## Boundary principles

- Backend authorization is authoritative. A client capability flag, hidden button, cached membership, route guard, selector or local filter never authorizes a command or protected read.
- Tenant/workspace/project/resource scope is resolved and enforced by the server. Normal tenant requests do not provide a trusted tenant identifier as command authority.
- DTOs are wire contracts. Generated DTOs are not Avalonia ViewModels or domain models.
- Client-side aggregation is presentation-only unless the API explicitly declares the returned dataset complete and bounded for that purpose.
- Security-sensitive filtering, redaction, visibility, count suppression and safe-not-found behavior remain server-side.
- Compatibility shims are temporary adapters around a stable backend contract; they must not become the new application model.
- Existing Angular behavior is preserved only when it is part of an explicit backend contract. Angular quirks are not migrated by default.

## P0 HTTP inventory

Existing Angular/API binding evidence is maintained in `docs/frontend/api-binding-verification.md`. The migration-blocking P0 operations are additionally pinned in `p0-api-boundary.json` so an accidental route removal is CI-visible.

| Area | Canonical HTTP surface | Contract / DTO owner | Migration rule |
| --- | --- | --- | --- |
| Auth/session | `/api/auth/login`, `/api/auth/me`, `/api/auth/logout`, `/api/auth/status` | `AuthController`, auth Application DTOs | Cookie session is transport state; UI never synthesizes identity. |
| CSRF | `GET /api/security/csrf-token` | `SecurityController`, `CsrfTokenResponse` | Unsafe cookie-authenticated requests send the server-provided header/token. |
| Tenant | `/api/tenants/current`, `/api/tenants/my`, `/api/tenants/switch` | `TenantsController` | Current tenant is server-resolved; switch is a command, not local state mutation. |
| Workspace | `GET/POST /api/workspaces`, `/api/workspaces/capabilities`, `/api/workspaces/{workspaceId}` | `WorkspacesController` | Capability projection is a UX hint; create/list/detail remain server-authorized. |
| Project | `/api/workspaces/{workspaceId}/projects`, `/projects/create-options`, `/api/projects/{projectId}`, `/activate` | Project controllers/Application DTOs | Workspace-scoped create and explicit activate are canonical. Deprecated unscoped create is not a fallback. |
| Task | `/api/projects/{projectId}/tasks/create-options`, `/tasks/create`, task read/update surfaces | Task/Project controllers/Application DTOs | Canonical project-scoped create owns authority, defaults and workflow options. |
| Conversation/message | `/api/conversations`, `/{conversationId}`, `/{conversationId}/messages` and mutation routes | `ConversationsController` and messaging services | Recursive visibility/read boundaries and unread state are server-owned. |
| Announcement | `/api/announcements` and child operations; durable draft workflow where applicable | Announcement controllers | Audience authorization and redacted not-found semantics stay server-side. |
| Notification | `/api/notifications`, unread/count/navigation surfaces | `NotificationsController` | Navigation targets are server-projected and must be re-authorized on open. |
| Files | `/api/files`, detail/activity/sharing/download/download-grant/version routes | `FilesController`, file services | Metadata, grant and byte delivery all require backend authorization. |
| Search | `GET /api/search` and scoped auxiliary searches | `SearchController` | Search results are already authorization/redaction projections; client must not widen them. |
| Audit/admin | `/api/admin/*` and audit/evidence/report routes | audit/admin controllers | Admin/system role does not imply a cross-tenant global bypass unless the endpoint contract explicitly grants it. |

This table is a family-level inventory. The OpenAPI artifact is the operation-level inventory, while `p0-api-boundary.json` pins migration-blocking operations.

## HTTP authentication, cookie and CSRF contract

### Session

The first-party HTTP client uses the production authentication cookie `.Coglatas.Auth`. The Avalonia transport must use an owned `CookieContainer`/handler abstraction rather than exposing cookies to ViewModels.

Authentication/session expiry is authoritative on the server. A cached `CurrentUser` projection is not proof that a later request remains authenticated.

### CSRF

`GET /api/security/csrf-token` returns the token plus header name. The current canonical header is `X-CSRF-Token`. Cookie-authenticated unsafe HTTP requests and SignalR negotiate must use the same antiforgery boundary when CSRF protection is enabled.

Rules:

- safe GET/HEAD reads do not invent CSRF requirements;
- unsafe POST/PUT/PATCH/DELETE requests use the token contract;
- a failed unsafe request is not automatically replayed merely because a client can fetch a new token;
- a single controlled CSRF refresh/retry may be implemented in the transport layer only when the request is safe to replay under its idempotency contract;
- auth/CSRF implementation types must not escape into application/domain models.

Issue #773 owns the Avalonia implementation of this contract.

## Error and HTTP status contract

### Target shape

The migration target is `ApiErrorEnvelope`:

```text
requestId
error.code
error.message
error.target
error.details[]
error.redactionApplied
traceId
status
```

`CanonicalErrorEnvelope` / `ApiEnvelope` are the backend canonicalization path for migrated surfaces. Some legacy controllers still return `{ error: ... }` or other shapes; those are compatibility debt tracked by #534, not a second canonical model.

### Status categories

The Avalonia application client must reason from typed category + HTTP status, not from English message parsing.

| Category | HTTP behavior |
| --- | --- |
| Authentication required / expired | 401 |
| Explicit authorization/capability denial | 403 unless disclosure policy requires safe 404 |
| Hidden or missing protected resource | 404 according to resource disclosure policy |
| Malformed/validation failure | 400 or an endpoint-documented 422; do not infer from message text |
| Stale version / optimistic concurrency / idempotency conflict | 409 |
| Rate limited | 429; honor `Retry-After` when supplied |
| Dependency unavailable | 503 |
| Unexpected server failure | 5xx canonical safe envelope; never expose raw exception detail |

Until #534 closes the repository-wide mismatch, the Avalonia transport/application adapter may accept both canonical and explicitly documented legacy error shapes. Presentation code must receive one application error model and must not branch on controller-specific JSON.

## Authorization and projection ownership

The following logic is **never** migrated as client authority:

- tenant membership validation;
- workspace/project/group membership and capability evaluation;
- hidden-resource existence decisions;
- announcement audience authorization;
- conversation/message recursive visibility;
- file metadata/download/share authorization;
- search/audit/notification redaction;
- project/task create authority;
- server-owned capability grants;
- permission-sensitive counts and aggregates.

Client flags such as `canCreate`, `canActivate`, `canAddFiles`, `uiPermissions`, route guards and disabled controls are presentation hints. The server re-evaluates authority on every protected operation.

## Pagination, ordering and filtering

Current broad list APIs commonly use `PagedResponse<T>`:

```text
items[]
page
pageSize
totalCount
```

Rules for migration:

- preserve endpoint-specific default/max `pageSize`; do not introduce a client-wide guessed maximum;
- ordering/filtering that affects authorization, totals or durable cursor position is server-owned;
- local filtering is allowed only on an explicitly complete bounded projection and remains presentation-only;
- a client must not fetch all pages merely to reproduce a server-side filter;
- cursor pagination is represented separately when an endpoint explicitly exposes `nextCursor`/`hasMore`; do not fake a cursor over page numbers;
- migration from page-number to cursor semantics is a contract change and must be additive or explicitly versioned.

Issue #772 owns the reusable Avalonia paging abstractions.

## Date, time and timezone representation

- Wire instants use OpenAPI `string` + `date-time` and map to `DateTimeOffset` in C#.
- Server-generated durable instants are UTC-normalized while retaining offset-safe parsing at the client boundary.
- The client does not send locale-formatted date strings.
- A local wall-clock scheduling use case that depends on civil time carries the endpoint-defined local value plus IANA timezone identifier and any required overlap/disambiguation field. It is not converted into an invented browser timezone rule.
- `OptionalDateTimeOffset` PATCH sentinels preserve the OpenAPI wire shape already verified by `scripts/ci/verify-openapi.py`.
- UI localization/timezone formatting occurs after mapping out of generated DTOs.

## Enum and status vocabulary

The migration does **not** globally change existing wire enum encoding.

Current evidence shows that most JSON enums are numeric while explicitly converted types (for example `ConversationType`) use string names. Changing all enums to strings during the frontend rewrite would be an unrelated breaking API migration.

Rules:

- Kiota/generated transport models follow the OpenAPI wire representation;
- application adapters map transport enum/status values into application-facing types;
- ViewModels do not render raw numeric enum values;
- unknown/new additive enum values must fail safe in authorization/state-machine decisions and surface a typed compatibility error rather than silently map to an existing state;
- renaming/re-numbering/removing an existing wire enum value is breaking;
- adding a value is additive at the wire level but still requires client compatibility review when clients use exhaustive switches.

## Nullability, optional fields and additive compatibility

- `null`, omitted and empty are not interchangeable unless an endpoint contract explicitly says so.
- PATCH sentinel types retain their existing "not supplied" vs "explicit null" semantics.
- Adding an optional response field is additive.
- Adding a required request field, removing/renaming a field, changing type/nullability, narrowing an accepted enum, changing authorization visibility, or changing success/error status semantics is breaking unless versioned/migrated.
- Clients ignore unknown additive JSON properties by default but must not ignore unknown values that affect authorization, lifecycle or data integrity.
- Generated models are regenerated from the exact OpenAPI artifact; hand-editing generated models is prohibited.

## Idempotency and optimistic concurrency

Canonical create/transition commands that declare `Idempotency-Key` keep that key across an uncertain replay of the **same normalized command by the same scoped identity**. A key is not reused for a different payload.

For canonical Workspace/Project/Task creation, the existing contract requires a printable ASCII key in the documented length range (currently 8-128 characters where the route declares it).

Rules:

- unsafe mutation is not automatically retried without an endpoint idempotency/replay contract;
- 409 stale-version/idempotency conflicts are application outcomes, not transient network failures;
- expected version/ETag-style values come from authoritative server state and are refreshed after conflict;
- timeout after dispatch is treated as "commit outcome unknown" when the endpoint cannot prove otherwise.

## Files boundary

Avalonia must not depend on browser `File`, object URL or DOM APIs.

The backend boundary is:

- authorized file metadata/list/detail;
- upload stream + metadata/multipart contract;
- preview/version metadata where provided;
- authorized direct content route and/or download grant;
- sharing policy/grant DTOs;
- server-side size/MIME/extension/quota/authorization checks.

A download grant is capability data with its own expiry/scope. It is not durable application state and must not be persisted as if it were a stable file URL. Local save/open/picker behavior belongs to the platform abstraction owned by #779.

## SignalR boundary

SignalR is not represented by ordinary OpenAPI paths, so it is pinned separately in `p0-api-boundary.json` and this document.

Endpoint: `/hubs/app`

Authentication requirement: `AppHub` is an authenticated hub and must carry `[Authorize]`.

Client-invoked direct public method signatures:

- `Task<HubSubscriptionResult> SubscribeUser()`
- `Task<HubSubscriptionResult> SubscribeTenant()`
- `Task<HubSubscriptionResult> SubscribeWorkspace(Guid)`
- `Task<HubSubscriptionResult> SubscribeConversation(Guid)`
- `Task<HubSubscriptionResult> SubscribeProject(Guid)`
- `Task<HubSubscriptionResult> UnsubscribeWorkspace(Guid)`
- `Task<HubSubscriptionResult> UnsubscribeConversation(Guid)`
- `Task<HubSubscriptionResult> UnsubscribeProject(Guid)`

Server events:

- `DurableEvent`
- `AuthorizationInvalidated`

`HubSubscriptionResult` is `{ allowed, code }`.

Semantics:

- connection authentication and current tenant come from the authenticated connection scope;
- resource IDs do not establish authority;
- user/tenant subscriptions are derived from the authenticated context and intentionally have no public unsubscribe hub methods;
- workspace/conversation/project subscriptions are re-authorized by the backend;
- a reconnect that creates a new connection must rebuild the desired subscription set; a previous connection's group membership is not application state;
- `AuthorizationInvalidated` causes protected client projections/subscriptions to be revalidated, not merely hidden in the UI;
- reconnect delay/backoff is client transport policy, not a server business contract;
- durable event payloads must be mapped through a client application layer before presentation consumes them.

Issue #778 owns the Avalonia transport implementation.

## Angular logic classification

This classification is the rule used while #765 completes the exhaustive inventory.

| Class | Meaning | Migration treatment | Representative current examples |
| --- | --- | --- | --- |
| 1. Presentation-only | Formatting, labels, layout state, view-only sorting of complete data | Reimplement in Avalonia | display labels, local visual expansion/selection, rendering-only formatting |
| 2. Client orchestration | Transport/navigation/lifecycle coordination without business authority | Move into shared Avalonia application/client layer where useful | CSRF acquisition, connection lifecycle, post-create authoritative refetch, active-context preference reconciliation |
| 3. Domain/business rule | Rule changes outcome/validity/state machine | Backend owns it | project/task lifecycle validity, canonical defaults, create authority, scheduling rules |
| 4. Authorization/filtering | Rule determines visibility/permission/protected counts | Backend owns it; client only consumes projection | route guards, capability-driven buttons, local permission filters |
| 5. Compatibility shim | Temporary adaptation to inconsistent legacy wire behavior | Isolate behind adapter and delete after backend migration | legacy `{error}` parsing, numeric/string enum compatibility, legacy response-shape normalization |

A rule discovered in Angular is not copied merely because it exists. If classification 3 or 4 logic is required for correctness and does not exist on the backend, create/fix the backend contract first.

## OpenAPI and C# generated client decision

Decision for #772: **Microsoft Kiota** is the preferred generated C# client path over introducing a second HTTP abstraction such as Refit/RestSharp.

Rationale:

- the backend already emits deterministic OpenAPI 3.1;
- the target client is .NET/C#;
- #772 already standardizes on `HttpClient`, `System.Text.Json`, Kiota and `Microsoft.Extensions.Http.Resilience`;
- generated code can be kept behind an application-facing adapter and regenerated in CI;
- the generator/tool version can be exact-pinned under #804/#795 supply-chain rules.

Generated code rules:

```text
OpenAPI 3.1 artifact
  -> exact-pinned Kiota generation
  -> generated transport client/models
  -> Coglatas client application adapters/services
  -> Avalonia ViewModels
  -> Views
```

Forbidden dependency direction:

```text
View -> generated DTO
ViewModel -> raw Kiota request builder everywhere
Domain/Application public contract -> Kiota implementation type
```

The generated client is a transport implementation, not the UI domain model.

## Breaking vs additive policy

### Breaking

Any of the following requires migration/versioning and CI-visible review:

- remove or rename a P0 route/method;
- remove/rename/retype a response field consumed by a supported client;
- add a required request field without compatible default/overload semantics;
- change nullability meaning;
- change enum numeric value/name already on the wire;
- change `date-time` into locale/local-time text or vice versa;
- change page/cursor semantics incompatibly;
- change 401/403/404 disclosure behavior;
- change idempotency scope/replay semantics;
- weaken server authorization by moving a decision to the client;
- rename SignalR hub methods/events or change their parameter/result meaning.

### Additive

Normally additive, subject to client exhaustive-switch review:

- optional response property;
- new endpoint;
- new optional query/filter field with unchanged defaults;
- new error code under an already-supported status/category;
- new enum value when all supported clients safely handle unknown values;
- new SignalR event that existing clients may ignore.

## CI contract gate

`p0-api-boundary.json` is intentionally smaller than the full OpenAPI document. It pins the migration-blocking operations whose accidental disappearance would prevent the Avalonia foundation/core vertical slice.

`scripts/ci/verify-openapi.py` remains the SEC-01 OpenAPI verifier. AV-MIG-02 uses the separate `scripts/ci/verify_av_mig_contract_boundary.py` so migration-specific policy does not get conflated with the general security OpenAPI contract.

The AV-MIG verifier evaluates operation-level security using OpenAPI Security Requirement OR semantics. Every protected P0 operation must declare a non-empty `security` array with exactly `[{"CookieAuth": []}]`, without additional AND/OR schemes or scopes; missing security, `security: []`, `{}` anonymous alternatives, and unrelated-scheme alternatives fail. Every `anonymous: true` P0 operation must declare operation-level `security: []` exactly so it cannot accidentally inherit a future document-level requirement. `SecurityOpenApiOperationTransformer` emits this explicit empty override for `[AllowAnonymous]` endpoints.

The same verifier resolves the effective Release/net10.0 C# `DefineConstants` from MSBuild and parses C# with the pinned SDK Roslyn syntax tree. It excludes comments, literal contents and inactive branches, and checks `nonOpenApiContracts.signalR` against live backend source for the exact `/hubs/app` mapping, the required `[Authorize]` hub control, exact direct-public method signatures, and emitted server event names. Nested type methods cannot satisfy an `AppHub` method sentinel. The CSRF checks use the same preprocessed live-source view for the exact token endpoint and header contract, so declarations hidden behind inactive `#if` branches cannot satisfy either contract family.

`scripts/ci/test_av_mig_contract_boundary.py` is the deterministic fail-closed mutation harness. It includes positive baseline/active-symbol coverage plus negative mutations for OpenAPI security drift, SignalR authorization removal, method removal/rename, parameter-count/type and return-type drift, top-level and nested decoy types, route/event drift, comment-out mutations, inactive `#if` mutations across SignalR and CSRF sentinel families, and CSRF endpoint/header drift. The documentation intentionally does not hard-code the mutation count so adding a new sentinel cannot make this description stale.

`scripts/ci/generate-security-openapi-contract.sh` runs the mutation harness before contract generation and runs the AV-MIG verifier against both deterministic generated OpenAPI copies. Focused C# transformer tests also pin the `[AllowAnonymous]` explicit-empty-security behavior. The required security CI path invokes the generator, so these are CI-enforced sentinels rather than inventory-only metadata.

This is a baseline breaking-change detector, not a complete semantic diff engine. #772 may add generated-client drift checks and a richer OpenAPI diff gate once the exact Kiota toolchain is pinned.

## Client-independent implementation checklist

An Avalonia feature is allowed to implement from this boundary when all applicable items are true:

- HTTP operation/schema exists in OpenAPI or the non-OpenAPI contract is documented here;
- backend owns authorization and protected filtering;
- required error/status behavior can be mapped without parsing English messages;
- paging/order semantics are explicit;
- enum/date/nullability representation is known;
- mutation retry/idempotency/version semantics are known;
- generated DTO can be mapped to an application model without Angular source;
- no Angular service/facade/effect contains correctness-critical class 3/4 logic absent from backend.

## Known gaps / follow-up ownership

1. **Repository-wide error normalization is incomplete.** #534 owns removal of legacy controller-local error shapes. #766 defines the client boundary and compatibility direction; it does not silently rewrite every controller.
2. **#765 is still required for exhaustive Angular classification.** The representative classification above is sufficient to start backend/client boundary work, but #766 should not be considered fully closed until #765's inventory is reconciled and no P0 class 3/4 rule exists only in Angular.
3. **Generated-client implementation belongs to #772.** #766 chooses the boundary and generator direction; it does not add generated source prematurely.
4. **Avalonia auth/session implementation belongs to #773.** This document fixes the wire/session/CSRF semantics it must implement.
5. **SignalR and platform file I/O implementations belong to #778/#779.** Their server contracts are fixed here.

## Acceptance mapping for #766

| Acceptance criterion | Evidence in this change |
| --- | --- |
| P0 endpoint/DTO inventory exists | OpenAPI source of truth + family inventory above + machine-readable P0 operation sentinels + existing detailed Angular binding inventory |
| Angular business/auth logic classified | classification model and representative mappings above; exhaustive reconciliation waits for #765 |
| no client-side authorization dependency plan | explicit backend-authority rules and class 4 migration rule |
| error/date/enum/pagination documented | dedicated sections above |
| OpenAPI -> C# generation decision | Kiota selected; implementation delegated to #772 |
| SignalR/File contract integrated | dedicated sections above + deterministic non-OpenAPI source checks |
| breaking/additive policy exists | dedicated compatibility policy above + deterministic contract sentinel gate |
| Avalonia can implement without Angular source | contract source order and client-independent checklist above |

## Reaudit status and remaining reconciliation

Status: **PARTIALLY_FIXED**. Keep #765, #766 and #798 open. Passing sentinels
does not establish complete API/DTO coverage or migration readiness.

`p0-operation-inventory.json` separately pins the existing 25 P0 operations.
The verifier compares policy and OpenAPI against it, preventing simultaneous
policy/document deletion from silently passing. It is not an independently
proven list of every P0 operation; manifest changes still require scope review.

The CI generator executes production CLI mutations and isolated compiled runtime
mutations. Runtime inspection checks the actual composed Hub endpoints, effective
policy, default authenticate/challenge schemes and callable method signatures.
It runs in Test with seed paths disabled by the harness, without starting database
workers. Source checks deliberately reject unsupported conditional/chained
Hub registration forms. Event checks establish literal call-site presence, not
event delivery, payload completeness or authorization at dispatch time.

| Remaining work | Required evidence | Owner |
| --- | --- | --- |
| Complete P0 operation coverage | Every required/embedded surface mapped from real HTTP calls to method/path/controller/action, with omissions recorded | #765 / #766 |
| Complete DTO coverage | Request/success/error schema, status, nullability, enum/date/paging, idempotency and concurrency fields per operation | #766 |
| Angular classification | Each component/service/facade/mapper function classified, including child and cross-cutting owners | #765 / #766 |
| Server authority proof | Every class-3/4 rule linked to backend use case and direct HTTP denial/tenant isolation tests; unknown is not zero | #766 |
| Files and realtime semantics | Grant/preview/version/reconnect/event DTO behavior reconciled against source and integration tests | #766 / #778 / #779 |
| Demo scope | Angular primary-demo acceptance distinguished from Avalonia core technical preview; no expansion inferred from Wave numbers | #765 / #798 |
| Final gates | Updated PR heads, required CI and reviewed integration evidence | #765 / #766 |

The representative classification file is not an exhaustive zero-defect audit.
The inventory checker follows directly injected registered state owners;
transitive ownership and behavioral authority remain review work. PostgreSQL,
browser end-to-end and full event-delivery tests remain separate gates.
