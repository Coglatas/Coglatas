using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Projects;
using Coglatas.Application.Realtime;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Audit;
using Coglatas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Tests.PostgreSql;

[Trait("Scope", "WPC02D")]
public sealed class Wpc02DProjectActivationPostgreSqlTests
{
    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task DraftHasNoWorkflow_ActivationCommitsCanonicalOperationalDefaultsAtomically()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            var graph = await SeedPlanningProjectAsync(database, "activate");

            await using (var before = CreateTenantContext(database, graph))
            {
                Assert.Equal(0, await before.TaskWorkflowDefinitions.CountAsync(item => item.ProjectId == graph.ProjectId));
                Assert.Equal(0, await before.TaskWorkflowStages.CountAsync(item => item.ProjectId == graph.ProjectId));
                Assert.Equal(0, await before.Conversations.CountAsync(item =>
                    item.ProjectId == graph.ProjectId &&
                    item.DefaultKind == ConversationDefaultKind.ProjectGeneral));
            }

            await using (var scope = CreateActivationScope(database, graph, graph.OwnerUserId, canManage: true))
            {
                var result = await scope.Service.ActivateAsync(graph.ProjectId, expectedVersion: 1);
                Assert.True(result.IsSuccess, result.Error);
            }

            await using var verification = CreateTenantContext(database, graph);
            var project = await verification.Projects.SingleAsync(item => item.Id == graph.ProjectId);
            Assert.Equal(ProjectStatus.Active, project.Status);
            Assert.Equal(ProjectActivationState.Activated, project.ActivationState);
            Assert.Equal(graph.Now, project.ActivatedAtUtc);
            Assert.Equal(ProjectActivationService.CanonicalActivationVersion, project.ActivationVersion);
            Assert.True(project.VersionNo > 1);

            var definition = await verification.TaskWorkflowDefinitions
                .SingleAsync(item => item.ProjectId == graph.ProjectId);
            Assert.Equal("Default", definition.Name);
            Assert.True(definition.ReviewEnforcementEnabled);

            var stages = await verification.TaskWorkflowStages
                .Where(item => item.ProjectId == graph.ProjectId)
                .OrderBy(item => item.SortKey)
                .ToListAsync();
            Assert.Collection(
                stages,
                stage => AssertStage(stage, "Backlog", TaskStageCategory.Backlog, initial: true, terminal: false),
                stage => AssertStage(stage, "Todo", TaskStageCategory.Todo, initial: false, terminal: false),
                stage => AssertStage(stage, "In Progress", TaskStageCategory.InProgress, initial: false, terminal: false),
                stage => AssertStage(stage, "Review", TaskStageCategory.Review, initial: false, terminal: false),
                stage => AssertStage(stage, "Done", TaskStageCategory.Done, initial: false, terminal: true),
                stage => AssertStage(stage, "Cancelled", TaskStageCategory.Cancelled, initial: false, terminal: true));

            var general = await verification.Conversations.SingleAsync(item =>
                item.ProjectId == graph.ProjectId &&
                item.DefaultKind == ConversationDefaultKind.ProjectGeneral);
            Assert.Equal(ConversationType.ProjectChannel, general.Type);
            Assert.Equal("general", general.Title);
            Assert.Equal(ConversationVisibility.PublicWithinScope, general.Visibility);

            var participants = await verification.ConversationMembers
                .Where(item => item.ConversationId == general.Id)
                .OrderBy(item => item.UserId)
                .ToListAsync();
            Assert.Equal(2, participants.Count);
            var owner = Assert.Single(participants, item => item.UserId == graph.OwnerUserId);
            Assert.Equal(ConversationMemberRole.Admin, owner.Role);
            Assert.True(owner.CanRead);
            Assert.True(owner.CanPost);
            Assert.True(owner.CanManageMembers);

            var viewer = Assert.Single(participants, item => item.UserId == graph.ViewerUserId);
            Assert.Equal(ConversationMemberRole.ReadOnly, viewer.Role);
            Assert.True(viewer.CanRead);
            Assert.False(viewer.CanPost);
            Assert.False(viewer.CanManageMembers);

            Assert.Equal(1, await verification.AuditLogs.CountAsync(item =>
                item.Action == "ProjectActivated" && item.EntityId == graph.ProjectId));
            Assert.Equal(1, await verification.OutboxEvents.CountAsync(item =>
                item.EventType == "Projects.ProjectChanged.v1" && item.AggregateId == graph.ProjectId));
            Assert.Equal(2, await verification.OutboxEvents.CountAsync(item =>
                item.EventType == "Security.AuthorizationStateChanged.v1"));
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task StaleVersionRejectsActivationWithoutProvisioningOrAudit()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            var graph = await SeedPlanningProjectAsync(database, "stale");

            await using (var scope = CreateActivationScope(database, graph, graph.OwnerUserId, canManage: true))
            {
                var result = await scope.Service.ActivateAsync(graph.ProjectId, expectedVersion: 999);
                Assert.False(result.IsSuccess);
                Assert.Equal("ConcurrentModification", result.ErrorDetail?.Code);
                Assert.Equal("body.expectedVersion", result.ErrorDetail?.Target);
            }

            await AssertPlanningWithoutActivationSideEffectsAsync(database, graph);
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task CapabilityDeniedRejectsActivationWithoutProvisioningOrAudit()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            var graph = await SeedPlanningProjectAsync(database, "denied");

            await using (var scope = CreateActivationScope(database, graph, graph.OwnerUserId, canManage: false))
            {
                var result = await scope.Service.ActivateAsync(graph.ProjectId, expectedVersion: 1);
                Assert.False(result.IsSuccess);
                Assert.Equal("CapabilityDenied", result.ErrorDetail?.Code);
            }

            await AssertPlanningWithoutActivationSideEffectsAsync(database, graph);
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task InvalidExistingWorkflowFailsAfterGeneralStagingWithoutPartialCommit()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            var graph = await SeedPlanningProjectAsync(database, "invalid-workflow");

            await using (var seed = CreateTenantContext(database, graph))
            {
                var definition = new TaskWorkflowDefinition
                {
                    TenantId = graph.TenantId,
                    WorkspaceId = graph.WorkspaceId,
                    ProjectId = graph.ProjectId,
                    Name = "Invalid Legacy",
                    ReviewEnforcementEnabled = true,
                    VersionNo = 1
                };
                seed.TaskWorkflowDefinitions.Add(definition);
                seed.TaskWorkflowStages.Add(NewStage(
                    graph,
                    definition.Id,
                    "Only Initial",
                    TaskStageCategory.Todo,
                    1000,
                    initial: true,
                    terminal: false));
                await seed.SaveChangesAsync();
            }

            await using (var scope = CreateActivationScope(database, graph, graph.OwnerUserId, canManage: true))
            {
                var result = await scope.Service.ActivateAsync(graph.ProjectId, expectedVersion: 1);
                Assert.False(result.IsSuccess);
                Assert.Equal("InvalidTaskWorkflow", result.ErrorDetail?.Code);
            }

            await using var verification = CreateTenantContext(database, graph);
            var project = await verification.Projects.SingleAsync(item => item.Id == graph.ProjectId);
            Assert.Equal(ProjectStatus.Planning, project.Status);
            Assert.Equal(ProjectActivationState.NeverActivated, project.ActivationState);
            Assert.Null(project.ActivatedAtUtc);
            Assert.Null(project.ActivationVersion);
            Assert.Empty(await verification.Conversations.Where(item => item.ProjectId == graph.ProjectId).ToListAsync());
            Assert.Equal(1, await verification.TaskWorkflowDefinitions.CountAsync(item => item.ProjectId == graph.ProjectId));
            Assert.Equal(1, await verification.TaskWorkflowStages.CountAsync(item => item.ProjectId == graph.ProjectId));
            Assert.Equal(0, await verification.AuditLogs.CountAsync(item =>
                item.Action == "ProjectActivated" && item.EntityId == graph.ProjectId));
            Assert.Equal(0, await verification.OutboxEvents.CountAsync());
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task CompatibleExistingWorkflowIsReusedWithoutRegeneration()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            var graph = await SeedPlanningProjectAsync(database, "reuse");
            Guid existingDefinitionId;

            await using (var seed = CreateTenantContext(database, graph))
            {
                var definition = new TaskWorkflowDefinition
                {
                    TenantId = graph.TenantId,
                    WorkspaceId = graph.WorkspaceId,
                    ProjectId = graph.ProjectId,
                    Name = "Legacy Compatible",
                    ReviewEnforcementEnabled = false,
                    VersionNo = 1
                };
                seed.TaskWorkflowDefinitions.Add(definition);
                existingDefinitionId = definition.Id;
                seed.TaskWorkflowStages.AddRange(
                    NewStage(graph, definition.Id, "Ready", TaskStageCategory.Todo, 1000, true, false),
                    NewStage(graph, definition.Id, "Doing", TaskStageCategory.InProgress, 2000, false, false),
                    NewStage(graph, definition.Id, "Complete", TaskStageCategory.Done, 3000, false, true));
                await seed.SaveChangesAsync();
            }

            await using (var scope = CreateActivationScope(database, graph, graph.OwnerUserId, canManage: true))
            {
                var result = await scope.Service.ActivateAsync(graph.ProjectId, expectedVersion: 1);
                Assert.True(result.IsSuccess, result.Error);
            }

            await using var verification = CreateTenantContext(database, graph);
            var definitionAfter = await verification.TaskWorkflowDefinitions
                .SingleAsync(item => item.ProjectId == graph.ProjectId);
            Assert.Equal(existingDefinitionId, definitionAfter.Id);
            Assert.Equal("Legacy Compatible", definitionAfter.Name);
            Assert.False(definitionAfter.ReviewEnforcementEnabled);
            Assert.Equal(3, await verification.TaskWorkflowStages.CountAsync(item => item.ProjectId == graph.ProjectId));
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task ConcurrentActivationCommitsExactlyOneCanonicalOutcome()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            var graph = await SeedPlanningProjectAsync(database, "concurrent");
            await using var first = CreateActivationScope(database, graph, graph.OwnerUserId, canManage: true);
            await using var second = CreateActivationScope(database, graph, graph.OwnerUserId, canManage: true);

            var results = await Task.WhenAll(
                first.Service.ActivateAsync(graph.ProjectId, expectedVersion: 1),
                second.Service.ActivateAsync(graph.ProjectId, expectedVersion: 1));

            Assert.Equal(1, results.Count(result => result.IsSuccess));
            var rejected = Assert.Single(results, result => !result.IsSuccess);
            Assert.Equal("ConcurrentModification", rejected.ErrorDetail?.Code);

            await using var verification = CreateTenantContext(database, graph);
            var project = await verification.Projects.SingleAsync(item => item.Id == graph.ProjectId);
            Assert.Equal(ProjectStatus.Active, project.Status);
            Assert.Equal(ProjectActivationState.Activated, project.ActivationState);
            Assert.Equal(1, await verification.TaskWorkflowDefinitions.CountAsync(item => item.ProjectId == graph.ProjectId));
            Assert.Equal(6, await verification.TaskWorkflowStages.CountAsync(item => item.ProjectId == graph.ProjectId));
            Assert.Equal(1, await verification.Conversations.CountAsync(item =>
                item.ProjectId == graph.ProjectId &&
                item.DefaultKind == ConversationDefaultKind.ProjectGeneral));
            Assert.Equal(1, await verification.AuditLogs.CountAsync(item =>
                item.Action == "ProjectActivated" && item.EntityId == graph.ProjectId));
            Assert.Equal(1, await verification.OutboxEvents.CountAsync(item =>
                item.EventType == "Projects.ProjectChanged.v1" && item.AggregateId == graph.ProjectId));
        });
    }

    private static async Task AssertPlanningWithoutActivationSideEffectsAsync(
        string connectionString,
        ActivationGraph graph)
    {
        await using var verification = CreateTenantContext(connectionString, graph);
        var project = await verification.Projects.SingleAsync(item => item.Id == graph.ProjectId);
        Assert.Equal(ProjectStatus.Planning, project.Status);
        Assert.Equal(ProjectActivationState.NeverActivated, project.ActivationState);
        Assert.Null(project.ActivatedAtUtc);
        Assert.Null(project.ActivationVersion);
        Assert.Empty(await verification.TaskWorkflowDefinitions.Where(item => item.ProjectId == graph.ProjectId).ToListAsync());
        Assert.Empty(await verification.Conversations.Where(item => item.ProjectId == graph.ProjectId).ToListAsync());
        Assert.Equal(0, await verification.AuditLogs.CountAsync(item =>
            item.Action == "ProjectActivated" && item.EntityId == graph.ProjectId));
        Assert.Equal(0, await verification.OutboxEvents.CountAsync());
    }

    private static async Task<ActivationGraph> SeedPlanningProjectAsync(string connectionString, string suffix)
    {
        var currentTenant = new CurrentTenantService();
        var options = Options(connectionString);
        await using var db = new AppDbContext(options, currentTenant);
        var runId = Guid.NewGuid().ToString("N");
        var now = new DateTimeOffset(2026, 8, 18, 0, 0, 0, TimeSpan.Zero);

        var tenant = new Tenant
        {
            Name = $"WPC02D Tenant {suffix} {runId}",
            DisplayName = $"WPC02D Tenant {suffix}",
            Slug = $"wpc02d-{suffix}-{runId}",
            Status = TenantStatus.Active
        };
        var owner = NewUser($"owner-{suffix}-{runId}");
        var viewer = NewUser($"viewer-{suffix}-{runId}");

        currentTenant.SetPlatformScope();
        db.Tenants.Add(tenant);
        db.Users.AddRange(owner, viewer);
        await db.SaveChangesAsync();

        currentTenant.SetTenant(tenant.Id, tenant.Slug);
        db.TenantUsers.AddRange(
            NewTenantUser(tenant.Id, owner.Id, TenantUserRole.Owner, now),
            NewTenantUser(tenant.Id, viewer.Id, TenantUserRole.Member, now));

        var workspace = new Workspace
        {
            TenantId = tenant.Id,
            Name = $"WPC02D Workspace {suffix}",
            Slug = $"wpc02d-workspace-{suffix}-{runId}",
            CreatedByUserId = owner.Id,
            Status = WorkspaceStatus.Active
        };
        db.Workspaces.Add(workspace);
        db.WorkspaceMembers.AddRange(
            NewWorkspaceMember(tenant.Id, workspace.Id, owner.Id, WorkspaceRole.Owner, now),
            NewWorkspaceMember(tenant.Id, workspace.Id, viewer.Id, WorkspaceRole.ReadOnly, now));

        var project = new Project
        {
            TenantId = tenant.Id,
            WorkspaceId = workspace.Id,
            OwnerUserId = owner.Id,
            Name = $"WPC02D Project {suffix}",
            Slug = $"wpc02d-project-{suffix}-{runId}",
            Status = ProjectStatus.Planning,
            Visibility = ProjectVisibility.MembersOnly,
            ActivationState = ProjectActivationState.NeverActivated,
            VersionNo = 1,
            CreatedByUserId = owner.Id
        };
        db.Projects.Add(project);
        db.ProjectMembers.AddRange(
            new ProjectMember
            {
                TenantId = tenant.Id,
                ProjectId = project.Id,
                UserId = owner.Id,
                Role = ProjectRole.Owner,
                JoinedAt = now
            },
            new ProjectMember
            {
                TenantId = tenant.Id,
                ProjectId = project.Id,
                UserId = viewer.Id,
                Role = ProjectRole.Viewer,
                JoinedAt = now
            });
        await db.SaveChangesAsync();

        return new ActivationGraph(
            tenant.Id,
            tenant.Slug,
            workspace.Id,
            project.Id,
            owner.Id,
            viewer.Id,
            now);
    }

    private static ActivationScope CreateActivationScope(
        string connectionString,
        ActivationGraph graph,
        Guid userId,
        bool canManage)
    {
        var currentTenant = new CurrentTenantService();
        currentTenant.SetTenant(graph.TenantId, graph.TenantSlug);
        var db = new AppDbContext(Options(connectionString), currentTenant);
        var clock = new FixedClock(graph.Now);
        var currentUser = new TestCurrentUser(userId);
        var projectRepository = new ProjectRepository(db);
        var workspaceRepository = new WorkspaceRepository(db);
        var tenantRepository = new TenantRepository(db);
        var outbox = new TransactionalOutbox(new OutboxEventRepository(db), currentTenant, clock);
        var authorizationChanges = new AuthorizationStateChangePublisher(outbox, currentTenant, clock);
        var general = new ProjectGeneralActivationProvisioner(
            new DefaultConversationStore(db),
            projectRepository,
            currentTenant,
            clock,
            authorizationChanges);
        var workflow = new ProjectTaskWorkflowActivationProvisioner(
            new ProjectActivationWorkflowStore(db),
            new ProjectTaskWorkflowResolver(new NoConfiguredProjectTaskWorkflowSource()));
        var service = new ProjectActivationService(
            projectRepository,
            workspaceRepository,
            tenantRepository,
            new FixedProjectAuthorizationService(canManage),
            general,
            workflow,
            new ProjectActivationUnitOfWork(db),
            currentUser,
            currentTenant,
            clock,
            new DbAuditLogger(db, clock, currentUser, currentTenant),
            new BusinessInvalidationPublisher(outbox, currentTenant, clock));
        return new ActivationScope(db, service);
    }

    private static AppDbContext CreateTenantContext(string connectionString, ActivationGraph graph)
    {
        var currentTenant = new CurrentTenantService();
        currentTenant.SetTenant(graph.TenantId, graph.TenantSlug);
        return new AppDbContext(Options(connectionString), currentTenant);
    }

    private static DbContextOptions<AppDbContext> Options(string connectionString) =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .AddInterceptors(new ProjectGovernanceSaveChangesInterceptor())
            .Options;

    private static TaskWorkflowStage NewStage(
        ActivationGraph graph,
        Guid definitionId,
        string name,
        TaskStageCategory category,
        long sortKey,
        bool initial,
        bool terminal) => new()
    {
        TenantId = graph.TenantId,
        WorkspaceId = graph.WorkspaceId,
        ProjectId = graph.ProjectId,
        DefinitionId = definitionId,
        Name = name,
        InternalCategory = category,
        SortKey = sortKey,
        IsInitialStage = initial,
        IsTerminalStage = terminal,
        VersionNo = 1
    };

    private static void AssertStage(
        TaskWorkflowStage stage,
        string name,
        TaskStageCategory category,
        bool initial,
        bool terminal)
    {
        Assert.Equal(name, stage.Name);
        Assert.Equal(category, stage.InternalCategory);
        Assert.Equal(initial, stage.IsInitialStage);
        Assert.Equal(terminal, stage.IsTerminalStage);
    }

    private static User NewUser(string suffix) => new()
    {
        DisplayName = $"WPC02D User {suffix}",
        Email = $"{suffix}@example.test".ToLowerInvariant(),
        NormalizedEmail = $"{suffix}@example.test".ToUpperInvariant(),
        Status = UserStatus.Active,
        SystemRole = SystemRole.NormalUser
    };

    private static TenantUser NewTenantUser(
        Guid tenantId,
        Guid userId,
        TenantUserRole role,
        DateTimeOffset now) => new()
    {
        TenantId = tenantId,
        UserId = userId,
        Role = role,
        Status = TenantUserStatus.Active,
        JoinedAt = now
    };

    private static WorkspaceMember NewWorkspaceMember(
        Guid tenantId,
        Guid workspaceId,
        Guid userId,
        WorkspaceRole role,
        DateTimeOffset now) => new()
    {
        TenantId = tenantId,
        WorkspaceId = workspaceId,
        UserId = userId,
        Role = role,
        Status = MembershipStatus.Active,
        JoinedAt = now
    };

    private sealed record ActivationGraph(
        Guid TenantId,
        string TenantSlug,
        Guid WorkspaceId,
        Guid ProjectId,
        Guid OwnerUserId,
        Guid ViewerUserId,
        DateTimeOffset Now);

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class TestCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid? UserId { get; } = userId;
        public Guid? SessionId => null;
        public string? Email => null;
        public SystemRole? SystemRole => Coglatas.Domain.Enums.SystemRole.NormalUser;
        public bool IsAuthenticated => true;
    }

    private sealed class FixedProjectAuthorizationService(bool canManage) : IProjectAuthorizationService
    {
        public Task<bool> CanViewProject(Guid userId, Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(canManage);

        public Task<bool> CanManageProject(Guid userId, Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(canManage);

        public Task<bool> CanCreateProject(Guid userId, Guid workspaceId, Guid groupId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class ActivationScope(AppDbContext db, ProjectActivationService service) : IAsyncDisposable
    {
        private AppDbContext Db { get; } = db;
        public ProjectActivationService Service { get; } = service;
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
