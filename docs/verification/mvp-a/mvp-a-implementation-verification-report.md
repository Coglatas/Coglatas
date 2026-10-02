# MVP-A Implementation Verification Report

Verification date: 2026-06-24

## Summary

MVP-A implementation verification is not complete.

The repository builds, starts, applies EF Core migrations to PostgreSQL, exposes working health checks, protects anonymous API access, and has broad source/test evidence for authorization, tenancy, files, messaging, and audit logging.

The current P0 blocker is baseline identity/bootstrap: a fresh startup seeds a tenant and plans but no user, tenant membership, invite, or administrator. Because of that, authenticated dashboard/admin access, admin-vs-user authorization, cross-tenant runtime checks, and authenticated AuditLog verification are blocked.

2026-06-28 A-04 refresh: automated backend AuthZ boundary verification passed after fixing the auth HTTP test harness Data Protection key path. `AuthSecurityHttpTests` passed 15/15, `TenantIsolation` filtered tests passed 24/24, and the full backend suite passed 128/128. Fresh-runtime admin/non-admin/wrong-tenant smoke remains blocked by the baseline identity/bootstrap gap and is not accepted as MVP-A Go.

2026-06-28 A-05 refresh: a source-level Development error-response leakage risk was fixed by making global unhandled exception responses generic in every environment. Repo keyword scans found no confirmed committed raw secret in this pass, and the full backend suite passed 129/129. A-05 remains Needs verification because redacted Gitleaks reproduction, live logs, authenticated API/UI/export smoke, and historical evidence review are not complete.

2026-06-29 A-07 refresh: a file metadata storage identifier exposure risk was fixed by removing internal storage identifiers from file and artifact-version API response DTOs. File/attachment/artifact downloads now set private no-store headers, denied file metadata/download attempts now write metadata-only audit entries, the targeted file/storage/tenant-boundary test slice passed 32/32, and the full backend suite passed 134/134. A-07 remains Needs verification for fresh-runtime file smoke under signed-in test users, attachment/conversation sensitive-body coverage, removed-participant and explicit-grant/revoked-grant behavior, object-storage/signed-URL behavior, and live PostgreSQL/container evidence.

2026-06-29 A-08 refresh: removed-participant message mutation, cross-conversation read cursor, and private message notification-body risks were fixed in the communication service path. The focused HTTP tenant and communication-boundary test slice passed 11/11, and the full backend suite passed 138/138. A-08 remains Needs verification for fresh-runtime authenticated communication smoke, same-tenant DM non-participant/admin policy, thread coverage, realtime/polling coverage, live audit/log review, and live PostgreSQL/container evidence.

2026-06-29 A-09 refresh: audit-query tenant scoping, covered conversation/message denial audit logging, and login security metadata minimization were fixed. The focused auth/tenant/audit test slice passed 42/42, and the full backend suite passed 146/146. A-09 remains Needs verification for live audit/security log review, full denial/admin/file/communication event matrix coverage, metadata sanitizer policy, `SecurityEvent.Email` policy, and live PostgreSQL/container evidence.

## Environment

| Item | Observed value |
| --- | --- |
| OS | Ubuntu 24.04 container |
| .NET SDK | 10.0.200 |
| .NET runtime | Microsoft.NETCore.App 10.0.4, Microsoft.AspNetCore.App 10.0.4 |
| Docker | 29.3.0-1 |
| Docker Compose | v2.40.3 |
| Node.js / npm | Node v24.14.0, npm 11.9.0 |
| PostgreSQL | Docker Compose `postgres:18-alpine` from `infra/compose/dev/local.yml` |
| Important limitation | No seeded/bootstrap login user exists on fresh startup |

See [mvp-a-environment-notes.md](mvp-a-environment-notes.md).

## Repository Inventory

| Item | Evidence |
| --- | --- |
| Solution | `AipPortal.slnx` |
| Projects | `src/AipPortal.Domain`, `src/AipPortal.Application`, `src/AipPortal.Infrastructure`, `src/AipPortal.Web`, `tests/AipPortal.Tests` |
| Target framework | `net10.0` in all .NET projects inspected |
| Application entrypoint | `src/AipPortal.Web/Program.cs` |
| Startup configuration | `Program.cs`, `src/AipPortal.Web/Extensions/DependencyInjection.cs`, `src/AipPortal.Infrastructure/DependencyInjection.cs` |
| Appsettings | `appsettings.json`, `appsettings.Development.json`, `appsettings.Test.json`, on-prem/SaaS/production examples |
| Docker | `Dockerfile`, `docker-compose.yml`, `infra/compose/dev/local.yml`, `deploy/onprem/compose.yml` |
| Database config | Npgsql provider in `AipPortal.Infrastructure/DependencyInjection.cs`; connection string `DefaultConnection` |
| DbContext | `src/AipPortal.Infrastructure/Persistence/AppDbContext.cs` |
| Migrations | 12 migrations in `src/AipPortal.Infrastructure/Persistence/Migrations/` |
| Authentication | Cookie auth in `Program.cs`; auth APIs in `AuthController`; session validation in `DbSessionCookieAuthenticationEvents` |
| Authorization | Controller `[Authorize]` attributes plus service authorization classes for tenant, workspace, group, channel, project, file, conversation, forms, events, admin |
| Dashboard/UI | Angular is the MVP-A P0 frontend target under `frontend/`; hosted artifacts must include `angular-app.marker` before user-facing fallback routes are served |
| AuditLog | `AuditLog`, `SecurityEvent`, `DbAuditLogger`, `DbAuditQueryService`, service audit calls |
| File/messaging | `FilesController`, `FileService`, `FileAuthorizationService`, `ConversationsController`, `ConversationService`, `ConversationAuthorizationService` |
| Tests | `tests/AipPortal.Tests` and `tests/ui` Playwright tests |
| CI | `.github/workflows/ci.yml` |
| README startup docs | `README.md` documents restore, EF update, run, local Compose, and missing admin bootstrap |

## Verification Matrix

| Area | Status | Evidence | Blocker | Next action |
| --- | --- | --- | --- | --- |
| Build / Startup | Pass | EV-003, EV-005, EV-013 | None | Keep using `--no-launch-profile` when environment overrides matter. |
| Health check | Pass | EV-014, EV-015, EV-016 | None | Optional P1: decide whether `/healthz` or `/api/health` aliases are required. |
| Auth / Login | Partial | EV-019, EV-020, EV-021, EV-022, EV-023 | P0-001 | Add minimal approved bootstrap/seed login path for verification. |
| Tenant / User / Role | Failed | EV-023 plus source inventory | P0-001 | Seed or bootstrap first admin/user with tenant membership for MVP-A verification. |
| Authorization | Partial / Needs verification | EV-024, EV-025, EV-026, EV-055 through EV-063 plus tests/source | P0-002 for fresh-runtime smoke | Keep automated AuthZ tests green; run admin/non-admin/tenant smoke after P0-001. |
| EF Core / PostgreSQL | Pass | EV-007, EV-008, EV-009, EV-010, EV-011 | None | Keep PostgreSQL connection-string requirement documented. |
| Dashboard reachability | Partial | EV-027 | P0-001 | Verify authenticated dashboard after baseline login exists. |
| AuditLog | Needs verification | EV-028, EV-091 through EV-098 | P0-001 plus A-09 runtime and matrix follow-up | Keep auth/query/denial audit tests green; trigger live login/logout/admin/file/message denial actions after baseline login exists. |
| File / Messaging | Needs verification | EV-029, EV-076 through EV-090 | A-07 and A-08 runtime and actor-matrix follow-up | Verify runtime file/message permission checks with approved synthetic users after P0-001; expand attachment/conversation/grant, DM, thread, realtime, and audit/log matrices. |
| CI / tests | Partial | EV-006, EV-030, EV-031, EV-032, EV-033 | None P0 | Install frontend deps or rely on CI job; ensure Compose env docs are clear. |
| Sensitive data boundary | Needs verification | EV-067 through EV-075 | A-05 scanner/log/UI/export follow-up plus P0-001/P0-002 | Run redacted scanner artifacts, live sanitized logs, authenticated API/UI/export smoke, and historical evidence review. |

## P0 Blockers

See [mvp-a-p0-blockers.md](mvp-a-p0-blockers.md).

- P0-001: Fresh MVP-A startup has no usable login/admin bootstrap.
- P0-002: End-to-end authorization verification is blocked by missing baseline identity.

## P1 Follow-Up

- Add or document explicit local verification setup for frontend Playwright dependencies; local `npm test` currently fails because `playwright` is not installed.
- Decide whether `/healthz`, `/api/health`, `/ready`, or `/live` aliases are needed. Current canonical endpoints are `/health/live` and `/health/ready`.
- Document that default and on-prem Compose config require `POSTGRES_PASSWORD`; raw `docker compose config` fails without it.
- Verify AuditLog runtime coverage for authorization failures, admin actions, role changes, file operations, and message operations after P0-001.
- Verify file download authorization and messaging tenant/member boundaries with real seeded users after P0-001.
- Review messaging attachment behavior: `ConversationService` stores message attachments as metadata-only, while general file upload uses the file storage service.
- Normalize safe-denial HTTP status mapping where controller/service failures currently return `400` for authorization/not-found denials, if API-contract quality requires 403/404 instead.

## Evidence Collected

Primary evidence file: [mvp-a-evidence-log.md](mvp-a-evidence-log.md).

Key evidence IDs:

- Build/test: EV-003 through EV-006.
- PostgreSQL/migrations: EV-007 through EV-011.
- Startup/health: EV-012 through EV-018.
- Auth/login: EV-019 through EV-023.
- Authorization/dashboard: EV-024 through EV-027.
- Audit/file/messaging/CI: EV-028 through EV-033.
- A-02 through A-08 refresh evidence: EV-034 through EV-090.

## Not Verified

- Authenticated user dashboard access: blocked by no baseline user.
- Authenticated admin dashboard/API access: blocked by no baseline admin.
- Non-admin receiving 403 on admin APIs: blocked by no baseline non-admin session.
- Cross-tenant runtime denial with authenticated users: blocked by no baseline users/memberships.
- File download permission behavior at runtime: blocked by no baseline users/data.
- Message send/history runtime behavior: blocked by no baseline users/data.
- Logout behavior with an authenticated session: blocked by no baseline user.
- AuditLog rows for authenticated core admin/auth/file/message actions: blocked by no baseline user.
- Full local Angular-facing Playwright UI test run: pending new specs and frontend dependency setup; legacy static-SPA specs are obsolete.
- A-05 redacted Gitleaks local reproduction: blocked by missing local `gitleaks` and unavailable Docker daemon.
- A-05 live runtime log review and authenticated API/UI/export smoke: blocked or Needs verification until synthetic authenticated users and sanitized runtime captures are available.
- A-07 fresh-runtime authenticated file upload/download/denial smoke: blocked by no baseline user.
- A-07 attachment/conversation sensitive-body coverage, removed-participant behavior, explicit grant/revoked-grant behavior, and object-storage/signed-URL behavior: Needs verification or blocked by missing implementation/runtime.
- A-08 fresh-runtime authenticated communication smoke: blocked by no baseline user.
- A-08 same-tenant DM non-participant/admin policy, thread coverage, realtime/polling coverage, live audit/log review, PostgreSQL, and container runtime evidence: Needs verification or blocked by missing implementation/runtime.

## Scope Confirmation

- No new product scope was added.
- No Future / Deferred work was promoted into MVP-A.
- No school production readiness was claimed.
- No Cultural Festival Pilot approval was claimed.
- No raw sensitive information was committed in the evidence.
- Product code changed for the A-05 error-response hardening fix, A-07 file boundary hardening, and A-08 communication boundary hardening.
- No destructive database command was run.

## Recommended Next PRs

1. Add the smallest MVP-A verification bootstrap path for a first admin/user and tenant membership, limited to local/dev/test verification and documented startup use.
2. Add a narrow authorization smoke test suite covering anonymous, non-admin, admin, tenant boundary, and file download denial paths.
3. Add AuditLog smoke checks for login success/failure, logout, admin role/action, file upload/download/delete, and message send/delete.
4. Update local verification docs for `POSTGRES_PASSWORD`, `--no-launch-profile`, and frontend test dependency setup.
5. Add A-07 attachment/conversation/grant matrix tests after the baseline identity/bootstrap path exists.
6. Add A-08 same-tenant DM, admin policy, thread, realtime/polling, and live audit/log matrix tests after the baseline identity/bootstrap path exists.
