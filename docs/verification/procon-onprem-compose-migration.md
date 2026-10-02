# Procon on-prem Compose migration verification

Issue: #465

Status: migration service verified locally; full production-profile app startup
still requires the licensed frontend build secret and the intended TLS proxy.

## Scope

`deploy/onprem/compose.yml` now defines a one-shot SDK `migrate` service. It
waits for PostgreSQL health, restores the existing toolchain, and runs the
normal EF Core update command. The app uses Compose
`service_completed_successfully` dependency gating, so it is not started after
a migration failure. The application process itself still does not auto-migrate.

## Local verification

On 2026-08-30, Docker Desktop 29.7.2 and Docker Compose 5.4.0 ran an isolated
project with a new PostgreSQL 18 named volume:

```powershell
$env:DB_PASSWORD = '<disposable local validation password>'
docker compose -p aipsite-onprem-verify -f deploy/onprem/compose.yml `
  up --abort-on-container-exit --exit-code-from migrate postgres migrate
docker compose -p aipsite-onprem-verify -f deploy/onprem/compose.yml `
  down --volumes --remove-orphans
```

The migration container restored the pinned `dotnet-ef` tool, built the Web
startup project, and applied the schema through
`20260829173340_AddMessageFollowUps` with exit code zero. The named database
and NuGet-cache volumes and network were then removed under the same isolated
Compose project.

The Compose configuration was also inspected as JSON: the app depends on both
a healthy PostgreSQL service and successful `migrate` completion, while the
migration service itself depends on healthy PostgreSQL and has restart policy
`no`.

The CI workflow repeats this migration-only clean-volume check with an
isolated Compose project. Its CI-only override places the local containers on
Docker's built-in bridge and makes `migrate` share PostgreSQL's network
namespace, using loopback rather than a per-project network on the self-hosted
runner. It intentionally starts only PostgreSQL and `migrate`, so it does not
require the production Angular build secret. The on-prem deployment command
does not use that override.

The CI-only command starts no application container and uses a dummy database.
Its normal cleanup trap removes project-scoped containers and volumes. A
force-cancelled job can still leave those resources behind, but it no longer
leaks a per-project Docker subnet.

## Remaining limits

- The production app image was not built or started in this verification. Its
  Angular release build correctly requires the external build-only Syncfusion
  license secret; no substitute credential was used.
- The profile does not bundle a TLS proxy. Secure cookies, HTTPS redirect,
  HSTS, and forwarded-header behavior must be verified through the intended
  deployed proxy topology.
- This run proves fresh-database migration and Compose dependency wiring, not
  authentication, CSRF, user provisioning, or the Task execution Golden Path.
