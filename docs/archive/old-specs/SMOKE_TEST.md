# Smoke Test

Run this checklist before a local demo, internal pilot handoff, or on-prem school demonstration. Record failures in `docs/archive/status/PILOT_STATUS.md`; do not silently skip broken workflows.

## Local Dev Startup

1. Start PostgreSQL and create a local database/user.
2. From the repository root:

```powershell
$env:ConnectionStrings__DefaultConnection='Host=localhost;Port=5432;Database=aip_portal_dev;Username=aip_portal;Password=<local-password>'
dotnet restore AipPortal.slnx
dotnet build AipPortal.slnx
dotnet tool restore
dotnet ef database update --project src/AipPortal.Infrastructure --startup-project src/AipPortal.Web
dotnet run --project src/AipPortal.Web
```

3. Open the printed local URL.
4. Check `/health/live` and `/health/ready`.
5. Confirm no production-only warnings are hidden.

## Docker Compose Startup

```powershell
$env:POSTGRES_PASSWORD='<strong-password>'
docker compose up --build
```

Verify:

- App responds at `http://localhost:8080` unless `AIP_PORTAL_PORT` is overridden.
- PostgreSQL container becomes healthy.
- File uploads write to the `aip_uploads` volume.
- Shutdown and restart keep database and uploaded-file state.

## OnPremSingleTenant Startup

```powershell
$env:POSTGRES_PASSWORD='<strong-password>'
docker compose -f deploy/onprem/compose.yml up --build
```

Verify:

- `Tenancy:AppMode=OnPremSingleTenant`.
- The configured default tenant exists after startup seed.
- `GET /api/tenants/current` returns the default tenant.
- Tenant switching is hidden and disabled.
- Local file storage root is writable and backed up.
- Plan/subscription data is presented as license/configuration data, not payment.

## SaaS-Mode Tenant Setup

1. Start with `Tenancy:AppMode=SaaS`.
2. Use host/subdomain/session resolution in non-development environments. Use `X-Tenant-Slug` only in development.
3. Sign in as PlatformAdmin.
4. Create a tenant through `POST /api/platform/tenants`.
5. Add a tenant owner/admin.
6. Sign in as that tenant admin.
7. Confirm tenant admin cannot call `/api/platform/*`.
8. Confirm a TenantA user cannot access TenantB records by URL or API.
9. Suspend TenantA and confirm normal tenant routes fail.
10. Reactivate TenantA and confirm normal access returns.

## First Admin Setup

- Create any PlatformAdmin through a controlled bootstrap procedure.
- Disable `Platform:PlatformAdminSetupMode` after setup.
- Do not keep default passwords.
- Confirm `Security:CookieSecurePolicy=Always`, `Security:RequireHttps=true`, and `Security:EnableHsts=true` for production.

## Basic User Workflow

1. Invite a user from tenant admin.
2. Register by invite.
3. Login.
4. Change password.
5. Create a workspace.
6. Create a group.
7. Add the user as a member.
8. Create a channel.
9. Post a message.
10. Reply in a thread.
11. Pin the post.
12. Suspend the user and verify login is blocked.
13. Reactivate the user.

## Production Tracking Workflow

1. Create a project.
2. Add a project member.
3. Create a milestone.
4. Create a task.
5. Assign the member.
6. Add a comment.
7. Create an artifact.
8. Upload an artifact version.
9. View project Gantt data.
10. View project dashboard data.
11. View `GET /api/me/tasks`.

## DM And Notification Workflow

1. Create a direct conversation.
2. Send a message.
3. Confirm the recipient sees an unread count.
4. Open the conversation as the recipient.
5. Mark the conversation read.
6. Confirm read state updates.
7. Confirm a non-member cannot read the conversation.
8. Trigger a notification and confirm another user cannot mark it read.

## File Upload Test

1. Upload a valid `.txt` file with `text/plain`.
2. Upload a blocked extension and confirm rejection.
3. Upload a blocked MIME type and confirm rejection.
4. Upload an oversized file and confirm rejection.
5. Download the authorized file.
6. Try to download from a user without access and confirm denial.
7. Confirm file metadata uses a `tenants/{tenantId}/...` storage key.
8. Confirm audit logging for upload/download/delete where implemented.

## Tenant-Aware Shell

- Sign in as a normal tenant user and confirm the header shows current tenant name, status, role, and app mode.
- Confirm normal users do not see Platform Admin navigation.
- In OnPremSingleTenant mode, confirm the tenant switcher is hidden.
- In SaaS or OnPremMultiTenant with switching enabled, confirm the switcher lists only memberships from `GET /api/tenants/my` and reloads after a successful switch.

## Admin Separation

- Sign in as Tenant Owner/Admin and confirm `/tenant-admin` loads current-tenant usage, quotas, features, and settings summary.
- Confirm Tenant Owner/Admin does not see `/platform-admin` navigation and cannot call `/api/platform/*`.
- Sign in as PlatformAdmin and confirm `/platform-admin` loads platform overview, tenants, usage, and plans.

## Backup Test

1. Stop writes or create a consistent snapshot.
2. Back up PostgreSQL:

```powershell
pg_dump --format=custom --file=aipportal.backup "$env:AIPPORTAL_DATABASE_URL"
```

3. Back up the configured `FileStorage:RootPath` or Docker upload volume.
4. Restore both into an isolated environment.
5. Start the app against restored data.
6. Verify login, tenant selection, project lists, file metadata, authorized download, audit continuity, and `/health/ready`.

## Shutdown And Restart Test

- Stop the app cleanly.
- Restart with the same database and file storage.
- Verify login still works.
- Verify tenant context still resolves.
- Verify previously uploaded files still download through authorization.
- Verify project/task/dashboard data persists.
