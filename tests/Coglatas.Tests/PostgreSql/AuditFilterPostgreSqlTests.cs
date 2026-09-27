using Coglatas.Application.Audit;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Tenancy;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Tests.PostgreSql;

public sealed class AuditFilterPostgreSqlTests
{
    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task AuditGridFilterPredicatesTranslateAndCountInsideTenantScope()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(PostgreSqlTestEnvironment.RequireConnectionString())
            .Options;
        var currentTenant = new CurrentTenantService();
        currentTenant.SetPlatformScope();
        var runId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;

        await using var dbContext = new AppDbContext(options, currentTenant);
        var tenant = new Tenant
        {
            Name = $"Audit filter {runId}",
            DisplayName = "Audit filter",
            Slug = $"audit-filter-{runId}",
        };
        var otherTenant = new Tenant
        {
            Name = $"Other audit filter {runId}",
            DisplayName = "Other audit filter",
            Slug = $"other-audit-filter-{runId}",
        };
        var user = new User
        {
            DisplayName = $"Auditor {runId}",
            Email = $"auditor-{runId}@example.test",
            NormalizedEmail = $"AUDITOR-{runId}@EXAMPLE.TEST".ToUpperInvariant(),
            PasswordHash = "test-hash",
            Status = UserStatus.Active,
        };
        dbContext.Tenants.AddRange(tenant, otherTenant);
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        var workspace = new Workspace
        {
            TenantId = tenant.Id,
            Name = $"Retention {runId}",
            Slug = $"retention-{runId}",
            CreatedByUserId = user.Id,
            Status = WorkspaceStatus.Active,
        };
        dbContext.Workspaces.Add(workspace);
        dbContext.AuditLogs.AddRange(
            new AuditLog
            {
                TenantId = tenant.Id,
                ActorUserId = user.Id,
                WorkspaceId = workspace.Id,
                Action = "file.export.failed",
                EntityType = "ExportJob",
                Summary = $"Neptune {runId} failed.",
                CreatedAt = now,
            },
            new AuditLog
            {
                TenantId = otherTenant.Id,
                ActorUserId = user.Id,
                Action = "file.export.failed",
                EntityType = "ExportJob",
                Summary = $"Neptune {runId} failed in another Tenant.",
                CreatedAt = now,
            });
        await dbContext.SaveChangesAsync();

        currentTenant.SetTenant(tenant.Id, tenant.Slug);
        var service = new DbAuditQueryService(
            dbContext,
            new FixedCurrentUser(user),
            currentTenant,
            new TenantRepository(dbContext),
            new FixedAuditAuthorization());

        var result = await service.ListAuditGridAsync(new AuditLogQuery(
            Action: "FILE.EXPORT.FAILED",
            EntityType: "exportjob",
            // RFC3339 permits non-UTC offsets. Normalize them before Npgsql
            // parameter binding so schema-compliant fuzz inputs cannot become 500s.
            FromDate: now.AddMinutes(-1).ToOffset(TimeSpan.FromMinutes(220)),
            ToDate: now.AddMinutes(1).ToOffset(TimeSpan.FromMinutes(220)),
            PageSize: 100,
            Q: $"neptune {runId}",
            Actor: runId,
            Severity: "critical",
            Result: "failed"));

        Assert.True(result.IsSuccess, result.Error);
        Assert.Single(result.Value!.Items);
        Assert.Equal(1, result.Value.TotalCount);
        Assert.Equal(workspace.Name, result.Value.Items[0].WorkspaceLabel);
    }

    private sealed class FixedCurrentUser(User user) : ICurrentUser
    {
        public Guid? UserId => user.Id;
        public Guid? SessionId => Guid.NewGuid();
        public string? Email => user.Email;
        public SystemRole? SystemRole => user.SystemRole;
        public bool IsAuthenticated => true;
    }

    private sealed class FixedAuditAuthorization : IAuditAuthorizationService
    {
        private static readonly AuditCapabilityResponse Capabilities = new(true, true, false, false, true);

        public Task<AuditCapabilityResponse> GetCapabilitiesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Capabilities);

        public Task<Result> AuthorizeAsync(
            string capabilityKey,
            string operation,
            CancellationToken cancellationToken = default) => Task.FromResult(Result.Success());
    }
}
