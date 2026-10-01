# Development

Last verified: 2026-06-18.

## Prerequisites

- .NET 10 SDK.
- PostgreSQL supported by the current Npgsql package, either host-native or through `infra/compose/dev/db.yml`.
- Node.js 24.15+ for the Angular workspace and CI-equivalent UI test workflow.
- Docker/Compose for the recommended PostgreSQL-only mode, optional full-container development, and Linux Playwright parity.

The repository pins `dotnet-ef` 10.0.8 in both `dotnet-tools.json` and `.config/dotnet-tools.json`.

## Restore and build

```bash
dotnet restore Coglatas.slnx
dotnet tool restore
dotnet build Coglatas.slnx
```

If an execution sandbox blocks MSBuild named pipes, use a single worker and disabled build servers:

```bash
dotnet build Coglatas.slnx --disable-build-servers -m:1
```

## Database setup

Recommended default: start PostgreSQL only in Docker.

```bash
docker compose -f infra/compose/dev/db.yml up -d
```

`src/Coglatas.Web/appsettings.Development.json` points to that container on
`localhost:5433` with safe development-only credentials. If you want a
different local PostgreSQL instance, override
`ConnectionStrings__DefaultConnection` before applying migrations or starting
the app.

Apply migrations:

```bash
dotnet ef database update \
  --project src/Coglatas.Infrastructure \
  --startup-project src/Coglatas.Web
```

The application does not automatically apply migrations.

## Run the application

```bash
ASPNETCORE_ENVIRONMENT=Development \
dotnet run --project src/Coglatas.Web
```

Development defaults use:

- `HeaderForDevelopmentOnly` tenant resolution;
- `X-Tenant-Slug` when explicitly allowed;
- non-secure local cookies;
- no HTTPS redirect or HSTS;
- CSRF enabled;
- rate limiting and lockout disabled;
- `PlatformAdminSetupMode=true`.

Important: setup mode does not create an administrator. Use the explicit seed variables below, or the development-only `LocalAdmin:*` compatibility path, when a local administrator is required.

Run the Angular dev server on the host:

```bash
cd frontend
npm ci
npm run start
```

## Syncfusion licensed builds

The default `npm run build` does not activate Syncfusion and remains suitable
for the current fallback-only frontend. Before building an artifact that
contains approved Syncfusion components, put the vendor-issued value in the
Git-ignored `.env` (or set it as an OS environment variable), then export it
only for the build command and run:

```powershell
Set-Location frontend
npm run build:licensed
```

`npm run build:licensed` fails before the Angular build when
`SYNCFUSION_LICENSE` is missing, empty, or whitespace-only. It does not print
the value. See [the Syncfusion license runbook](SYNCFUSION_LICENSE_RUNBOOK.md)
for safe PowerShell, POSIX shell, and Compose procedures.

`frontend/proxy.conf.json` targets the backend at `http://localhost:5098`,
which matches `src/Coglatas.Web/Properties/launchSettings.json`.

## Angular hosted by ASP.NET Core

Angular source lives under `frontend/`. The ASP.NET Core app serves built
Angular artifacts from `src/Coglatas.Web/wwwroot`; do not place Angular source
files in `wwwroot`.

Build and copy the Angular app into the ASP.NET Core static root:

```bash
cd frontend
npm ci
npm run build:hosted
cd ..
dotnet run --project src/Coglatas.Web
```

`npm run build` writes to `frontend/dist/coglatas-web`. `npm run build:hosted`
copies those artifacts into `src/Coglatas.Web/wwwroot` and replaces the legacy
static SPA entrypoint. The Angular build emits `angular-app.marker`; without
that marker, ASP.NET Core does not use `wwwroot/index.html` as the user-facing
fallback.

To have `dotnet publish` run the Angular build on a machine with Node.js
available, configure `SYNCFUSION_LICENSE` in the invoking environment first.
The publish target uses `build:hosted:licensed` and fails closed when it is
missing:

```bash
dotnet publish src/Coglatas.Web/Coglatas.Web.csproj -c Release -p:BuildAngularFrontendOnPublish=true
```

Angular owns user-facing non-API routes such as `/login`, `/register/invite`,
`/workspaces`, `/projects`, `/conversations`, `/admin`, `/account`, `/files`,
and `/notifications` after the hosted build is present. Backend-owned routes do
not fall back to Angular: `/api/*`, `/health`, `/health/live`,
`/health/ready`, `/healthz`, `/metrics`, `/swagger/*`, `/hangfire/*`,
`/signin-google`, `/auth/callback/*`, and `/favicon.ico`.

## Optional full Docker development

```bash
cp .env.example .env
docker compose -f infra/compose/dev/full.yml up --build
```

This optional profile:

- starts PostgreSQL;
- starts the backend and frontend in containers;
- uses the same safe development-only PostgreSQL defaults as the lightweight mode;
- uses polling-based file watching for Windows Docker Desktop compatibility.

It is not the default contributor path and can be slower on Windows Docker Desktop because the backend and frontend both watch bind-mounted files.

See [README.dev-env.md](../README.dev-env.md) for the lightweight mode, the optional full-container mode, and the Linux Playwright parity runner.

## Seed behavior

`AppDbContextSeed` can create:

- one default tenant;
- four plan records;
- an explicit initial administrator when `COGLATAS_SEED_ADMIN_ENABLED=true`;
- a development-only local administrator when `LocalAdmin:SeedOnStartup=true` in Development;
- optional UI-shell modules, panels, commands, and radial profiles.
- deterministic synthetic browser-smoke data only in the `Test` environment
  with an explicit browser-smoke seed opt-in. The U-22 demo uses
  `COGLATAS_BROWSER_SMOKE_SEED_ENABLED=true`; the host also supports
  `BrowserSmokeSeed:Enabled=true`. The fixture includes a test user,
  workspace, announcement, Projects, Tasks, and required memberships, plus one
  U-22-specific synthetic Project/Task only for the loopback Test-demo flow
  documented in `docs/u22/demo-data.md`.

The administrator seed uses the existing password hasher and creates or updates a platform administrator with owner membership in the default tenant. `COGLATAS_SEED_ADMIN_USERNAME` is stored as the display name because the current user model uses email for login and has no username column.

The legacy `LocalAdmin:*` compatibility path is separate from the explicit `COGLATAS_SEED_ADMIN_*` bootstrap. Keep `LOCAL_ADMIN_SEED_ON_STARTUP=false` unless you intentionally want that development-only behavior.

Without the explicit browser-smoke flag, it does not create workspaces, groups,
channels, projects, demo data, or invite links.

The U-22 test-only credentials and fixture boundary are documented in
`docs/u22/demo-data.md`. Do not use them outside that explicit Test-only flow.

## Tests

Run .NET tests:

```bash
dotnet test Coglatas.slnx
```

Run PostgreSQL-backed assertions by supplying a migrated test database:

```bash
export POSTGRES_TEST_CONNECTION_STRING='Host=localhost;Port=5432;Database=coglatas_test;Username=coglatas;Password=<test-password>'
dotnet test Coglatas.slnx --filter 'Category=PostgreSQLIntegration'
```

Without that variable, the current PostgreSQL tests return early and are reported as passed.

Run UI tests:

```bash
npm ci
npx playwright install chromium
npm run test:ui
```

`npm run test:ui` is the static Angular suite: it serves the Angular build and
mocks APIs. It does not run the ASP.NET Core backend.

Run the isolated real-backend browser smoke:

```powershell
npm.cmd run test:ui:real-backend
```

This starts PostgreSQL, EF Core migrations, ASP.NET Core with the hosted
production Angular build, deterministic synthetic seed data, and Playwright in
one isolated Compose project. It uses the non-HSTS Compose alias
`http://coglatas-backend:8080` within that network.
For a manual run against an already-started backend, set
`COGLATAS_REAL_BACKEND_SMOKE=1`, `PLAYWRIGHT_BASE_URL`,
`COGLATAS_BROWSER_SMOKE_EMAIL`, and `COGLATAS_BROWSER_SMOKE_PASSWORD`, then run
`node tests/ui/run-real-backend-playwright.mjs`. Local `dotnet run` normally
uses port 5098; the Compose app uses port 8080.

The separate post-deployment public HTTPS gate is documented in
`docs/verification/p0-public-https-golden-path.md`. It requires a protected,
dedicated synthetic fixture and never substitutes a direct Kestrel or localhost
run for the real TLS/proxy route.

See `docs/TESTING.md`.

## Adding a feature

Follow the existing slice:

1. Domain entity/enums if persistent state is needed.
2. Application DTOs, interfaces, service, and resource authorization.
3. Repository contract and infrastructure implementation.
4. EF configuration and migration.
5. Thin Web controller.
6. Unit/service and HTTP tests.
7. Browser UI only when the feature is intended to be user-accessible there.
8. Update the implementation status in `docs/AI_CONTEXT.md` and open issues in `docs/KNOWN_ISSUES.md`.

Do not call a backend-only feature complete when the intended workflow requires the bundled UI.

## Configuration audit rule

Before adding documentation for a setting, search for both:

- where the setting is bound; and
- where its value is read to change behavior.

`FeatureOptions` and most of `PlatformOptions` are examples of settings that are bound but not currently enforced.

## Documentation maintenance

- Active truth belongs under `docs/`.
- Historical snapshots belong under `docs/archive/`.
- Do not update an archived status report to make it look current; update the active docs and annotate the archive index.
- Use exact repository paths for implementation claims.
