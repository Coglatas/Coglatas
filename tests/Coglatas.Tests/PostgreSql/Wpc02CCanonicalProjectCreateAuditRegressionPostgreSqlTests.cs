using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Projects;
using Coglatas.Application.Realtime;
using Coglatas.Application.Tenancy;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Audit;
using Coglatas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Tests.PostgreSql;

[Trait("Scope", "WPC02C")]
public sealed class Wpc02CCanonicalProjectCreateAuditRegressionPostgreSqlTests
{
    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task DraftCreateDoesNotProvisionProjectGeneralOrTaskWorkflow()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            var graph = await SeedGraphAsync(database, "draft-defaults");
            Guid projectId;

            await using (var scope = CreateServiceScope(database, graph))
            {
                var result = await scope.Service.CreateAsync(
                    graph.WorkspaceId,
                    new CanonicalCreateProjectRequest("Draft without activation defaults"),
                    "wpc02c-draft-no-defaults");

                Assert.True(result.IsSuccess, result.Error);
                projectId = result.Value!.Id;
                Assert.Equal(ProjectStatus.Planning, result.Value.Status);
                Assert.Equal(ProjectActivationState.NeverActivated, result.Value.ActivationState);
                Assert.Equal(ProjectVisibility.MembersOnly, result.Value.Visibility);
            }

            await using var verification = CreateTenantContext(database, graph);
            Assert.Empty(await verification.TaskWorkflowDefinitions
                .Where(item => item.ProjectId == projectId)
                .ToListAsync());
            Assert.Empty(await verification.TaskWorkflowStages
                .Where(item => item.ProjectId == projectId)
                .ToListAsync());
            Assert.Empty(await verification.Conversations
                .Where(item => item.ProjectId == projectId && item.DefaultKind == ConversationDefaultKind.ProjectGeneral)
                .ToListAsync());
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task SameIdempotencyKeyWithDifferentRequestConflictsWithoutSecondSideEffectSet()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            var graph = await SeedGraphAsync(database, "idempotency-mismatch");
            Guid projectId;

            await using (var first = CreateServiceScope(database, graph))
            {
                var created = await first.Service.CreateAsync(
                    graph.WorkspaceId,
                    new CanonicalCreateProjectRequest("Original request"),
                    "wpc02c-same-key-different-request");
                Assert.True(created.IsSuccess, created.Error);
                projectId = created.Value!.Id;
            }

            await using (var second = CreateServiceScope(database, graph))
            {
                var conflict = await second.Service.CreateAsync(
                    graph.WorkspaceId,
                    new CanonicalCreateProjectRequest("Different request"),
                    "wpc02c-same-key-different-request");
                Assert.False(conflict.IsSuccess);
                Assert.Equal("IdempotencyConflict", conflict.ErrorDetail?.Code);
            }

            await using var verification = CreateTenantContext(database, graph);
            Assert.Equal(1, await verification.Projects.CountAsync());
            Assert.Equal(1, await verification.ProjectMembers.CountAsync(item => item.ProjectId == projectId));
            Assert.Equal(1, await verification.IdempotencyRecords.CountAsync(item => item.ResourceType == "Project"));
            Assert.Equal(1, await verification.AuditLogs.CountAsync(item =>
                item.Action == "ProjectCreated" && item.EntityId == projectId));
            Assert.Equal(1, await verification.OutboxEvents.CountAsync(item =>
                item.EventType == "Security.AuthorizationStateChanged.v1"));
            Assert.Empty(await verification.TaskWorkflowDefinitions.Where(item => item.ProjectId == projectId).ToListAsync());
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task ForeignWorkspaceAndForeignTenantGroupsFailClosedWithoutCreateSideEffects()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            var graph = await SeedGraphAsync(database, "foreign-groups");

            await using var scope = CreateServiceScope(database, graph);
            var otherWorkspace = await scope.Service.CreateAsync(
                graph.WorkspaceId,
                new CanonicalCreateProjectRequest(
                    "Wrong Workspace Group",
                    GroupId: graph.ForeignWorkspaceGroupId),
                "wpc02c-wrong-workspace-group");
            var otherTenant = await scope.Service.CreateAsync(
                graph.WorkspaceId,
                new CanonicalCreateProjectRequest(
                    "Wrong Tenant Group",
                    GroupId: graph.ForeignTenantGroupId),
                "wpc02c-wrong-tenant-group");

            Assert.False(otherWorkspace.IsSuccess);
            Assert.False(otherTenant.IsSuccess);
            Assert.Equal("NotFound", otherWorkspace.ErrorDetail?.Code);
            Assert.Equal("NotFound", otherTenant.ErrorDetail?.Code);

            await using var verification = CreateTenantContext(database, graph);
            Assert.Empty(await verification.Projects.ToListAsync());
            Assert.Empty(await verification.ProjectMembers.ToListAsync());
            Assert.Empty(await verification.IdempotencyRecords.Where(item => item.ResourceType == "Project").ToListAsync());
            Assert.Empty(await verification.AuditLogs.Where(item => item.Action == "ProjectCreated").ToListAsync());
            Assert.Empty(await verification.OutboxEvents.Where(item => item.EventType == "Security.AuthorizationStateChanged.v1").ToListAsync());
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task RequiredOutboxFailureRollsBackProjectMembershipAuditAndIdempotency()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            var graph = await SeedGraphAsync(database, "outbox-failure");

            await using (var scope = CreateServiceScope(
                             database,
                             graph,
                             authorizationPublisher: new ThrowingAuthorizationPublisher()))
            {
                var result = await scope.Service.CreateAsync(
                    graph.WorkspaceId,
                    new CanonicalCreateProjectRequest("Rollback on required Outbox failure"),
                    "wpc02c-outbox-failure");

                Assert.False(result.IsSuccess);
                Assert.Equal("DependencyUnavailable", result.ErrorDetail?.Code);
            }

            await AssertNoCreateSideEffectsAsync(database, graph);
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task RequiredAuditFailureRollsBackProjectMembershipAndIdempotency()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            var graph = await SeedGraphAsync(database, "audit-failure");

            await using (var scope = CreateServiceScope(
                             database,
                             graph,
                             auditLogger: new ThrowingAuditLogger()))
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Service.CreateAsync(
                    graph.WorkspaceId,
                    new CanonicalCreateProjectRequest("Rollback on required audit failure"),
                    "wpc02c-audit-failure"));
            }

            await AssertNoCreateSideEffectsAsync(database, graph);
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task OwnerCanCreateRestrictedVisibility()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            var graph = await SeedGraphAsync(database, "restricted");
            Guid projectId;

            await using (var scope = CreateServiceScope(database, graph))
            {
                var result = await scope.Service.CreateAsync(
                    graph.WorkspaceId,
                    new CanonicalCreateProjectRequest(
                        "Restricted canonical Project",
                        Visibility: ProjectVisibility.Restricted),
                    "wpc02c-restricted");

                Assert.True(result.IsSuccess, result.Error);
                Assert.Equal(ProjectVisibility.Restricted, result.Value!.Visibility);
                projectId = result.Value.Id;
            }

            await using var verification = CreateTenantContext(database, graph);
            var project = await verification.Projects.SingleAsync(item => item.Id == projectId);
            Assert.Equal(ProjectVisibility.Restricted, project.Visibility);
            Assert.Equal(ProjectStatus.Planning, project.Status);
            Assert.Equal(ProjectActivationState.NeverActivated, project.ActivationState);
            Assert.Empty(await verification.TaskWorkflowDefinitions.Where(item => item.ProjectId == projectId).ToListAsync());
        });
    }

    private static async Task AssertNoCreateSideEffectsAsync(string connectionString, AuthorityGraph graph)
    {
        await using var verification = CreateTenantContext(connectionString, graph);
        Assert.Empty(await verification.Projects.ToListAsync());
        Assert.Empty(await verification.ProjectMembers.ToListAsync());
        Assert.Empty(await verification.IdempotencyRecords.Where(item => item.ResourceType == "Project").ToListAsync());
        Assert.Empty(await verification.AuditLogs.Where(item => item.Action == "ProjectCreated").ToListAsync());
        Assert.Empty(await verification.OutboxEvents.Where(item => item.EventType == "Security.AuthorizationStateChanged.v1").ToListAsync());
        Assert.Empty(await verification.TaskWorkflowDefinitions.ToListAsync());
        Assert.Empty(await verification.TaskWorkflowStages.ToListAsync());
    }

    private static async Task<AuthorityGraph> SeedGraphAsync(string connectionString, string suffix)
    {
        var currentTenant = new CurrentTenantService();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new AppDbContext(options, currentTenant);
        var runId = Guid.NewGuid().ToString("N");
        var now = new DateTimeOffset(2026, 8, 18, 8, 0, 0, TimeSpan.Zero);

        var tenant = new Tenant
        {
            Name = $"WPC02C regression tenant {suffix} {runId}",
            DisplayName = $"WPC02C regression tenant {suffix}",
            Slug = $"wpc02c-reg-{suffix}-{runId}",
            Status = TenantStatus.Active
        };
        var owner = NewUser($"owner-{suffix}-{runId}");

        currentTenant.SetPlatformScope();
        db.Tenants.Add(tenant);
        db.Users.Add(owner);
        await db.SaveChangesAsync();

        currentTenant.SetTenant(tenant.Id, tenant.Slug);
        db.TenantUsers.Add(new TenantUser
        {
            TenantId = tenant.Id,
            UserId = owner.Id,
            Role = TenantUserRole.Owner,
            Status = TenantUserStatus.Active,
            JoinedAt = now
        });

        var workspace = NewWorkspace(tenant.Id, owner.Id, $"primary-{suffix}-{runId}");
        var foreignWorkspace = NewWorkspace(tenant.Id, owner.Id, $"foreign-{suffix}-{runId}");
        db.Workspaces.AddRange(workspace, foreignWorkspace);
        db.WorkspaceMembers.Add(new WorkspaceMember
        {
            TenantId = tenant.Id,
            WorkspaceId = workspace.Id,
            UserId = owner.Id,
            Role = WorkspaceRole.Owner,
            Status = MembershipStatus.Active,
            JoinedAt = now
        });

        var foreignWorkspaceGroup = NewGroup(
            tenant.Id,
            foreignWorkspace.Id,
            owner.Id,
            $"foreign-workspace-{suffix}-{runId}");
        db.Groups.Add(foreignWorkspaceGroup);
        await db.SaveChangesAsync();

        var foreignTenant = new Tenant
        {
            Name = $"WPC02C foreign tenant {suffix} {runId}",
            DisplayName = $"WPC02C foreign tenant {suffix}",
            Slug = $"wpc02c-foreign-{suffix}-{runId}",
            Status = TenantStatus.Active
        };
        var foreignOwner = NewUser($"foreign-owner-{suffix}-{runId}");

        currentTenant.SetPlatformScope();
        db.Tenants.Add(foreignTenant);
        db.Users.Add(foreignOwner);
        await db.SaveChangesAsync();

        currentTenant.SetTenant(foreignTenant.Id, foreignTenant.Slug);
        db.TenantUsers.Add(new TenantUser
        {
            TenantId = foreignTenant.Id,
            UserId = foreignOwner.Id,
            Role = TenantUserRole.Owner,
            Status = TenantUserStatus.Active,
            JoinedAt = now
        });
        var foreignTenantWorkspace = NewWorkspace(
            foreignTenant.Id,
            foreignOwner.Id,
            $"cross-tenant-{suffix}-{runId}");
        db.Workspaces.Add(foreignTenantWorkspace);
        var foreignTenantGroup = NewGroup(
            foreignTenant.Id,
            foreignTenantWorkspace.Id,
            foreignOwner.Id,
            $"cross-tenant-group-{suffix}-{runId}");
        db.Groups.Add(foreignTenantGroup);
        await db.SaveChangesAsync();

        currentTenant.SetTenant(tenant.Id, tenant.Slug);
        return new AuthorityGraph(
            tenant.Id,
            tenant.Slug,
            workspace.Id,
            owner.Id,
            foreignWorkspaceGroup.Id,
            foreignTenantGroup.Id,
            now);
    }

    private static ServiceScope CreateServiceScope(
        string connectionString,
        AuthorityGraph graph,
        IAuditLogger? auditLogger = null,
        IAuthorizationStateChangePublisher? authorizationPublisher = null)
    {
        var currentTenant = new CurrentTenantService();
        currentTenant.SetTenant(graph.TenantId, graph.TenantSlug);
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options;
        var db = new AppDbContext(options, currentTenant);
        var clock = new FixedClock(graph.Now);
        var currentUser = new FixedCurrentUser(graph.OwnerUserId);
        var projectRepository = new ProjectRepository(db);
        var workspaceRepository = new WorkspaceRepository(db);
        var groupRepository = new GroupRepository(db);
        var tenantRepository = new TenantRepository(db);
        var capabilityEvaluator = new CapabilityGrantEvaluator(
            new CapabilityGrantRepository(db),
            tenantRepository,
            workspaceRepository,
            currentTenant,
            clock);
        authorizationPublisher ??= new AuthorizationStateChangePublisher(
            new TransactionalOutbox(new OutboxEventRepository(db), currentTenant, clock),
            currentTenant,
            clock);
        auditLogger ??= new DbAuditLogger(db, clock, currentUser, currentTenant);

        var service = new CanonicalProjectCreateService(
            projectRepository,
            workspaceRepository,
            groupRepository,
            tenantRepository,
            capabilityEvaluator,
            currentUser,
            currentTenant,
            clock,
            auditLogger,
            authorizationPublisher,
            new EfCreateIdempotencyCoordinator(db));
        return new ServiceScope(db, service);
    }

    private static AppDbContext CreateTenantContext(string connectionString, AuthorityGraph graph)
    {
        var currentTenant = new CurrentTenantService();
        currentTenant.SetTenant(graph.TenantId, graph.TenantSlug);
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options;
        return new AppDbContext(options, currentTenant);
    }

    private static User NewUser(string suffix) => new()
    {
        DisplayName = $"WPC02C regression user {suffix}",
        Email = $"{suffix}@example.test".ToLowerInvariant(),
        NormalizedEmail = $"{suffix}@example.test".ToUpperInvariant(),
        Status = UserStatus.Active,
        SystemRole = Coglatas.Domain.Enums.SystemRole.NormalUser
    };

    private static Workspace NewWorkspace(Guid tenantId, Guid creatorUserId, string suffix) => new()
    {
        TenantId = tenantId,
        Name = $"WPC02C workspace {suffix}",
        Slug = $"wpc02c-workspace-{suffix}",
        CreatedByUserId = creatorUserId,
        Status = WorkspaceStatus.Active
    };

    private static Group NewGroup(Guid tenantId, Guid workspaceId, Guid creatorUserId, string suffix) => new()
    {
        TenantId = tenantId,
        WorkspaceId = workspaceId,
        Name = $"WPC02C group {suffix}",
        Slug = $"wpc02c-group-{suffix}",
        GroupType = GroupType.Team,
        Status = GroupStatus.Active,
        CreatedByUserId = creatorUserId
    };

    private sealed record AuthorityGraph(
        Guid TenantId,
        string TenantSlug,
        Guid WorkspaceId,
        Guid OwnerUserId,
        Guid ForeignWorkspaceGroupId,
        Guid ForeignTenantGroupId,
        DateTimeOffset Now);

    private sealed class ServiceScope(AppDbContext context, CanonicalProjectCreateService service) : IAsyncDisposable
    {
        private AppDbContext Context { get; } = context;
        public CanonicalProjectCreateService Service { get; } = service;
        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class FixedCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid? UserId { get; } = userId;
        public Guid? SessionId => null;
        public string? Email => null;
        public Coglatas.Domain.Enums.SystemRole? SystemRole => Coglatas.Domain.Enums.SystemRole.NormalUser;
        public bool IsAuthenticated => true;
    }

    private sealed class ThrowingAuthorizationPublisher : IAuthorizationStateChangePublisher
    {
        public Task PublishAsync(
            Guid tenantId,
            Guid affectedUserId,
            string scopeType,
            Guid? scopeId,
            string change,
            CancellationToken cancellationToken = default) =>
            throw new RequiredOutboxStagingException();
    }

    private sealed class ThrowingAuditLogger : IAuditLogger
    {
        public Task LogAsync(AuditLogEntry entry, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Forced WPC-02C audit staging failure.");
    }
}
