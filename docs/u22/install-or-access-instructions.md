# U-22 install or access instructions

These instructions separate ordinary contributor setup from the isolated,
test-only demo environment. Neither path is a production deployment guide.
For deployment controls, use the active security and deployment documentation.

## Prerequisites

- .NET 10 SDK and the repository-pinned `dotnet-ef` tool.
- Node.js 24.15 or newer and npm compatible with the repository manifests.
- Docker and Docker Compose for PostgreSQL, Linux browser parity, or the
  isolated real-backend/demo stacks.
- A disposable PostgreSQL connection for provider-backed verification.
- A valid local Syncfusion build license only when invoking a licensed build or
  the Compose stack that builds one. Keep it in an environment variable or the
  approved secret path; never commit it.

## Contributor setup

From the repository root, restore the backend and start the development
PostgreSQL profile:

```powershell
dotnet restore Coglatas.slnx
dotnet tool restore
docker compose -f infra/compose/dev/db.yml up -d
dotnet ef database update `
  --project src/Coglatas.Infrastructure `
  --startup-project src/Coglatas.Web
```

Run the backend in one terminal:

```powershell
dotnet run --project src/Coglatas.Web
```

Run the Angular development server in another terminal:

```powershell
Set-Location frontend
npm ci
npm run start
```

For hosted Angular output used by ASP.NET Core, build from source rather than
editing `src/Coglatas.Web/wwwroot` directly:

```powershell
npm --prefix frontend run build:hosted
dotnet run --project src/Coglatas.Web
```

Local administrator bootstrap and development seed options are documented in
[docs/DEVELOPMENT.md](../DEVELOPMENT.md). Do not enable an administrator or
seed option in a shared environment merely to prepare a demo.

## Test-only U-22 demo access

The U-22 demo is valid only when the chosen submission baseline includes both
the test-only fixture and its loopback Compose overlay. Follow
[demo-data.md](demo-data.md) as the authoritative source for its exact
commands, test-only account, port, fixture contents, and teardown.

Before starting that stack:

1. Confirm the current shell has a valid `SYNCFUSION_LICENSE` without printing
   its value.
2. Confirm the selected source tree contains `infra/compose/demo/u22.yml` and
   [demo-data.md](demo-data.md).
3. Validate the composed files before running containers.
4. Use only the loopback endpoint supplied by the overlay.
5. Tear down only the named isolated Compose project after the rehearsal.

The overlay's deterministic data is restricted to the Test environment plus
explicit browser-smoke seed opt-in. It must not be started as a shared,
deployed, or production stack. Its source-policy values demonstrate stored
policy state only, not a Web retrieval, file read, provider invocation, or
runtime output.

## Verification access

Use the narrowest check first, then broaden to final evidence:

```powershell
dotnet test Coglatas.slnx
npm --prefix frontend test
npm --prefix frontend run build
npm run test:ui
```

`npm run test:ui` uses mocked browser APIs and is not a real-backend proof.
For the real backend, use the repository's real-backend runner or the required
CI workflow with its protected license configuration. A locally absent
`POSTGRES_TEST_CONNECTION_STRING` causes conditional provider tests to skip;
such a result is not PostgreSQL evidence.

At release freeze, use the final evidence table in
[submission-checklist.md](submission-checklist.md), including required
real-backend P0, PostgreSQL/migration, authorization, and U-22 journey checks.

## Safe handoff information

Give a reviewer the final tag or protected branch, immutable SHA, date, and
the verification links recorded in the checklist. Do not send passwords,
CSRF tokens, Syncfusion licenses, connection strings, or test credentials in
issue comments, screenshots, or submission material.
