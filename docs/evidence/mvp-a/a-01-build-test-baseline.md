# A-01 Build/Test/Evidence Baseline

Issue: A-01 - [MVP-A][P0][Baseline] Establish reproducible build/test/evidence baseline

Date: 2026-06-28

Branch: `main`

Commit: `8a3e52268437824b3b4d12cb729f87f570b777aa`

Result: Blocked

This baseline does not imply production approval or MVP-A Go.

## A-01 Definition

No repo-owned A-01 definition was found before this evidence file was added. The working definition came from the attached issue text supplied for this task: establish a reproducible build/test/Docker/database evidence baseline, record failures honestly, and avoid copying secrets or personal data into evidence.

## Environment

| Item | Observed value |
| --- | --- |
| OS | Windows 10.0.26200, win-x64 |
| .NET SDK | 10.0.301 |
| .NET host/runtime | 10.0.9 |
| global.json | Present, pins SDK 10.0.301 |
| .NET tool manifest | `.config/dotnet-tools.json` and `dotnet-tools.json`, `dotnet-ef` 10.0.8 |
| Docker client | 29.5.3 |
| Docker context | `desktop-linux` |
| Docker Compose | v5.1.4 |
| Node.js | v26.4.0 |
| npm | 11.17.0 through `npm.cmd`; `npm.ps1` blocked by PowerShell execution policy |
| `POSTGRES_TEST_CONNECTION_STRING` | Not set |

## Repo Structure Summary

| Area | Observed path |
| --- | --- |
| Solution | `AipPortal.slnx` |
| Source projects | `src/AipPortal.Domain`, `src/AipPortal.Application`, `src/AipPortal.Infrastructure`, `src/AipPortal.Web` |
| Backend test project | `tests/AipPortal.Tests/AipPortal.Tests.csproj` |
| UI tests | `tests/ui`, root `package.json`, `playwright.config.ts` |
| Docker | `Dockerfile`, `docker-compose.yml`, `infra/compose/dev/local.yml`, `deploy/onprem/compose.yml`, `.dockerignore` |
| Configuration | `src/AipPortal.Web/appsettings*.json`, `src/AipPortal.Web/Properties/launchSettings.json`, `.env.example` |
| CI | `.github/workflows/ci.yml` |
| Migrations | `src/AipPortal.Infrastructure/Persistence/Migrations` |
| Existing MVP-A verification | `docs/verification/mvp-a` |

Main build/test target selected: `AipPortal.slnx`. Selection reason: it is the only solution file and includes the backend test project.

## Commands Executed

| Area | Command | Result |
| --- | --- | --- |
| Repo search | `rg -n "A-01|MVP-A|baseline|build|test|evidence|exit gate|blocker" .` | No pre-existing repo-owned A-01 definition identified; existing MVP-A evidence docs found. |
| Environment | `dotnet --info` | Passed. SDK 10.0.301 is installed and matches `global.json`. |
| Restore | `dotnet restore AipPortal.slnx --disable-build-servers` | Passed; all projects up-to-date. |
| Tool restore | `dotnet tool restore` | Passed; restored `dotnet-ef` 10.0.8. A newer 10.0.9 tool was reported, but the manifest was not changed. |
| Build | `dotnet build AipPortal.slnx --configuration Release --no-restore --disable-build-servers -m:1` | Passed; 0 warnings, 0 errors, elapsed 00:01:47.70. |
| Test | `dotnet test AipPortal.slnx --configuration Release --no-build --verbosity normal --disable-build-servers -m:1` | Passed by runner; 128 total, 128 passed, 0 failed, elapsed 00:00:23.04. |
| Docker version | `docker --version` | Passed; client 29.5.3. Initial non-escalated check warned that Docker config was inaccessible. |
| Docker Compose version | `docker compose version` | Passed; v5.1.4. |
| Docker Compose config | `docker compose --env-file .env.example config --quiet` | Passed. Evidence does not copy `.env.example` values. |
| Docker daemon | `docker info` | Failed; Docker client is installed, but the `desktop-linux` server endpoint was unavailable. |
| Docker build | `docker compose --env-file .env.example build app` | Failed before app build because Docker daemon was unavailable. |
| Docker up | `docker compose --env-file .env.example up -d --wait` | Failed before startup because Docker daemon was unavailable. |
| EF migrations | `dotnet ef migrations list --project src\AipPortal.Infrastructure --startup-project src\AipPortal.Web --no-connect` | Passed; listed 12 migrations without connecting to a database. Applied/pending status was not verified. |
| UI Node | `node --version` | Passed; v26.4.0. |
| UI npm via PowerShell | `npm --version`; `npm test -- --reporter=list` | Failed because PowerShell blocks `npm.ps1` under the current execution policy. |
| UI npm via cmd shim | `npm.cmd --version` | Passed; 11.17.0. |
| UI test | `npm.cmd test -- --reporter=list` | Failed because `playwright` is not present in `node_modules`. |

## Test Summary

| Test surface | Total | Passed | Failed | Skipped | Not run / caveat |
| --- | ---: | ---: | ---: | ---: | --- |
| `dotnet test AipPortal.slnx` | 128 | 128 | 0 | 0 reported | `POSTGRES_TEST_CONNECTION_STRING` was not set; source inspection shows the two PostgreSQL integration tests return early when that variable is absent. |
| Root Playwright UI tests | 0 | 0 | 0 | 0 | Blocked locally because `playwright` is not installed in `node_modules`. |

## Docker/Container Summary

Docker Compose configuration is syntactically valid with `.env.example`, but Docker build and startup were not verified because the Docker Desktop Linux engine was not reachable:

```text
failed to connect to the docker API at npipe:////./pipe/dockerDesktopLinuxEngine
```

No app, migration, or PostgreSQL container reached runtime verification during this A-01 pass.

## Database/Migration Summary

EF tooling restored successfully. `dotnet ef migrations list --no-connect` listed 12 migrations:

- `20260606135558_InitialCreate`
- `20260606140904_AuthUserInviteUpdates`
- `20260606142553_OrganizationAndChannels`
- `20260606145345_MessagingAndProductionTracking`
- `20260607003116_EventsAttendanceCalendarBasics`
- `20260607005101_FormsSurveysApplications`
- `20260607011954_AdminSettingsAndHardeningIndexes`
- `20260607050801_MultiTenantFoundation`
- `20260607122744_PerformanceIndexesAndPagedPlanning`
- `20260608130000_SoftDeleteMetadataColumns`
- `20260608131000_PlansTableForStartupSeed`
- `20260610154740_AuthSessionSecurityHardening`

Applied/pending status and real PostgreSQL assertions remain unverified in this local pass because no database connection string was provided and Docker was unavailable.

## External Dependency Summary

- NuGet restore and local .NET tool restore succeeded.
- Docker daemon access is blocked on this host.
- Root UI test dependencies are not installed in `node_modules`.
- CI is configured to run PostgreSQL service tests, UI dependency install, Playwright browser install, Compose config validation, Docker image build, Gitleaks, dependency scan, and Trivy image scan.

## Limitations

- Docker image build, Compose startup, PostgreSQL container health, app container health, and `/health/ready` were not verified in this A-01 pass.
- Real PostgreSQL integration assertions were not executed by the local `dotnet test` run because `POSTGRES_TEST_CONNECTION_STRING` was not set.
- UI tests were not executed because Playwright is missing locally.
- This evidence records command outcomes only; it does not approve production readiness, MVP-A Go, or acceptance of unrelated P0/P1 blockers.

## Required Follow-Up

1. Start or repair Docker Desktop Linux engine, then rerun Docker build/startup evidence:
   `docker compose --env-file .env.example build app`
   `docker compose --env-file .env.example up -d --wait`
2. Run a non-production PostgreSQL baseline with a sanitized local connection string and `POSTGRES_TEST_CONNECTION_STRING` set.
3. Install locked UI test dependencies with `npm.cmd ci`, then rerun `npm.cmd test`.
4. If Docker startup succeeds, capture app and PostgreSQL container health plus `/health/ready` without copying secrets.
