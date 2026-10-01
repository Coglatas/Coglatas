# MVP-A Final Release Audit

Audit date: 2026-07-04 (Asia/Tokyo workspace date)

Scope: final release-candidate audit for the completed Initial MVP-A P0 implementation. This audit did not add features, weaken tests, change screenshot thresholds, or add production secrets.

## Executive summary

Final recommendation: **Go for MVP-A release candidate**.

No current P0 release blocker was found in the audited code, configuration, documentation, and focused local verification. The recommendation is limited to an MVP-A release candidate and does not mean unrestricted production or pilot use with real school or personal information.

The strongest release evidence is:

- Authentication and CSRF flows have backend middleware coverage plus frontend one-time CSRF retry handling.
- Backend authorization checks exist for tenant, workspace, project, member, file, conversation, admin, and student-record boundaries; the audited paths are not frontend-only hiding.
- File download grants reauthorize current user, tenant, actor, scope, classification, object state, and policy stamp before download.
- Focused backend security/readiness tests passed locally: 126 passed, 0 failed, 0 skipped.
- Frontend unit tests passed locally outside the Windows sandbox: 17 test files and 125 tests passed.
- EF migrations are discoverable through the local tool manifest, and CI applies PostgreSQL migrations.
- Docker compose files validate with `.env.example`; CI includes compose validation, Docker image build, Gitleaks, and Trivy jobs.

The remaining items are operational and hardening work. They should be fixed before broader operator handoff, production-like pilot data, or school personal-information processing.

## Final recommendation

**Go for MVP-A release candidate.**

Basis:

- P0 blockers: none found.
- P1 required soon: yes, mostly release-ops documentation and API contract consistency.
- P2 improvements: yes, mostly polish and hardening.
- Watch items: yes, especially local Docker runtime verification and production operating assumptions.

## P0 blockers

None found in this audit.

Explicitly checked:

- Unsafe methods require CSRF protection server-side.
- Frontend CSRF retry is bounded to one retry and avoids third-party requests.
- 401 terminal session handling clears frontend auth state and navigates to login.
- 403 authorization denial is not treated as logout.
- Auth token state is not stored in `localStorage`; `sessionStorage` usage found was UI-only state such as right-panel state and messaging drafts.
- Admin API paths are protected by backend authorization and service-level active-admin checks.
- File download grants are not bearer URLs alone; they require current authenticated actor and reauthorization.
- Path traversal and dangerous filename handling have dedicated storage and filename sanitizer coverage.
- Global exception handling avoids returning stack traces, internal paths, SQL text, or password-like details.
- `.env` is ignored; only `.env.example` is tracked.
- No screenshot threshold weakening was found in the Angular P0 screenshot helper.

## P1 required soon

1. Release-ops docs contradict the current development admin bootstrap.

   `README.md` documents the current development-only `LocalAdmin__SeedOnStartup` path, but `docs/DEPLOYMENT.md` and `docs/OPERATIONS.md` still state that no first-user or admin bootstrap exists. This is not a runtime P0 because the implementation and README are present, but it is a release-operations risk for anyone following the runbooks.

   Evidence:

   - `README.md`
   - `README.dev-env.md`
   - `docs/DEPLOYMENT.md`
   - `docs/OPERATIONS.md`
   - `src/AipPortal.Web/Program.cs`
   - `src/AipPortal.Infrastructure/Persistence/AppDbContextSeed.cs`
   - `tests/AipPortal.Tests/Persistence/AppDbContextSeedTests.cs`

2. API error shape and status semantics remain mixed.

   The global exception path returns the structured `ErrorResponse` and tests verify no internal detail leakage. Several controller/service paths still return compact `{ error }` responses and some authorization/not-found conditions are normalized at service/controller boundaries rather than through one uniform envelope. This is acceptable for MVP-A RC because it does not expose stack traces and the frontend adapter has coverage, but it should be standardized before stricter API binding or broader integrations.

   Evidence:

   - `src/AipPortal.Web/Middleware/GlobalExceptionHandlingMiddleware.cs`
   - `tests/AipPortal.Tests/Auth/GlobalExceptionHandlingMiddlewareTests.cs`
   - `frontend/src/app/core/api/api-error.adapter.ts`
   - `frontend/src/app/core/api/api-error.adapter.spec.ts`
   - `docs/frontend/api-binding-verification.md`

3. Production-grade admin recovery remains a runbook gap.

   Development local-admin seeding is present and explicitly development-only. A supported production administrator recovery and break-glass flow still needs to be documented and practiced before a real school deployment.

   Evidence:

   - `README.md`
   - `README.dev-env.md`
   - `docs/OPERATIONS.md`
   - `docs/DEPLOYMENT.md`

## P2 improvements

1. Fix route placeholder copy mojibake.

   `frontend/src/app/app.routes.ts` contains mojibake placeholder labels. This is not a release-security blocker, but it should be cleaned up for RC polish if those placeholder routes can be reached.

2. Tighten development Docker credential defaults.

   Docker development credentials are clearly development-only, `.env.example` is safe, and root/local compose validation succeeds with `.env.example`. `infra/compose/dev/full.yml` still has fallback development credentials for local convenience. Consider requiring explicit local overrides before sharing the dev environment broadly.

3. Add a live local Docker smoke proof when Docker Desktop is available.

   Compose schema validation passed, but this machine's Docker Desktop Linux engine was not running, so `docker compose -f infra/compose/dev/db.yml up -d postgres` could not start PostgreSQL locally. CI contains PostgreSQL migration and Docker checks, but the local runtime path should be captured once the daemon is available.

4. Expand optimistic concurrency where concurrent editing becomes user-facing.

   The audit found conflict UI stories and documentation, but no broad `RowVersion` or EF concurrency-token implementation. For MVP-A RC this is a watch-level product risk, not a P0 security issue. It becomes more important when collaborative edits are enabled against live data.

5. Keep object storage and restore behavior as explicit limitations.

   Local file storage boundaries are covered, and object storage is still a known limitation. Do not imply production object-storage durability until that path exists and is tested.

## Watch items

- This recommendation relies partly on the user-provided context that Angular P0 tests, screenshot regression, and GitHub Actions have passed.
- Local Docker runtime was not verified because Docker Desktop's Linux engine was unavailable. Compose files validated; no container was started.
- The local EF migration listing used `--no-connect`. CI is the evidence path for applying migrations against PostgreSQL.
- The audit did not fetch GitHub Actions artifacts, Gitleaks reports, Trivy reports, or screenshot artifacts from the remote run.
- `.env` exists locally but is ignored and was not inspected; tracked-file scans and `git ls-files` were used for repository-secret review.
- MVP-A RC should not be used with real school personal information until the P1 runbook gaps, admin recovery, backup/restore drill, and production data-handling checklist are closed.
- Messaging remains HTTP mock/API-boundary work only where documented; do not infer real-time SignalR/WebSocket/SSE/presence guarantees.
- Invite registration and onboarding remain limited to the implemented contracts and documentation; do not rely on undocumented enrollment flows for release operations.

## Exact commands run

Repository and memory/context orientation:

```powershell
git status --short
rg --files -g "*.sln*" -g "package.json" -g "docker-compose*.yml" -g ".env*" -g "*.md" -g "*.yml"
Get-ChildItem -Name
```

Security and implementation inspection:

```powershell
Get-Content src\AipPortal.Web\Program.cs
Get-Content src\AipPortal.Web\Middleware\CsrfProtectionMiddleware.cs
Get-Content src\AipPortal.Web\Controllers\AuthController.cs
Get-Content src\AipPortal.Web\Controllers\SecurityController.cs
Get-Content src\AipPortal.Application\Auth\AuthService.cs
Get-Content src\AipPortal.Application\Auth\LoginResponse.cs
Get-Content src\AipPortal.Web\Security\DbSessionCookieAuthenticationEvents.cs
Get-Content src\AipPortal.Application\Auth\UserSessionService.cs
Get-Content src\AipPortal.Infrastructure\Persistence\AppDbContextSeed.cs
Get-Content frontend\src\app\core\auth\auth-session.interceptor.ts
Get-Content frontend\src\app\core\auth\csrf-token.service.ts
Get-Content frontend\src\app\core\auth\auth-session.facade.ts
Get-Content frontend\src\app\app.routes.ts
Get-Content src\AipPortal.Application\Files\FileService.cs
Get-Content src\AipPortal.Infrastructure\Files\LocalFileStorageService.cs
Get-Content src\AipPortal.Application\Files\FileNameSanitizer.cs
Get-Content src\AipPortal.Web\Middleware\GlobalExceptionHandlingMiddleware.cs
Get-Content src\AipPortal.Infrastructure\Persistence\AppDbContext.cs
```

Frontend guardrail scans:

```powershell
git grep -n -I -E "(trim\(\).*password|password.*trim\(|localStorage|sessionStorage|AgGridAngular|ag-grid-enterprise|src/AipPortal.Web/wwwroot|wwwroot SPA|legacy static SPA)" -- frontend\src tests\ui frontend\README.md docs\frontend
rg -n "toHaveScreenshot|threshold|maxDiff|skip\(" tests\ui frontend\src docs\frontend
```

Secrets and configuration scans:

```powershell
git ls-files .env local.env .env.example
git grep -n -I -E "(AKIA[0-9A-Z]{16}|ghp_[A-Za-z0-9_]{36,}|github_pat_[A-Za-z0-9_]{20,}|BEGIN (RSA |EC |OPENSSH |PRIVATE )?PRIVATE KEY|xox[baprs]-[A-Za-z0-9-]{10,})"
git grep -n -I -E "(LOCAL_ADMIN_PASSWORD=|POSTGRES_PASSWORD=|Password=|Password:)"
Get-Content .gitignore
Get-Content .env.example
Get-Content .github\workflows\ci.yml
```

Docker and CI documentation:

```powershell
Get-Content infra/compose/dev/db.yml
Get-Content infra/compose/dev/full.yml
Get-Content infra/compose/dev/local.yml
Get-Content infra/compose/dev/playwright.yml
Get-Content README.dev-docker.md
Get-Content README.dev-env.md
Get-Content README.md
Get-Content docs\DEPLOYMENT.md
Get-Content docs\OPERATIONS.md
rg -n "postgres|database update|migrations|dotnet test|docker compose|gitleaks|trivy|test:ui:angular:docker" .github\workflows\ci.yml
```

Docker validation:

```powershell
docker compose -f infra/compose/dev/db.yml config --quiet
docker compose -f infra/compose/dev/full.yml config --quiet
docker compose -f infra/compose/dev/playwright.yml config --quiet
docker compose config --quiet
docker compose -f infra/compose/dev/local.yml config --quiet
docker compose -f deploy/onprem/compose.yml config --quiet
docker compose --env-file .env.example config --quiet
docker compose --env-file .env.example -f infra/compose/dev/local.yml config --quiet
docker compose --env-file .env.example -f deploy/onprem/compose.yml config --quiet
docker compose --env-file .env.example -f infra/compose/dev/playwright.yml config --quiet
docker compose -f infra/compose/dev/db.yml up -d postgres
```

Docker outcomes:

- The compose `config --quiet` commands passed when required variables were supplied through `.env.example`.
- Root/local compose validation without an env file failed as expected because `LOCAL_ADMIN_PASSWORD` is required.
- `docker compose -f infra/compose/dev/db.yml up -d postgres` failed before making changes because Docker Desktop's Linux engine was not running.

EF and database readiness:

```powershell
dotnet tool restore
dotnet ef migrations list --project src\AipPortal.Infrastructure --startup-project src\AipPortal.Web --no-build --no-connect
dotnet tool run dotnet-ef migrations list --project src\AipPortal.Infrastructure --startup-project src\AipPortal.Web --no-build --no-connect
$env:ConnectionStrings__DefaultConnection='Host=localhost;Port=5433;Database=aipportal_dev;Username=aipportal;Password=aipportal_dev_password'; dotnet tool run dotnet-ef migrations list --project src\AipPortal.Infrastructure --startup-project src\AipPortal.Web --no-build --no-connect
```

EF outcomes:

- `dotnet tool restore` restored `dotnet-ef` 10.0.8.
- Direct `dotnet ef` did not resolve the local tool from the sandboxed process.
- `dotnet tool run dotnet-ef ...` required a design-time connection string even with `--no-connect`.
- With the documented local development connection string, EF listed all migrations through `20260630120000_AddConversationParticipantPrivateState` with `--no-connect`.

Focused verification:

```powershell
dotnet test AipPortal.slnx --configuration Release --no-restore --filter "FullyQualifiedName~AuthSecurityHttpTests|FullyQualifiedName~GlobalExceptionHandlingMiddlewareTests|FullyQualifiedName~FileDownloadGrantBoundaryTests|FullyQualifiedName~LocalFileStorageServiceTests|FullyQualifiedName~FileNameSanitizerTests|FullyQualifiedName~TenantIsolationSecurityTests|FullyQualifiedName~HttpTenantIsolationTests|FullyQualifiedName~PaginationSafetyTests|FullyQualifiedName~AppDbContextSeedTests|FullyQualifiedName~StudentRecordRestrictedTests" -m:1
npm.cmd run test
```

Focused verification outcomes:

- Backend filtered security/readiness suite: 126 passed, 0 failed, 0 skipped.
- Frontend Angular unit tests: 17 test files passed, 125 tests passed.
- The first frontend test run inside the Windows sandbox failed with access-denied/module-resolution symptoms; rerunning outside the sandbox passed.

## Evidence paths

Authentication, session, and CSRF:

- `src/AipPortal.Web/Middleware/CsrfProtectionMiddleware.cs`
- `src/AipPortal.Web/Controllers/AuthController.cs`
- `src/AipPortal.Web/Controllers/SecurityController.cs`
- `src/AipPortal.Application/Auth/AuthService.cs`
- `src/AipPortal.Application/Auth/UserSessionService.cs`
- `src/AipPortal.Application/Auth/LoginResponse.cs`
- `src/AipPortal.Web/Security/DbSessionCookieAuthenticationEvents.cs`
- `frontend/src/app/core/auth/auth-session.interceptor.ts`
- `frontend/src/app/core/auth/auth-session.interceptor.spec.ts`
- `frontend/src/app/core/auth/csrf-token.service.ts`
- `tests/AipPortal.Tests/Auth/AuthSecurityHttpTests.cs`

Authorization and tenant/workspace boundaries:

- `src/AipPortal.Web/Controllers/AdminController.cs`
- `src/AipPortal.Web/Controllers/PlatformTenantsController.cs`
- `src/AipPortal.Web/Controllers/TenantsController.cs`
- `src/AipPortal.Application/Workspaces/WorkspaceAuthorizationService.cs`
- `src/AipPortal.Application/Messaging/ConversationAuthorizationService.cs`
- `src/AipPortal.Infrastructure/Persistence/AppDbContext.cs`
- `tests/AipPortal.Tests/Tenancy/TenantIsolationSecurityTests.cs`
- `tests/AipPortal.Tests/Tenancy/HttpTenantIsolationTests.cs`

File and attachment security:

- `src/AipPortal.Application/Files/FileService.cs`
- `src/AipPortal.Application/Files/FileNameSanitizer.cs`
- `src/AipPortal.Infrastructure/Files/LocalFileStorageService.cs`
- `tests/AipPortal.Tests/Files/FileDownloadGrantBoundaryTests.cs`
- `tests/AipPortal.Tests/Files/LocalFileStorageServiceTests.cs`
- `tests/AipPortal.Tests/Files/FileNameSanitizerTests.cs`
- `tests/AipPortal.Tests/StudentRecords/StudentRecordRestrictedTests.cs`

API validation and error handling:

- `src/AipPortal.Web/Middleware/GlobalExceptionHandlingMiddleware.cs`
- `tests/AipPortal.Tests/Auth/GlobalExceptionHandlingMiddlewareTests.cs`
- `tests/AipPortal.Tests/Pagination/PaginationSafetyTests.cs`
- `frontend/src/app/core/api/api-error.adapter.ts`
- `docs/API_CONTRACTS.md`
- `docs/frontend/api-binding-verification.md`

Database and migrations:

- `src/AipPortal.Infrastructure/Persistence/AppDbContext.cs`
- `src/AipPortal.Infrastructure/Persistence/AppDbContextDesignTimeFactory.cs`
- `src/AipPortal.Infrastructure/Persistence/Migrations/`
- `tests/AipPortal.Tests/PostgreSql/PostgreSqlIntegrationTests.cs`
- `.github/workflows/ci.yml`

Frontend guardrails:

- `frontend/src/app/app.routes.ts`
- `frontend/src/app/layout/app-shell/app-shell.component.*`
- `frontend/src/app/shared/data-grid/app-data-grid.component.*`
- `tests/ui/angular-smoke.spec.ts`
- `docs/frontend/p0-angular-final-verification.md`

Secrets, config, CI, Docker, and operations:

- `.gitignore`
- `.env.example`
- `.github/workflows/ci.yml`
- `infra/compose/dev/db.yml`
- `infra/compose/dev/full.yml`
- `infra/compose/dev/local.yml`
- `infra/compose/dev/playwright.yml`
- `README.md`
- `README.dev-env.md`
- `README.dev-docker.md`
- `docs/DEPLOYMENT.md`
- `docs/OPERATIONS.md`
- `docs/SECURITY_RULES.md`
- `docs/KNOWN_ISSUES.md`

## Unresolved assumptions

- GitHub Actions, Angular P0 screenshot regression, and full CI pass are accepted from the user's provided context; this local audit did not fetch remote CI artifacts.
- Docker Desktop was not running locally, so runtime Docker PostgreSQL startup was not reproduced. Compose validation passed, and CI remains the applied PostgreSQL migration evidence.
- The audit did not execute a full live backup/restore drill.
- The audit did not inspect the contents of the ignored local `.env` file.
- The recommendation assumes MVP-A release candidate scope, not production use with real school personal information.
