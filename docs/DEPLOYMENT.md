# Deployment

Last implementation audit: 2026-06-18.

This document describes what the repository currently supports. It is not a production certification.

## Deployment readiness

| Profile | Status | Current limitation |
| --- | --- | --- |
| Direct local development | Partially implemented | Requires external PostgreSQL and migrations; initial administrator seed is opt-in |
| `infra/compose/dev/local.yml` | Partially implemented | Migrates and can opt in to initial administrator seed |
| `docker-compose.yml` | Partially implemented | Migrates and can opt in to initial administrator seed |
| `deploy/onprem/compose.yml` | Partially implemented | Runs controlled migrations and binds the app origin to loopback by default; an operator-provided TLS proxy with an explicit forwarded-header trust boundary is required for public use and still needs target-host evidence |
| `deploy/sakura/docker-compose.yml` | Implemented for the current Sakura VPS topology | Requires owner-only external environment and Syncfusion license files |
| Broad public SaaS | Not ready | Object storage, bootstrap, API-token auth, recovery evidence, and deployment hardening are incomplete |

## Container files

### `Dockerfile`

- Builds and publishes `Coglatas.Web`.
- Builds the Angular frontend in a Node.js stage and copies the browser artifacts into `src/Coglatas.Web/wwwroot` before publishing `Coglatas.Web`.
- Installs `curl` for health checks.
- Listens on HTTP port 8080.
- Does not apply migrations.

### `infra/compose/dev/local.yml`

- PostgreSQL 18.
- Separate SDK migration service.
- Development app profile.
- `OnPremSingleTenant` with startup tenant seed.
- Local filesystem uploads in a named volume.
- No persistent Data Protection volume in this profile.
- Initial administrator seed is available through `COGLATAS_SEED_ADMIN_ENABLED`.

### `docker-compose.yml`

- PostgreSQL 18.
- Separate SDK migration service.
- Production app profile.
- Local filesystem uploads despite the base app mode being SaaS.
- Persistent Data Protection keys and uploads.
- Initial administrator seed is available through `COGLATAS_SEED_ADMIN_ENABLED`; it is disabled by default.
- The development-only `LocalAdmin:*` compatibility seed is not enabled by default in this profile.

### `deploy/onprem/compose.yml`

- PostgreSQL, a controlled one-shot SDK migration service, and the app.
- The app waits for a successful migration service completion; the application process still does not auto-migrate.
- `Tenancy:SeedOnStartup=true` begins only after the schema migration has completed.
- Production HTTPS redirect and secure-cookie settings are enabled.
- No reverse-proxy/TLS service is included: the supported public topology is an
  operator-provided external TLS proxy terminating before the Compose origin.
- The application port binds to `127.0.0.1` by default, not all host
  interfaces. `COGLATAS_BIND_ADDRESS` may be changed only for a documented
  private, trusted-network topology.
- Forwarded headers are disabled by default. An operator enabling proxy mode
  must also supply at least one trusted proxy IP or CIDR or startup fails
  closed.

### `deploy/sakura/docker-compose.yml`

- Builds from an explicit clean source worktree and preserves the existing
  `deploy` Compose project and named volumes.
- Supplies `/srv/coglatas/app/secrets/syncfusion-license.txt` as the
  `syncfusion_license` BuildKit secret only during the frontend build.
- Runs migrations before recreating the web service.
- Uses Caddy for the public route and checks ASP.NET Core readiness with the
  trusted forwarded-protocol headers used by that topology.
- Is deployed through `deploy/sakura/deploy.sh`, which rejects dirty source
  worktrees and group/other-readable secret or environment files.

For a fresh on-prem deployment, configure the external TLS proxy boundary,
then start the Compose profile normally; its one-shot `migrate` service applies
the schema before the app is allowed to start. A failed migration keeps the app
from starting successfully. This is not a replacement for a fresh-stack
deployment verification with the intended TLS proxy, credentials, and build
secret.

The current migration-only verification record is
`docs/verification/procon-onprem-compose-migration.md`. The canonical external
TLS-proxy contract and operator verification procedure are in
`docs/verification/procon-onprem-reverse-proxy-topology.md`.

## Configuration sources

Effective configuration is composed from:

- `src/Coglatas.Web/appsettings.json`;
- environment-specific `appsettings.*.json`;
- environment variables;
- command-line configuration.

The `*.example.json` files are examples only. ASP.NET Core does not automatically load `appsettings.SaaS.example.json`, `appsettings.OnPremSingleTenant.example.json`, or similar files.

Values such as `${DB_HOST}` inside JSON are not shell-expanded by ASP.NET Core. Copying an example file without replacing placeholders will not produce a working deployment.

## Enforced configuration

Startup validation checks:

- tenancy enum values and development-header policy;
- file provider names and required provider fields;
- upload size, extensions, and MIME types;
- CSRF service/middleware registration;
- production secure cookies, HTTPS, HSTS, persisted Data Protection keys, and disabled setup mode;
- production database password presence and basic placeholder/strength heuristics;
- object-storage secret presence when object storage is selected.

Source: `src/Coglatas.Web/Configuration/StartupConfigurationValidator.cs`.

## Bound but not enforced settings

The following settings should not be treated as runtime gates:

- `Features:EnableRadialMenu`
- `Features:EnableDockingLayout`
- `Features:EnableForms`
- `Features:EnableEvents`
- `Features:EnableProductionTracking`
- `Features:EnableWebhooks`
- `Features:EnableApiTokens`
- `Platform:EnablePlatformAdmin`
- `Platform:AllowTenantCreationFromAdmin`
- `Platform:EnablePlansAndSubscriptions`
- `Platform:EnableUsageQuota`

`Platform:PlatformAdminSetupMode` is read only by production startup validation. It does not implement bootstrap.

Tenant feature enforcement uses database plan/settings data through `FeatureFlagService`, not the `Features:*` section.

## Database migrations

Apply migrations before app startup:

```bash
dotnet tool restore
dotnet ef database update \
  --project src/Coglatas.Infrastructure \
  --startup-project src/Coglatas.Web
```

Generate a review script:

```bash
dotnet ef migrations script \
  --project src/Coglatas.Infrastructure \
  --startup-project src/Coglatas.Web
```

The app checks for pending migrations in `/health/ready` but does not apply them.

For container deployments, the application reads the PostgreSQL connection string from `ConnectionStrings__DefaultConnection`. The production Compose files in this repository assemble that value from `DB_HOST`, `DB_NAME`, `DB_USER`, and `DB_PASSWORD`, and they provision the PostgreSQL container with the same `DB_NAME` / `DB_USER` / `DB_PASSWORD` values to keep the migration and web containers aligned.

## Frontend static hosting

Production static hosting serves Angular build artifacts from
`src/Coglatas.Web/wwwroot`. The Angular source of truth remains under
`frontend/`; the `wwwroot` files are build output for hosting.

Local hosted build:

```bash
cd frontend
npm ci
npm run build:hosted
cd ..
dotnet run --project src/Coglatas.Web
```

Publish build with Node.js available (with `SYNCFUSION_LICENSE` configured in
the invoking environment):

```bash
dotnet publish src/Coglatas.Web/Coglatas.Web.csproj -c Release -p:BuildAngularFrontendOnPublish=true
```

The Dockerfile uses a separate Node.js stage and copies
`frontend/dist/coglatas-web` into `src/Coglatas.Web/wwwroot` before
`dotnet publish`.

### Syncfusion license activation

The release Docker build requires `SYNCFUSION_LICENSE` as a BuildKit secret.
The value is mounted only while the frontend build stage runs
`npm run syncfusion:activate`; it is not a Docker `ARG`, image `ENV`, runtime
container environment variable, or browser configuration value. The supported
Compose profiles map the secret from the Git-ignored `.env` file. See
[the Syncfusion license runbook](SYNCFUSION_LICENSE_RUNBOOK.md) before a
licensed release build.

On the Sakura VPS, the secret source is a protected file rather than a raw
Compose environment value. Use `deploy/sakura/deploy.sh`; do not copy the
license file into the repository, frontend directory, Docker build context, or
runtime service environment.

To verify a production image contains the current Angular Projects and My Tasks
bundle, rebuild the image and inspect the served client bundle from the running
container rather than relying on a local `dist` directory:

```bash
docker compose build app
docker compose up -d app
curl -fsS http://localhost:${COGLATAS_PORT:-8080}/app/projects | grep -q '<app-root'
curl -fsS http://localhost:${COGLATAS_PORT:-8080}/app/tasks | grep -q '<app-root'
docker compose exec app sh -lc "grep -R \"My Tasks\" /app/wwwroot/browser /app/wwwroot 2>/dev/null | head"
```

Angular fallback is limited to safe user-facing GET routes after the Angular
build marker is present. `/api/*` returns backend API 404 behavior and never
serves Angular `index.html`. Backend-owned routes remain outside Angular
fallback: `/health`, `/health/live`, `/health/ready`, `/healthz`, `/metrics`,
`/swagger/*`, `/hangfire/*`, `/signin-google`, `/auth/callback/*`, and
`/favicon.ico`.

## Tenant and administrator bootstrap

Implemented startup seed can create a default tenant, plans, and an explicit initial administrator.

Set the following environment variables only when intentionally bootstrapping or reconciling the first administrator:

```bash
COGLATAS_SEED_ADMIN_ENABLED=true
COGLATAS_SEED_ADMIN_EMAIL=admin@example.local
COGLATAS_SEED_ADMIN_USERNAME=admin
COGLATAS_SEED_ADMIN_PASSWORD=<strong-password>
```

The seed uses the existing `IPasswordHasher` and `User` model rather than writing password hashes directly. The account receives the platform administrator system role and an active owner membership in the default tenant. This project has no separate role table, so role creation maps to those existing enum-backed roles.

Do not keep bootstrap credentials in committed Compose files. Store the password in `.env`, deployment environment variables, or a secret manager, and disable `COGLATAS_SEED_ADMIN_ENABLED` after first startup unless continued reconciliation is intentional.

## Files

`LocalFileSystem` is the only implemented provider.

The names `ObjectStorage`, `S3Compatible`, and `OCIObjectStorage` pass provider-name validation but resolve to `UnsupportedObjectStorageService`. Readiness returns unhealthy for these providers, saves fail, reads throw, and signed URLs are unavailable.

Tenant metadata export does not include file bodies and cannot replace storage backups.

## HTTPS and reverse proxies

Production startup requires HTTPS redirect, HSTS, and secure cookies.

The canonical on-prem topology is vendor-neutral:

```text
Internet
  -> operator-provided external TLS proxy or tunnel
  -> host loopback/private origin port
  -> Coglatas Compose app
  -> PostgreSQL on the internal Compose network
```

The proxy owns certificates and TLS termination. The app container has no
certificate files and must not be published directly to an untrusted public
HTTP interface. The stock on-prem Compose mapping is
`127.0.0.1:${COGLATAS_PORT:-8080}:8080`; do not override that to
`0.0.0.0` unless a separate trusted private-network design is recorded and
firewalled.

Set the following operator values before a public on-prem startup:

```bash
COGLATAS_BIND_ADDRESS=127.0.0.1
COGLATAS_PORT=8080
REVERSE_PROXY_TRUST_FORWARDED_HEADERS=true
# One or more comma-delimited proxy peer IPs as seen by the app container.
REVERSE_PROXY_TRUSTED_PROXIES=172.17.0.1
# Or, when the proxy is on a dedicated internal Docker/private network:
REVERSE_PROXY_TRUSTED_NETWORKS=172.30.50.0/24
```

`ReverseProxy:TrustForwardedHeaders` enables `X-Forwarded-For`,
`X-Forwarded-Proto`, and `X-Forwarded-Host` only after the immediate peer
matches an explicitly configured IP or CIDR (loopback remains an ASP.NET Core
default). Hostnames are rejected rather than DNS-resolved, the proxy chain is
limited to one hop, and header symmetry is required. This prevents a public
client that reaches a non-public origin by mistake from spoofing scheme, host,
or client IP. Leaving proxy mode disabled also leaves forwarded headers
untrusted; enabling it without a boundary causes startup validation to fail.

The existing Sakura Caddy deployment is a conforming implementation of this
contract. It declares its private Docker proxy network as the trusted boundary;
operators with a fixed internal subnet should narrow the supplied CIDR further.
Cloudflare Tunnel is another possible external proxy implementation, but the
application contract does not depend on Cloudflare APIs or credentials.

Focused Kestrel coverage verifies secure CSRF-cookie issuance through an
explicit trusted HTTPS proxy boundary. It does not replace target-host proxy
verification.

## Health endpoints

- `GET /health/live`: process liveness.
- `GET /health/ready`: database connection, no pending migrations, storage readiness, Data Protection path, and default tenant in single-tenant mode.
- `GET /health`: temporary redirect to readiness.

`/health/ready` is the canonical operator and proxy readiness path. Configure
the external proxy health check against the public HTTPS host, preserving its
normal `Host`, `X-Forwarded-Proto: https`, and `X-Forwarded-For` behavior. The
in-container health check may use loopback only to determine whether the app
process itself is ready; it is not evidence that the public proxy route works.

Readiness does not prove:

- an admin user exists;
- login works;
- tenant memberships exist;
- core workflows pass;
- backups can be restored;
- reverse-proxy routing is correct.

## Required pre-deployment audit

Before any pilot:

1. Apply or explicitly disable the supported first-admin bootstrap.
2. Apply and record migrations.
3. Verify tenant resolution for the exact host/proxy topology.
4. Verify login, tenant membership, and role separation.
5. Run cross-tenant HTTP tests against PostgreSQL.
6. Verify uploads/downloads using the configured storage.
7. Back up and restore PostgreSQL plus file storage.
8. Record the app image/tag and configuration.
9. Confirm no placeholder secrets or example configuration remain.
10. Confirm `ss -ltn` (or equivalent) shows the app origin only on loopback or
    the documented trusted private interface, then verify
    `https://<public-host>/health/ready` through the external proxy.

See `docs/KNOWN_ISSUES.md` and `docs/OPERATIONS.md`.
