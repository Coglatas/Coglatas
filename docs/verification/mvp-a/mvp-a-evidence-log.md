# MVP-A Evidence Log

Verification date: 2026-06-24

Result status values: Pass, Partial, Failed, Blocked, Missing, Needs verification.

## A-01 Baseline Refresh

Refresh date: 2026-06-28

Detailed evidence:

- `docs/evidence/mvp-a/a-01-build-test-baseline.md`
- `docs/evidence/mvp-a/a-01-baseline-failure-log.md`

Summary: `dotnet restore`, `dotnet build`, and `dotnet test` passed on Windows with .NET SDK 10.0.301; the test runner reported 128/128 passing. Docker build/startup evidence is blocked because the Docker Desktop Linux engine endpoint is unavailable. Local UI test evidence is blocked because Playwright is not installed in `node_modules`. Live PostgreSQL assertions remain Needs verification because `POSTGRES_TEST_CONNECTION_STRING` was not set.

## A-02 Health Check Refresh

Refresh date: 2026-06-28

Detailed evidence:

- `docs/evidence/mvp-a/a-02-health-check-baseline.md`
- `docs/evidence/mvp-a/a-02-health-check-failure-log.md`

Summary: the app started locally and `/health/live` returned 200 with `{"status":"OK"}`, but `/health/ready` returned 503 with `{"status":"Unhealthy"}`. Local PostgreSQL on port 5432 was unavailable, Docker Desktop Linux engine was unavailable, and PostgreSQL/container health could not be verified. This A-02 refresh is Blocked and does not imply production approval.

## A-03 Application Smoke Refresh

Refresh date: 2026-06-28

Detailed evidence:

- `docs/evidence/mvp-a/a-03-application-smoke-baseline.md`
- `docs/evidence/mvp-a/a-03-smoke-failure-log.md`

Summary: the app started locally on `http://127.0.0.1:18083`; root/login/dashboard SPA shell routes returned 200; `/health/live` returned 200; `/health/ready` returned 503; public auth status returned unauthenticated; CSRF protection rejected unsafe anonymous POSTs; protected anonymous APIs returned 401; API 404 returned safe JSON. Local PostgreSQL on port 5432 and Docker Desktop Linux engine were unavailable. Frontend Angular, Storybook, and root Playwright smoke checks were blocked by incomplete local `node_modules`. This A-03 refresh is Blocked and does not imply production approval, MVP-A Go, or production readiness.

## A-04 AuthZ Boundary Refresh

Refresh date: 2026-06-28

Detailed evidence:

- `docs/evidence/mvp-a/a-04-authz-boundary-baseline.md`
- `docs/evidence/mvp-a/a-04-authz-boundary-failure-log.md`

Summary: source inspection and automated backend verification found cookie auth, CSRF, session invalidation, tenant/project/conversation/file isolation, admin denial, audit-log role checks, and security-event tenant scoping materially covered by tests. A test-harness Data Protection key-path failure was fixed, then `AuthSecurityHttpTests` passed 15/15, `TenantIsolation` filtered tests passed 24/24, and the full backend suite passed 128/128. A-04 remains Needs verification for fresh-runtime authenticated smoke because the baseline identity/bootstrap blocker still prevents direct admin/non-admin/wrong-tenant runtime checks on a fresh app baseline. Docker runtime and local PostgreSQL also remained unavailable. This A-04 refresh does not imply production approval, MVP-A Go, or production readiness.

## A-05 Sensitive Data Boundary Refresh

Refresh date: 2026-06-28

Detailed evidence:

- `docs/evidence/mvp-a/a-05-sensitive-data-boundary-baseline.md`
- `docs/evidence/mvp-a/a-05-sensitive-data-boundary-failure-log.md`

Summary: repo keyword scans and source inspection found no confirmed committed raw secret in this pass, and the ignored local `.env` was treated as local sensitive config without copying values. A source-level Development error-response leak was fixed by making global unhandled exception responses generic in every environment, and the new regression test plus full backend suite passed 129/129. A-05 remains Needs verification because local redacted Gitleaks reproduction is blocked by missing local `gitleaks` and unavailable Docker daemon, live runtime logs were not captured, authenticated API/UI/export smoke remains blocked by P0-001/P0-002, and existing historical docs/evidence still need broader human review. This A-05 refresh does not imply production approval, MVP-A Go, or production readiness.

## A-07 File Boundary Refresh

Refresh date: 2026-06-29

Detailed evidence:

- `docs/evidence/mvp-a/a-07-file-boundary-baseline.md`
- `docs/evidence/mvp-a/a-07-file-boundary-failure-log.md`

Summary: source inspection and automated backend verification found no confirmed unauthorized file-body exposure in the tested synthetic paths after this pass. A metadata-only storage identifier exposure risk was fixed by removing internal storage identifiers from file and artifact-version API response DTOs, private no-store headers were added to file/attachment/artifact download responses, and metadata-only denied file access audit entries were added. The targeted file/storage/tenant-boundary test slice passed 32/32, and the full backend suite passed 134/134. A-07 remains Needs verification for fresh-runtime file smoke under signed-in test users, attachment/conversation sensitive-body actor coverage, removed-participant and explicit-grant/revoked-grant behavior, object-storage/signed-URL behavior, and live PostgreSQL/container evidence. This A-07 refresh does not imply production approval, MVP-A Go, or production readiness.

## A-08 Communication Boundary Refresh

Refresh date: 2026-06-29

Detailed evidence:

- `docs/evidence/mvp-a/a-08-communication-boundary-baseline.md`
- `docs/evidence/mvp-a/a-08-communication-boundary-failure-log.md`

Summary: source inspection and automated backend verification found no confirmed unauthorized conversation/message body exposure in the tested synthetic paths after this pass. Three communication boundary risks were fixed: removed message authors now must remain active conversation participants to edit/delete, read cursor updates now reject missing/deleted/cross-conversation message IDs, and message notification bodies no longer embed private message text. The focused HTTP tenant and communication-boundary test slice passed 11/11, and the full backend suite passed 138/138. A-08 remains Needs verification for fresh-runtime authenticated communication smoke, same-tenant DM actor matrix, admin/teacher policy, thread coverage, realtime/polling coverage, live audit/log review, live PostgreSQL/container evidence, and final safe-denial status-code classification. This A-08 refresh does not imply production approval, MVP-A Go, or production readiness.

| Evidence ID | Area | Command or method | Environment | Observed result | Status | Related blocker | Sensitive data status |
| --- | --- | --- | --- | --- | --- | --- | --- |
| EV-001 | Repository inventory | `find . -maxdepth 3 ...` and source inspection | Workspace | Found `AipPortal.slnx`, four source projects, one .NET test project, UI tests, Dockerfiles/Compose, and GitHub Actions CI. | Pass | None | No secrets copied |
| EV-002 | Build / Startup | `dotnet --info` | Ubuntu 24.04 container | .NET SDK 10.0.200 and ASP.NET Core runtime 10.0.4 available. | Pass | None | No secrets |
| EV-003 | Build / Startup | `dotnet restore AipPortal.slnx` | Workspace, escalated for package/cache access | Restore succeeded; all projects up-to-date. | Pass | None | No secrets |
| EV-004 | Build / Startup | `dotnet build AipPortal.slnx --configuration Release --no-restore` | Sandbox | Failed with no compiler errors due MSBuild `SocketException (13): Permission denied` creating named pipe server streams. | Blocked | Environment limitation | No secrets |
| EV-005 | Build / Startup | `dotnet build AipPortal.slnx --configuration Release --no-restore --disable-build-servers -m:1` | Escalated process permissions | Build succeeded, 0 warnings, 0 errors. | Pass | None | No secrets |
| EV-006 | CI / tests | `dotnet test AipPortal.slnx --configuration Release --no-build --verbosity normal --disable-build-servers -m:1` | Escalated process permissions | 123 tests passed. PostgreSQL tests were counted but require separate connection-string verification. | Partial | None | No secrets |
| EV-007 | EF Core / PostgreSQL | `docker compose -f infra/compose/dev/local.yml up -d postgres` | Docker local | PostgreSQL 18 container started and became healthy. | Pass | None | Local throwaway credential only |
| EV-008 | EF Core / PostgreSQL | `dotnet ef migrations list` before update | Local PostgreSQL | All 12 migrations listed as pending on empty `aip_portal` database. | Pass | None | Local throwaway credential redacted |
| EV-009 | EF Core / PostgreSQL | `dotnet ef database update` | Local PostgreSQL | All migrations applied successfully. | Pass | None | Local throwaway credential redacted |
| EV-010 | EF Core / PostgreSQL | `dotnet ef migrations list` after update | Local PostgreSQL | 12 migrations listed without pending markers. | Pass | None | Local throwaway credential redacted |
| EV-011 | EF Core / PostgreSQL | `POSTGRES_TEST_CONNECTION_STRING=... dotnet test ... --filter Category=PostgreSQLIntegration` | Local PostgreSQL | 2 PostgreSQL integration tests passed with real DB assertions. | Pass | None | Local throwaway credential redacted |
| EV-012 | Startup | `dotnet run --project src/AipPortal.Web --configuration Release --no-build` | Local PostgreSQL | App started, but launch profile forced `Development` and `http://localhost:5098`. | Partial | Environment/config note | Connection string redacted |
| EV-013 | Startup | `dotnet run ... --no-launch-profile` with `ASPNETCORE_ENVIRONMENT=Test` | Local PostgreSQL | App started in Test on `http://127.0.0.1:5086`. | Pass | None | Connection string redacted |
| EV-014 | Health check | `GET /health` | Running app | Returned `302` to `/health/ready`. | Pass | None | No secrets |
| EV-015 | Health check | `GET /health/live` | Running app | Returned `200` and `{"status":"OK"}`. | Pass | None | No secrets |
| EV-016 | Health check | `GET /health/ready` | Running app | Returned `200` with checks for database, migrations, fileStorage, dataProtection, and defaultTenant all OK. | Pass | None | No secrets |
| EV-017 | Health check | `GET /healthz` | Running app | Returned SPA fallback HTML, not a health endpoint. | Missing | P1 follow-up | No secrets |
| EV-018 | Health check | `GET /api/health` | Running app | Returned `404` JSON. | Missing | P1 follow-up | No secrets |
| EV-019 | Auth / Login | `GET /api/auth/status` | Running app | Returned `200` with unauthenticated status. | Pass | None | No secrets |
| EV-020 | Auth / Login | `GET /api/auth/me` | Running app | Returned `401 Unauthorized`. | Pass | None | No secrets |
| EV-021 | Auth / Login | `POST /api/auth/login` without CSRF | Running app | Returned `403` with CSRF error. | Pass | None | Dummy credentials only |
| EV-022 | Auth / Login | `POST /api/auth/login` with valid CSRF and invalid credentials | Running app | Returned `401` with generic error `Invalid email or password.` | Pass | None | Dummy credentials only |
| EV-023 | Tenant / User / Role | Fresh DB startup counts | Fresh local PostgreSQL database | After migrations and Test startup: tenants 1, plans 4, users 0, tenant_users 0. | Failed | P0-001 | Counts only |
| EV-024 | Authorization | `GET /api/admin/users` anonymous | Running app | Returned `401 Unauthorized`. | Pass | P0-002 for deeper checks | No secrets |
| EV-025 | Authorization | `GET /api/projects` anonymous | Running app | Returned `401 Unauthorized`. | Pass | P0-002 for deeper checks | No secrets |
| EV-026 | Authorization | `GET /api/ui/modules` anonymous | Running app | Returned `401 Unauthorized`. | Pass | P0-002 for deeper checks | No secrets |
| EV-027 | Dashboard reachability | `GET /` and `GET /dashboard` anonymous | Running app | Returned SPA login HTML. Authenticated dashboard data could not be verified without a user. | Partial | P0-001 | No private data |
| EV-028 | AuditLog | Source inspection | `DbAuditLogger`, `AuditLog`, `SecurityEvent`, services | AuditLog/SecurityEvent models and logger exist; metadata sensitive-key redaction exists; many services call audit logger. Runtime authenticated audit coverage blocked by no user. | Partial | P0-001 | No secrets |
| EV-029 | File / Messaging baseline | Source inspection | File and messaging services/controllers | File upload/download/delete use storage, permission checks, and audit calls. Messaging APIs require authorization and audit message actions; conversation attachment path is metadata-only. | Partial | P1 follow-up | No secrets |
| EV-030 | CI / tests | `.github/workflows/ci.yml` inspection | Workspace | CI includes PostgreSQL service, restore, build, migrations, tests, npm ci, Playwright install/tests, secret scan, dependency scan, Compose validation, Docker build, Trivy scan. | Pass | None | No secrets |
| EV-031 | CI / tests | `npm test -- --reporter=list` | Workspace | Failed with `playwright: not found` because `node_modules` is absent. | Blocked | P1 follow-up | No secrets |
| EV-032 | Docker / Compose | `docker compose config` | Workspace | Failed without `POSTGRES_PASSWORD`. | Partial | P1 follow-up | No secrets |
| EV-033 | Docker / Compose | `POSTGRES_PASSWORD=verification_only_password docker compose config` and on-prem equivalent | Workspace | Default and on-prem Compose configs rendered successfully with dummy value. Local Compose config rendered successfully without extra env. | Pass | None | Dummy value only |
| EV-034 | A-02 health source | Source inspection of `Program.cs`, Docker/Compose, appsettings, and deployment docs | Windows host | Existing custom endpoints are `/health`, `/health/live`, and `/health/ready`; `/healthz`, `/ready`, and `/live` are not dedicated health endpoints. | Pass | None | No secrets |
| EV-035 | A-02 local liveness | `curl.exe -i -s http://localhost:5098/health/live` | Windows host, local `dotnet run` | Returned 200 with `{"status":"OK"}`. | Pass | None | No secrets |
| EV-036 | A-02 local readiness | `curl.exe -i -s http://localhost:5098/health/ready` | Windows host, local `dotnet run` | Returned 503 with `{"status":"Unhealthy"}` because required DB-backed readiness was unavailable in this run. | Blocked | A-02 health baseline | No secrets |
| EV-037 | A-02 DB dependency | `Test-NetConnection -ComputerName localhost -Port 5432` | Windows host | TCP connection to local PostgreSQL port 5432 failed. | Blocked | A-02 health baseline | No secrets |
| EV-038 | A-02 Docker dependency | `docker info`; `docker compose --env-file .env.example ps` | Windows host | Docker Desktop Linux engine endpoint unavailable; PostgreSQL/app container health could not be inspected. | Blocked | A-02 health baseline | No secrets |
| EV-039 | A-02 backend suite | `dotnet test AipPortal.slnx --configuration Release --no-build --verbosity normal --disable-build-servers -m:1` | Windows host | 115 passed, 13 failed; failures clustered in `AuthSecurityHttpTests` CSRF/login setup returning 500. | Blocked | A-02 clean gate evidence | No secrets |
| EV-040 | A-03 backend suite | `dotnet restore`; `dotnet build`; `dotnet test` | Windows host | Restore passed, Release build passed with 0 warnings/errors, and 128/128 backend tests passed. | Pass | None | No secrets |
| EV-041 | A-03 app startup | `dotnet run ... --no-launch-profile` | Windows host | App started in Development on `http://127.0.0.1:18083`. | Pass | None | No secrets |
| EV-042 | A-03 root and shell routes | `GET /`, `/login`, `/dashboard` | Running local app | Returned SPA shell HTML with no private data observed. Authenticated dashboard data was not verified. | Partial | P0-001 for authenticated dashboard | No private data |
| EV-043 | A-03 health routes | `GET /health`, `/health/live`, `/health/ready` | Running local app | `/health` redirected to readiness, `/health/live` returned 200, and `/health/ready` returned 503 with minimal unhealthy JSON. | Blocked | A-03 smoke baseline | No secrets |
| EV-044 | A-03 health alias candidates | `GET /healthz`, `/ready`, `/live` | Running local app | Returned SPA fallback HTML, not dedicated health endpoints. | Missing | P1 follow-up | No secrets |
| EV-045 | A-03 Swagger/OpenAPI candidates | `GET /swagger`, `/swagger/index.html`, `/openapi` | Running local app | No dedicated Swagger/OpenAPI endpoint was verified; routes returned SPA fallback or 404. | Needs verification | A-03 smoke baseline | No secrets |
| EV-046 | A-03 public auth status | `GET /api/auth/status`; `GET /api/security/csrf-token` | Running local app | Auth status returned unauthenticated; CSRF endpoint returned a token payload whose value was not copied. | Pass | None | Token value omitted |
| EV-047 | A-03 unsafe anonymous POSTs | `POST /api/auth/login`; `POST /api/auth/logout` without CSRF | Running local app | Returned 403 with generic CSRF error. | Pass | None | Dummy credentials only |
| EV-048 | A-03 anonymous protected APIs | `GET /api/auth/me`; `/api/admin/users`; `/api/projects`; `/api/ui/modules` | Running local app | All returned 401 without exposing private bodies. | Pass | P0-002 for deeper authenticated checks | No private data |
| EV-049 | A-03 safe 404 behavior | `GET /api/not-found-test`; `/api/auth/not-found-test` | Running local app | Returned generic 404 JSON with trace ID and no stack trace observed. | Pass | None | No secrets |
| EV-050 | A-03 local DB dependency | `Test-NetConnection -ComputerName localhost -Port 5432` | Windows host | TCP connection to local PostgreSQL port 5432 failed. | Blocked | A-03 smoke baseline | No secrets |
| EV-051 | A-03 Docker dependency | `docker info`; `docker compose --env-file .env.example config --quiet` | Windows host | Compose config passed, but Docker Desktop Linux engine endpoint was unavailable. | Blocked | A-03 smoke baseline | No secrets |
| EV-052 | A-03 frontend build | `npm.cmd run build` in `aipsite-frontend` | Windows host | Blocked because local Angular CLI was missing from `aipsite-frontend/node_modules`. | Blocked | A-03 frontend smoke | No secrets |
| EV-053 | A-03 Storybook build | `npm.cmd run build-storybook` in `aipsite-frontend` | Windows host | Blocked because local Angular CLI was missing from `aipsite-frontend/node_modules`. | Blocked | A-03 frontend smoke | No secrets |
| EV-054 | A-03 root UI tests | `npm.cmd test -- --reporter=list` | Windows host | Blocked because the Playwright executable was unavailable. | Blocked | A-03 UI smoke | No secrets |
| EV-055 | A-04 source inventory | Source inspection of `Program.cs`, controllers, services, auth/tenant tests, and audit query service | Windows host | Cookie auth, CSRF middleware, `[Authorize]` coverage, resource authorization services, tenant query filters, and audit/security query role checks identified. | Pass | A-04 | No secrets |
| EV-056 | A-04 restore sandbox attempt | `dotnet restore AipPortal.slnx --disable-build-servers` | Sandboxed Windows host | Failed because sandbox blocked NuGet access to `api.nuget.org:443`. | Blocked | Environment permission | No secrets |
| EV-057 | A-04 restore approved network | `dotnet restore AipPortal.slnx --disable-build-servers` | Windows host with approved network access | Restore passed. | Pass | None | No secrets |
| EV-058 | A-04 build | `dotnet build AipPortal.slnx --configuration Release --no-restore --disable-build-servers -m:1` | Windows host | Build passed with 0 warnings and 0 errors. | Pass | None | No secrets |
| EV-059 | A-04 initial full test | `dotnet test AipPortal.slnx --configuration Release --no-build --verbosity normal --disable-build-servers -m:1` | Windows host | Failed 115 passed / 13 failed; all failures were `AuthSecurityHttpTests` blocked by Data Protection key writes to the user profile. | Failed | A-04 test harness | No token values copied |
| EV-060 | A-04 auth security fix | Test-harness Data Protection key path update | Workspace | `AuthSecurityHttpTests` now persists Data Protection keys to an isolated temp directory. | Pass | None | No secrets |
| EV-061 | A-04 auth security tests | `dotnet test tests\AipPortal.Tests\AipPortal.Tests.csproj --configuration Release --no-build --filter FullyQualifiedName~AuthSecurityHttpTests --logger "console;verbosity=normal"` | Windows host | 15/15 passed: CSRF rejection/acceptance, hidden session ID, revoked/expired/disabled sessions, and unsafe-method routing covered. | Pass | None | No token values copied |
| EV-062 | A-04 tenant isolation tests | `dotnet test tests\AipPortal.Tests\AipPortal.Tests.csproj --configuration Release --no-build --filter FullyQualifiedName~TenantIsolation --logger "console;verbosity=normal"` | Windows host | 24/24 passed: tenant, workspace, project, conversation, file, notification, audit, security-event, suspended-tenant, and platform-scope boundaries covered. | Pass | None | Synthetic data only |
| EV-063 | A-04 final backend suite | `dotnet test AipPortal.slnx --configuration Release --no-build --verbosity normal --disable-build-servers -m:1` | Windows host | 128/128 passed. PostgreSQL tests still require separate live connection-string evidence because they return early when `POSTGRES_TEST_CONNECTION_STRING` is absent. | Pass | Live PostgreSQL still Needs verification | No secrets |
| EV-064 | A-04 Compose config | `docker compose --env-file .env.example config --quiet` | Windows host | Compose config passed. | Pass | None | `.env.example` values not copied |
| EV-065 | A-04 Docker daemon | `docker info` | Windows host | Docker client available, but daemon endpoint `npipe:////./pipe/docker_engine` was unavailable; Docker config access warning also observed. | Blocked | Docker runtime | No secrets |
| EV-066 | A-04 PostgreSQL local port | `Test-NetConnection -ComputerName localhost -Port 5432` | Windows host | TCP connection failed. | Blocked | Live PostgreSQL runtime | No secrets |
| EV-067 | A-05 definition/source inventory | Repo search and source inspection | Windows host | No pre-existing A-05 evidence file was found; working definition came from the attached issue text; config, source, docs/evidence, logging, API error, UI, and export/download scopes were inventoried. | Pass | A-05 | No raw values copied |
| EV-068 | A-05 keyword scan | `git grep -n -I -i` A-05 terms, counted without printing raw values | Windows host | Broad keyword counts completed: examples include password 274/105 files, secret 174/52 files, token 3511/232 files, private-key marker 0/0 files, client_secret 0/0 files. | Partial | A-05 scanner follow-up | Counts only |
| EV-069 | A-05 high-signal secret scan | `git grep -l -I -i -E "BEGIN ... PRIVATE KEY|client_secret|api_key|apikey|AKIA...|ghp_...|xox..." -- .` | Windows host | One source file matched an `apiKey` validation literal; no private-key/OAuth/GitHub/AWS/Slack token file hit was observed. | Pass | None | File name only |
| EV-070 | A-05 local `.env` boundary | `git ls-files`, `git check-ignore`, redacted key counting | Windows host | `.env` is ignored/untracked and contains sensitive key names; raw values were not printed. | Needs verification | Local secret provenance | Values omitted |
| EV-071 | A-05 redacted scanner availability | `gitleaks version`; `docker info` | Windows host | Local `gitleaks` is not installed and Docker daemon is unavailable, so CI-style redacted Gitleaks reproduction did not run locally. | Blocked | A-05 scanner follow-up | No report generated |
| EV-072 | A-05 config boundary | appsettings, Compose, workflow, launch settings, `.env.example` inspection plus `docker compose --env-file .env.example config --quiet` | Windows host | Committed production-like configs use placeholders or environment requirements; Compose config with `.env.example` passed. | Pass | Local `.env` caveat | No raw secrets copied |
| EV-073 | A-05 error response fix | `GlobalExceptionHandlingMiddleware` update and regression test | Windows host | Global unhandled exception responses now use a generic message in every environment; focused regression test passed. | Pass | None | Synthetic sensitive-looking text only |
| EV-074 | A-05 backend suite | `dotnet test AipPortal.slnx --configuration Release --no-build --verbosity normal --disable-build-servers -m:1` | Windows host | 129/129 passed after the A-05 error-response fix and test. | Pass | None | No raw secrets copied |
| EV-075 | A-05 log/API/UI/export runtime coverage | Source inspection plus existing A-03/A-04 evidence review | Windows host | No broad request-body logging or EF sensitive-data logging was found; live logs, authenticated UI/API/export smoke, and historical docs review remain incomplete. | Needs verification | P0-001/P0-002 plus A-05 runtime follow-up | No live private data copied |
| EV-076 | A-07 source inventory | Repo search and source inspection | Windows host | No pre-existing A-07 evidence file was found; file, attachment, artifact-version, local storage, upload policy, authorization, and test surfaces were inventoried. | Pass | A-07 | No real files copied |
| EV-077 | A-07 storage identifier fix | DTO and service response shaping | Workspace | Removed `storageKey`, `storedFileName`, and artifact-version `filePath` from file/artifact API response DTOs. | Pass | None | Internal storage identifiers not copied |
| EV-078 | A-07 private download cache headers | Controller download response update | Workspace | File, attachment, and artifact-version download actions set no-store/no-cache headers before returning file streams. | Pass | None | No file body copied |
| EV-079 | A-07 denied file access audit | `FileService` denied metadata/download audit update | Workspace | Denied file/attachment metadata and download attempts now write generic metadata-only audit entries. | Partial | A-07 audit matrix follow-up | No filename, body, storage key, token, cookie, or signed URL copied |
| EV-080 | A-07 focused tests | `dotnet test --no-build --filter "FullyQualifiedName~HttpTenantIsolationTests|FullyQualifiedName~LocalFileStorageServiceTests|FullyQualifiedName~TenantIsolationSecurityTests"` | Windows host | 32/32 passed: file metadata storage identifiers omitted, denied responses safe, download private cache header present, traversal-like upload filename sanitized, local storage traversal rejected, tenant isolation covered. | Pass | Live runtime still Needs verification | Synthetic test data only |
| EV-081 | A-07 full backend suite | `dotnet test --no-build` | Windows host | 134/134 passed after A-07 file boundary fixes and tests. | Pass | None | No real files copied |
| EV-082 | A-07 remaining file-boundary gaps | Source inspection plus test review | Windows host | Fresh-runtime authenticated file smoke, conversation attachment body matrix, removed participant, explicit grant/revoked grant, object storage/signed URL, and live PostgreSQL/container evidence remain incomplete. | Needs verification | P0-001/P0-002 plus A-07 follow-up | No real files copied |
| EV-083 | A-08 source inventory | Repo search and source inspection | Windows host | No pre-existing A-08 evidence file was found; conversation, direct-message-through-conversation, message, participant, read-state, notification, and audit/log surfaces were inventoried. No SignalR/WebSocket or dedicated conversation-thread endpoint was identified. | Pass | A-08 | No real messages copied |
| EV-084 | A-08 removed participant edit/delete fix | `ConversationAuthorizationService` update | Workspace | Message edit/delete authorization now requires active conversation visibility for authors, and delete rejects already-deleted messages. | Pass | None | Synthetic test data only |
| EV-085 | A-08 read cursor validation fix | `ConversationService.MarkReadAsync` update | Workspace | Read-state updates now reject missing, deleted, or cross-conversation `LastReadMessageId` values before storing them. | Pass | None | No private body copied |
| EV-086 | A-08 message notification body minimization | `ConversationService.SendMessageAsync` update | Workspace | New message notifications now use a generic body instead of embedding message text. | Pass | None | No message body copied into notifications |
| EV-087 | A-08 build | `dotnet build AipPortal.slnx --configuration Release --no-restore --disable-build-servers -m:1` | Windows host | Build passed with 0 warnings and 0 errors. | Pass | None | No secrets |
| EV-088 | A-08 focused communication tests | `dotnet test tests\AipPortal.Tests\AipPortal.Tests.csproj --configuration Release --no-build --filter FullyQualifiedName~HttpTenantIsolationTests --logger "console;verbosity=normal"` | Windows host | 11/11 passed: participant-scoped message body access, generic denied responses, removed-participant read/edit/delete denial, read-cursor scope validation, notification self-scope, and generic message notification body covered with synthetic data. | Pass | Live runtime still Needs verification | Synthetic messages only |
| EV-089 | A-08 full backend suite | `dotnet test AipPortal.slnx --configuration Release --no-build --verbosity normal --disable-build-servers -m:1` | Windows host | 138/138 passed after A-08 communication boundary fixes and tests. | Pass | None | No real messages copied |
| EV-090 | A-08 remaining communication-boundary gaps | Source inspection plus focused test review | Windows host | Fresh-runtime authenticated communication smoke, same-tenant DM non-participant/admin policy, thread coverage, realtime/polling coverage, live audit/log review, PostgreSQL, and container evidence remain incomplete. | Needs verification | P0-001/P0-002 plus A-08 follow-up | No real messages copied |
| EV-091 | A-09 source inventory | Repo search and source inspection | Windows host | No pre-existing A-09 evidence file was found; audit log, security event, audit query, auth security, denial logging, admin audit, file audit, communication audit, exception handling, and evidence surfaces were inventoried. | Pass | A-09 | No raw logs copied |
| EV-092 | A-09 audit query tenant-scope fix | `DbAuditQueryService` update | Workspace | Tenant-admin audit/security reads now include explicit `TenantId` predicates in addition to the authorization check and EF tenant filter. | Pass | None | No audit row bodies copied |
| EV-093 | A-09 conversation denial audit fix | `ConversationService` update | Workspace | Covered conversation/message denial paths now write generic metadata-only audit entries before returning denial results. | Pass | Remaining denial matrix incomplete | No message body copied |
| EV-094 | A-09 login security metadata minimization | `AuthService` update and regression test | Workspace | Login security metadata no longer stores raw submitted email; it records `emailProvided` and `userId` when known. | Pass | `SecurityEvent.Email` policy still Needs verification | Synthetic email only |
| EV-095 | A-09 focused auth/tenant/audit tests | `dotnet test tests\AipPortal.Tests\AipPortal.Tests.csproj --no-restore --filter "FullyQualifiedName~AuthServiceTests|FullyQualifiedName~TenantIsolationSecurityTests|FullyQualifiedName~HttpTenantIsolationTests"` | Windows host | 42/42 passed: auth metadata minimization, audit/security query tenant scoping, non-admin audit denial, platform-admin global audit reads, and conversation denial audit metadata covered with synthetic data. | Pass | Live runtime still Needs verification | Synthetic data only |
| EV-096 | A-09 full backend suite | `dotnet test tests\AipPortal.Tests\AipPortal.Tests.csproj --no-restore` | Windows host | 146/146 passed after final A-09 changes. | Pass | None | No real logs copied |
| EV-097 | A-09 sandbox restore attempt | `dotnet test tests\AipPortal.Tests\AipPortal.Tests.csproj --filter "FullyQualifiedName~TenantIsolationSecurityTests|FullyQualifiedName~HttpTenantIsolationTests"` | Sandboxed Windows host | Initial restore/test attempt was blocked because sandbox denied socket access to `api.nuget.org:443`; rerun with approved network restored packages and passed the focused slice. | Blocked | Environment permission | No secrets |
| EV-098 | A-09 remaining audit/security gaps | Source inspection plus focused test review | Windows host | Live audit/security logs, full denial matrix, admin audit matrix, file grant/revoke, destructive-operation attempts, metadata sanitizer completeness, `SecurityEvent.Email` policy, PostgreSQL, and container evidence remain incomplete. | Needs verification | P0-001/P0-002 plus A-09 follow-up | No raw logs copied |

## Evidence Notes

- `Pass` is used only where direct command/runtime/source evidence was collected.
- Authenticated admin/non-admin/tenant runtime checks are not marked Pass because no baseline login user exists.
- No screenshots were captured because curl evidence was sufficient and no private data was exposed.
- A-02 readiness is not accepted in the 2026-06-28 Windows refresh; liveness success is not production approval.
- A-03 smoke success on public shell/protected-anonymous checks is not MVP-A Go; readiness, Docker/PostgreSQL, frontend dependencies, and authenticated runtime checks remain blocked or need verification.
- A-04 automated auth/tenant/audit boundary tests passed after the test-harness Data Protection fix, but fresh-runtime authenticated admin/non-admin/wrong-tenant smoke remains Needs verification until the baseline identity/bootstrap blocker is resolved.
- A-05 fixed a source-level Development exception-message response leak, but A-05 remains Needs verification until redacted scanner artifacts, live logs, authenticated API/UI/export smoke, and historical evidence review are complete.
- A-07 fixed file/artifact API storage identifier response exposure and added private download cache headers plus denied-access audit logging, but A-07 remains Needs verification until fresh-runtime file smoke, attachment/conversation actor matrix, explicit grant/revoked grant behavior, object storage/signed URL behavior, and live runtime evidence are complete.
- A-08 fixed removed-participant message mutation, cross-conversation read cursor, and private message notification-body risks in the tested synthetic paths, but A-08 remains Needs verification until fresh-runtime communication smoke, same-tenant DM/admin policy, thread/realtime coverage, live audit/log review, and live runtime evidence are complete.
- A-09 fixed audit-query tenant scoping, covered conversation/message denial audit logging, and raw submitted-email login security metadata in the tested synthetic paths, but A-09 remains Needs verification until live audit/security logs, the full denial/admin/file/communication matrix, metadata sanitizer policy, and live runtime evidence are complete.
