# .NET SDK 10.0.302 Upgrade Verification

## Status

**Implementation updated; hosted verification pending.**

This document does not claim a passing result until the exact PR head completes the required local or hosted checks.

## Scope

| Item | Previous | Approved value | Change in this PR |
|---|---:|---:|---|
| .NET SDK | `10.0.301` | `10.0.302` | Yes |
| Target Framework | `net10.0` | `net10.0` | No |
| .NET Runtime / ASP.NET Core Runtime | floating `10.0` Docker tag | `10.0.10` | Runtime Docker image pinned |
| EF Core packages | `10.0.10` | `10.0.10` | No |
| `dotnet-ef` | `10.0.10` | `10.0.10` | No |
| Npgsql EF Core Provider | `10.0.3` | `10.0.3` | No |
| PostgreSQL schema | existing migrations | unchanged | No migration expected |
| Angular / npm dependencies | existing versions | unchanged | No |

## Files changed

- `global.json`
- `Dockerfile`
- `infra/docker/backend-dev.Dockerfile`
- `docker-compose.yml`
- `infra/compose/dev/local.yml`
- `infra/compose/test/real-backend-smoke.yml`
- this verification record

## Configuration contract

The repository root `global.json` selects:

```json
{
  "sdk": {
    "version": "10.0.302",
    "rollForward": "disable",
    "allowPrerelease": false
  }
}
```

Authoritative local and CI evidence must therefore report exactly `10.0.302` from `dotnet --version`. Preview SDKs and SDK roll-forward are not accepted.

Docker contracts:

- build and development SDK image: `mcr.microsoft.com/dotnet/sdk:10.0.302`
- application runtime image: `mcr.microsoft.com/dotnet/aspnet:10.0.10`
- migration services: `mcr.microsoft.com/dotnet/sdk:10.0.302`

## Required verification

### Toolchain identity

```bash
dotnet --version
dotnet --info
dotnet --list-sdks
dotnet --list-runtimes
dotnet tool restore
dotnet ef --version
```

Expected:

- SDK: `10.0.302`
- Target Framework: `net10.0`
- .NET Runtime / ASP.NET Core Runtime: `10.0.10`
- `dotnet-ef`: `10.0.10`

### Restore, build, and test

```bash
dotnet restore AipPortal.slnx --disable-parallel --verbosity normal
dotnet build AipPortal.slnx --configuration Release --no-restore --disable-build-servers -m:1
dotnet test AipPortal.slnx --configuration Release --no-build --disable-build-servers -m:1 --verbosity normal
```

### EF Core and PostgreSQL

```bash
dotnet ef migrations has-pending-model-changes \
  --project src/AipPortal.Infrastructure \
  --startup-project src/AipPortal.Web \
  --configuration Release

dotnet ef database update \
  --project src/AipPortal.Infrastructure \
  --startup-project src/AipPortal.Web \
  --configuration Release
```

Expected:

- no unexpected pending model changes
- no SDK-only migration
- all existing migrations apply successfully to a synthetic test database

### Publish and container checks

```bash
dotnet publish src/AipPortal.Web/AipPortal.Web.csproj \
  --configuration Release \
  --no-restore \
  --output artifacts/dotnet-10.0.302-publish

docker compose config --quiet
docker compose -f infra/compose/dev/local.yml config --quiet
docker compose -f infra/compose/test/real-backend-smoke.yml config --quiet
docker build --pull --file infra/docker/backend-dev.Dockerfile --tag aipsite-backend:dotnet-10.0.302 .
docker run --rm --entrypoint dotnet aipsite-backend:dotnet-10.0.302 --info
```

The integrated root `Dockerfile` build must also complete through the existing licensed frontend build path without exposing Syncfusion license material.

### Dependency and security reports

```bash
dotnet list AipPortal.slnx package --vulnerable --include-transitive
dotnet list AipPortal.slnx package --deprecated
```

## Acceptance gate

The PR remains **No-Go** until exact-head evidence confirms:

- valid `global.json`
- SDK `10.0.302` is installed and selected without roll-forward
- restore and Release build pass
- full backend tests pass
- EF Core reports no unexpected model change
- existing PostgreSQL migrations apply
- publish and Docker builds pass
- application startup and health checks pass
- vulnerable package and container scans do not introduce a new blocker

Historical evidence produced with SDK `10.0.301` remains historical and must not be rewritten as `10.0.302` evidence.

## Specification synchronization

The corresponding toolchain and evidence requirements are proposed in `NYGsatoshi/AIPsiteNYGspec` PR #60.
