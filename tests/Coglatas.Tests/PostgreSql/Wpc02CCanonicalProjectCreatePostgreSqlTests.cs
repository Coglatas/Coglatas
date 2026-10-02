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
public sealed class Wpc02CCanonicalProjectCreatePostgreSqlTests
{
    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task OwnerCreateAndRetryCommitsOneCanonicalPlanningProject()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithMigratedTemporaryDatabaseAsync(connectionString, async database =>
        {
            var graph = await SeedAuthorityAsync(database, "owner");
            var request = new CanonicalCreateProjectRequest(
                "Canonical Project",
                "WPC-02C owner create",
                Visibility: ProjectVisibility.WorkspaceVisible,
                StartDate: new DateOnly(2026, 9, 1),
                EndDate: new DateOnly(2026, 9, 30));

            Guid projectId;
            await using (var first = CreateServiceScope(database, graph, graph.OwnerUserId, SystemRole.NormalUser))
            {
                var result = await first.Service.CreateAsync(graph.WorkspaceId, request, "wpc02c-owner-create");
                Assert.True(result.IsSuccess, result.Error);
                projectId = result.Value!.Id;
                Assert.Equal(ProjectStatus.Planning, result.Value.Status);
                Assert.Equal(ProjectActivationState.NeverActivated, result.Value.ActivationState);
                Assert.Equal(ProjectVisibility.WorkspaceVisible, result.Value.Visibility);
            }

            await using (var replay = CreateServiceScope(database, graph, graph.OwnerUserId, SystemRole.NormalUser))
            {
                var result = await replay.Service.CreateAsync(graph.WorkspaceId, request, "wpc02c-owner-create");
                Assert.True(result.IsSuccess, result.Error);
                Assert.Equal(projectId, result.Value!.Id);
            }

            await using var verification = CreateTenantContext(database, graph);
            var project = await verification.Projects.SingleAsync(item => item.Id == projectId);
            Assert.Equal(graph.TenantId, project.TenantId);
            Assert.Equal(graph.WorkspaceId, project.WorkspaceId);
            Assert.Null(project.GroupId);
            Assert.Equal(graph.OwnerUserId, project.OwnerUserId);
            Assert.Equal(ProjectStatus.Planning, project.Status);
            Assert.Equal(ProjectActivationState.NeverActivated, project.ActivationState);
            Assert.Null(project.ActivatedAtUtc);
            Assert.Null(project.ActivationVersion);
            Assert.Equal(ProjectVisibility.WorkspaceVisible, project.Visibility);
            Assert.True(project.Slug.Length <= 140);

            var owner = Assert.Single(await verification.ProjectMembers
                .Where(item => item.ProjectId == projectId)
                .ToListAsync());
            Assert.Equal(graph.OwnerUserId, owner.UserId);
            Assert.Equal(ProjectRole.Owner, owner.Role);
            Assert.Equal(1, await verification.IdempotencyRecords.CountAsync(item => item.ResourceType == "Project"));
            Assert.Equal(1, await verification.AuditLogs.CountAsync(item =>
                item.Action == "ProjectCreated" && item.EntityId == projectId));
            Assert.Equal(1, await verification.OutboxEvents.CountAsync(item =>
                item.EventType == "Security.AuthorizationStateChanged.v1" && item.AggregateId == graph.OwnerUserId));
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task GroupManagerAuthorityCannotEscapeBoundGroup()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithMigratedTemporaryDatabaseAsync(connectionString, async database =>
        {
            var graph = await SeedAuthorityAsync(database, "group");
            await using var scope = CreateServiceScope(database, graph, graph.GroupManagerUserId, SystemRole.NormalUser);

            var bound = await scope.Service.CreateAsync(
                graph.WorkspaceId,
                new CanonicalCreateProjectRequest("Group-bound", GroupId: graph.GroupId),
                "wpc02c-group-bound");
            Assert.True(bound.IsSuccess, bound.Error);
            Assert.Equal(graph.GroupId, bound.Value!.GroupId);
            Assert.Equal(ProjectVisibility.MembersOnly, bound.Value.Visibility);

            var ungrouped = await scope.Service.CreateAsync(
                graph.WorkspaceId,
                new CanonicalCreateProjectRequest("Ungrouped escape"),
                "wpc02c-group-escape");
            Assert.False(ungrouped.IsSuccess);
            Assert.Equal("CapabilityDenied", ungrouped.ErrorDetail?.Code);

            await using var verification = CreateTenantContext(database, graph);
            Assert.Equal(1, await verification.Projects.CountAsync());
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task DelegatedProjectCreateDoesNotImplyVisibilityManagement()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithMigratedTemporaryDatabaseAsync(connectionString, async database =>
        {
            var graph = await SeedAuthorityAsync(database, "delegated", grantProjectCreate: true);

            await using (var scope = CreateServiceScope(database, graph, graph.DelegatedUserId, SystemRole.NormalUser))
            {
                var defaultVisibility = await scope.Service.CreateAsync(
                    graph.WorkspaceId,
                    new CanonicalCreateProjectRequest("Delegated default"),
                    "wpc02c-delegated-default");
                Assert.True(defaultVisibility.IsSuccess, defaultVisibility.Error);
                Assert.Equal(ProjectVisibility.MembersOnly, defaultVisibility.Value!.Visibility);

                var nonDefault = await scope.Service.CreateAsync(
                    graph.WorkspaceId,
                    new CanonicalCreateProjectRequest("Delegated visible", Visibility: ProjectVisibility.WorkspaceVisible),
                    "wpc02c-delegated-visible");
                Assert.False(nonDefault.IsSuccess);
                Assert.Equal("CapabilityDenied", nonDefault.ErrorDetail?.Code);
            }

            await AddWorkspaceCapabilityAsync(
                database,
                graph,
                graph.DelegatedUserId,
                CapabilityKeys.ProjectVisibilityManage);

            await using (var scope = CreateServiceScope(database, graph, graph.DelegatedUserId, SystemRole.NormalUser))
            {
                var authorized = await scope.Service.CreateAsync(
                    graph.WorkspaceId,
                    new CanonicalCreateProjectRequest("Delegated visible", Visibility: ProjectVisibility.WorkspaceVisible),
                    "wpc02c-delegated-visible");
                Assert.True(authorized.IsSuccess, authorized.Error);
                Assert.Equal(ProjectVisibility.WorkspaceVisible, authorized.Value!.Visibility);
            }
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task SystemAdminWithoutWorkspaceMembershipHasNoImplicitCreateAuthority()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithMigratedTemporaryDatabaseAsync(connectionString, async database =>
        {
            var graph = await SeedAuthorityAsync(database, "system-admin");
            await using var scope = CreateServiceScope(database, graph, graph.SystemAdminUserId, SystemRole.SystemAdmin);

            var result = await scope.Service.CreateAsync(
                graph.WorkspaceId,
                new CanonicalCreateProjectRequest("Implicit admin must fail"),
                "wpc02c-system-admin");

            Assert.False(result.IsSuccess);
            Assert.Equal("NotFound", result.ErrorDetail?.Code);
            await using var verification = CreateTenantContext(database, graph);
            Assert.Empty(await verification.Projects.ToListAsync());
            Assert.Empty(await verification.IdempotencyRecords.Where(item => item.ResourceType == "Project").ToListAsync());
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "Issue409")]
    public async Task CreateOptionsUseCanonicalAuthorityWithoutDisclosingOtherGroups()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithMigratedTemporaryDatabaseAsync(connectionString, async database =>
        {
            var graph = await SeedAuthorityAsync(database, "create-options", grantProjectCreate: true);
            Guid otherActiveGroupId;
            Guid archivedGroupId;
            await using (var seed = CreateTenantContext(database, graph))
            {
                var otherActive = new Group
                {
                    TenantId = graph.TenantId,
                    WorkspaceId = graph.WorkspaceId,
                    Name = "Other active Group",
                    Slug = $"other-active-{Guid.NewGuid():N}",
                    GroupType = GroupType.Team,
                    Status = GroupStatus.Active,
                    CreatedByUserId = graph.OwnerUserId
                };
                var archived = new Group
                {
                    TenantId = graph.TenantId,
                    WorkspaceId = graph.WorkspaceId,
                    Name = "Archived Group",
                    Slug = $"archived-{Guid.NewGuid():N}",
                    GroupType = GroupType.Team,
                    Status = GroupStatus.Archived,
                    CreatedByUserId = graph.OwnerUserId
                };
                otherActiveGroupId = otherActive.Id;
                archivedGroupId = archived.Id;
                seed.Groups.AddRange(otherActive, archived);
                await seed.SaveChangesAsync();
            }

            await using (var owner = CreateServiceScope(database, graph, graph.OwnerUserId, SystemRole.NormalUser))
            {
                var result = await owner.Service.GetCreateOptionsAsync(graph.WorkspaceId);
                Assert.True(result.IsSuccess, result.Error);
                Assert.Equal(graph.WorkspaceId, result.Value!.WorkspaceId);
                Assert.True(result.Value.CanCreateUngrouped);
                Assert.Equal(
                    new[] { ProjectVisibility.WorkspaceVisible, ProjectVisibility.MembersOnly, ProjectVisibility.Restricted },
                    result.Value.AllowedVisibilities);
                Assert.Equal(
                    new[] { graph.GroupId, otherActiveGroupId }.Order(),
                    result.Value.Groups.Select(group => group.Id).Order());
                Assert.DoesNotContain(result.Value.Groups, group => group.Id == archivedGroupId);
            }

            await using (var groupManager = CreateServiceScope(database, graph, graph.GroupManagerUserId, SystemRole.NormalUser))
            {
                var result = await groupManager.Service.GetCreateOptionsAsync(graph.WorkspaceId);
                Assert.True(result.IsSuccess, result.Error);
                Assert.False(result.Value!.CanCreateUngrouped);
                Assert.Equal(new[] { ProjectVisibility.MembersOnly }, result.Value.AllowedVisibilities);
                Assert.Equal(graph.GroupId, Assert.Single(result.Value.Groups).Id);
                Assert.DoesNotContain(result.Value.Groups, group => group.Id == otherActiveGroupId);
            }

            await using (var delegated = CreateServiceScope(database, graph, graph.DelegatedUserId, SystemRole.NormalUser))
            {
                var result = await delegated.Service.GetCreateOptionsAsync(graph.WorkspaceId);
                Assert.True(result.IsSuccess, result.Error);
                Assert.True(result.Value!.CanCreateUngrouped);
                Assert.Equal(new[] { ProjectVisibility.MembersOnly }, result.Value.AllowedVisibilities);
                Assert.Equal(2, result.Value.Groups.Count);
            }

            await using (var systemAdmin = CreateServiceScope(database, graph, graph.SystemAdminUserId, SystemRole.SystemAdmin))
            {
                var result = await systemAdmin.Service.GetCreateOptionsAsync(graph.WorkspaceId);
                Assert.False(result.IsSuccess);
                Assert.Equal("NotFound", result.ErrorDetail?.Code);
            }
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "Issue409")]
    public async Task ActivationPermissionBatchFailsClosedAfterMembershipOrWorkspaceRevocation()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithMigratedTemporaryDatabaseAsync(connectionString, async database =>
        {
            var graph = await SeedAuthorityAsync(database, "activation-permission");
            Guid projectId;
            await using (var seed = CreateTenantContext(database, graph))
            {
                var project = new Project
                {
                    TenantId = graph.TenantId,
                    WorkspaceId = graph.WorkspaceId,
                    OwnerUserId = graph.OwnerUserId,
                    CreatedByUserId = graph.OwnerUserId,
                    Name = "Activation permission draft",
                    Slug = $"activation-permission-{Guid.NewGuid():N}",
                    Status = ProjectStatus.Planning,
                    Visibility = ProjectVisibility.MembersOnly,
                    ActivationState = ProjectActivationState.NeverActivated,
                    VersionNo = 1
                };
                projectId = project.Id;
                seed.Projects.Add(project);
                seed.ProjectMembers.AddRange(
                    new ProjectMember
                    {
                        TenantId = graph.TenantId,
                        ProjectId = project.Id,
                        UserId = graph.OwnerUserId,
                        Role = ProjectRole.Owner,
                        JoinedAt = graph.Now
                    },
                    new ProjectMember
                    {
                        TenantId = graph.TenantId,
                        ProjectId = project.Id,
                        UserId = graph.SystemAdminUserId,
                        Role = ProjectRole.Owner,
                        JoinedAt = graph.Now
                    });
                await seed.SaveChangesAsync();
            }

            await using var verification = CreateTenantContext(database, graph);
            var repository = new ProjectRepository(verification);
            Assert.Contains(
                await repository.ListVisibleInWorkspaceAsync(graph.OwnerUserId, graph.WorkspaceId),
                project => project.Id == projectId);
            Assert.Empty(await repository.ListVisibleInWorkspaceAsync(graph.OwnerUserId, Guid.NewGuid()));
            Assert.Equal(
                new[] { projectId },
                await repository.ListActivatableProjectIdsAsync(graph.OwnerUserId, [projectId]));
            Assert.Empty(await repository.ListActivatableProjectIdsAsync(
                graph.SystemAdminUserId,
                [projectId]));

            var membership = await verification.WorkspaceMembers.SingleAsync(member =>
                member.WorkspaceId == graph.WorkspaceId &&
                member.UserId == graph.OwnerUserId);
            membership.Status = MembershipStatus.Suspended;
            await verification.SaveChangesAsync();
            Assert.Empty(await repository.ListActivatableProjectIdsAsync(graph.OwnerUserId, [projectId]));

            membership.Status = MembershipStatus.Active;
            var workspace = await verification.Workspaces.SingleAsync(item => item.Id == graph.WorkspaceId);
            workspace.Status = WorkspaceStatus.Archived;
            await verification.SaveChangesAsync();
            Assert.Empty(await repository.ListActivatableProjectIdsAsync(graph.OwnerUserId, [projectId]));
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task ConcurrentRetryCommitsOneProjectMembershipAuditAndAuthorizationEvent()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithMigratedTemporaryDatabaseAsync(connectionString, async database =>
        {
            var graph = await SeedAuthorityAsync(database, "concurrent");
            await using var first = CreateServiceScope(database, graph, graph.OwnerUserId, SystemRole.NormalUser);
            await using var second = CreateServiceScope(database, graph, graph.OwnerUserId, SystemRole.NormalUser);
            var request = new CanonicalCreateProjectRequest("Concurrent canonical project");

            var results = await Task.WhenAll(
                first.Service.CreateAsync(graph.WorkspaceId, request, "wpc02c-concurrent-create"),
                second.Service.CreateAsync(graph.WorkspaceId, request, "wpc02c-concurrent-create"));

            Assert.All(results, result => Assert.True(result.IsSuccess, result.Error));
            var firstProject = results[0].Value!;
            Assert.Equal(firstProject.Id, results[1].Value!.Id);
            var projectId = firstProject.Id;

            await using var verification = CreateTenantContext(database, graph);
            Assert.Equal(1, await verification.Projects.CountAsync());
            Assert.Equal(1, await verification.ProjectMembers.CountAsync(item => item.ProjectId == projectId));
            Assert.Equal(1, await verification.IdempotencyRecords.CountAsync(item => item.ResourceType == "Project"));
            Assert.Equal(1, await verification.AuditLogs.CountAsync(item =>
                item.Action == "ProjectCreated" && item.EntityId == projectId));
            Assert.Equal(1, await verification.OutboxEvents.CountAsync(item =>
                item.EventType == "Security.AuthorizationStateChanged.v1"));
        });
    }

    private static async Task<AuthorityGraph> SeedAuthorityAsync(
        string connectionString,
        string suffix,
        bool grantProjectCreate = false)
    {
        var currentTenant = new CurrentTenantService();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new AppDbContext(options, currentTenant);
        var runId = Guid.NewGuid().ToString("N");
        var now = new DateTimeOffset(2026, 8, 17, 10, 0, 0, TimeSpan.Zero);

        var tenant = new Tenant
        {
            Name = $"WPC02C Tenant {suffix} {runId}",
            DisplayName = $"WPC02C Tenant {suffix}",
            Slug = $"wpc02c-{suffix}-{runId}",
            Status = TenantStatus.Active
        };
        var owner = NewUser($"owner-{suffix}-{runId}");
        var groupManager = NewUser($"group-{suffix}-{runId}");
        var delegated = NewUser($"delegated-{suffix}-{runId}");
        var systemAdmin = NewUser($"sysadmin-{suffix}-{runId}", SystemRole.SystemAdmin);

        currentTenant.SetPlatformScope();
        db.Tenants.Add(tenant);
        db.Users.AddRange(owner, groupManager, delegated, systemAdmin);
        await db.SaveChangesAsync();

        currentTenant.SetTenant(tenant.Id, tenant.Slug);
        db.TenantUsers.AddRange(
            NewTenantUser(tenant.Id, owner.Id, TenantUserRole.Owner, now),
            NewTenantUser(tenant.Id, groupManager.Id, TenantUserRole.Member, now),
            NewTenantUser(tenant.Id, delegated.Id, TenantUserRole.Member, now),
            NewTenantUser(tenant.Id, systemAdmin.Id, TenantUserRole.Member, now));

        var workspace = new Workspace
        {
            TenantId = tenant.Id,
            Name = $"WPC02C Workspace {suffix}",
            Slug = $"wpc02c-workspace-{suffix}-{runId}",
            CreatedByUserId = owner.Id,
            Status = WorkspaceStatus.Active
        };
        db.Workspaces.Add(workspace);
        db.WorkspaceMembers.AddRange(
            NewWorkspaceMember(tenant.Id, workspace.Id, owner.Id, WorkspaceRole.Owner, now),
            NewWorkspaceMember(tenant.Id, workspace.Id, groupManager.Id, WorkspaceRole.Member, now),
            NewWorkspaceMember(tenant.Id, workspace.Id, delegated.Id, WorkspaceRole.Member, now));

        var group = new Group
        {
            TenantId = tenant.Id,
            WorkspaceId = workspace.Id,
            Name = $"WPC02C Group {suffix}",
            Slug = $"wpc02c-group-{suffix}-{runId}",
            GroupType = GroupType.Team,
            Status = GroupStatus.Active,
            CreatedByUserId = owner.Id
        };
        db.Groups.Add(group);
        db.GroupMembers.Add(new GroupMember
        {
            TenantId = tenant.Id,
            GroupId = group.Id,
            UserId = groupManager.Id,
            Role = GroupRole.Owner,
            JoinedAt = now
        });

        if (grantProjectCreate)
        {
            db.Set<CapabilityGrant>().Add(NewWorkspaceGrant(
                tenant.Id,
                workspace.Id,
                delegated.Id,
                owner.Id,
                CapabilityKeys.ProjectCreate,
                now));
        }

        await db.SaveChangesAsync();
        return new AuthorityGraph(
            tenant.Id,
            tenant.Slug,
            workspace.Id,
            group.Id,
            owner.Id,
            groupManager.Id,
            delegated.Id,
            systemAdmin.Id,
            now);
    }

    private static async Task AddWorkspaceCapabilityAsync(
        string connectionString,
        AuthorityGraph graph,
        Guid subjectUserId,
        string capabilityKey)
    {
        await using var db = CreateTenantContext(connectionString, graph);
        db.Set<CapabilityGrant>().Add(NewWorkspaceGrant(
            graph.TenantId,
            graph.WorkspaceId,
            subjectUserId,
            graph.OwnerUserId,
            capabilityKey,
            graph.Now));
        await db.SaveChangesAsync();
    }

    private static ServiceScope CreateServiceScope(
        string connectionString,
        AuthorityGraph graph,
        Guid userId,
        SystemRole systemRole)
    {
        var currentTenant = new CurrentTenantService();
        currentTenant.SetTenant(graph.TenantId, graph.TenantSlug);
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options;
        var db = new AppDbContext(options, currentTenant);
        var clock = new FixedClock(graph.Now);
        var currentUser = new TestCurrentUser(userId, systemRole);
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
        var authorizationChanges = new AuthorizationStateChangePublisher(
            new TransactionalOutbox(new OutboxEventRepository(db), currentTenant, clock),
            currentTenant,
            clock);
        var service = new CanonicalProjectCreateService(
            projectRepository,
            workspaceRepository,
            groupRepository,
            tenantRepository,
            capabilityEvaluator,
            currentUser,
            currentTenant,
            clock,
            new DbAuditLogger(db, clock, currentUser, currentTenant),
            authorizationChanges,
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

    private static User NewUser(string suffix, SystemRole role = SystemRole.NormalUser) => new()
    {
        DisplayName = $"WPC02C User {suffix}",
        Email = $"{suffix}@example.test".ToLowerInvariant(),
        NormalizedEmail = $"{suffix}@example.test".ToUpperInvariant(),
        Status = UserStatus.Active,
        SystemRole = role
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

    private static CapabilityGrant NewWorkspaceGrant(
        Guid tenantId,
        Guid workspaceId,
        Guid subjectUserId,
        Guid grantedByUserId,
        string capabilityKey,
        DateTimeOffset now) => new()
    {
        TenantId = tenantId,
        SubjectUserId = subjectUserId,
        CapabilityKey = capabilityKey,
        ScopeType = CapabilityScopeType.Workspace,
        ScopeId = workspaceId,
        GrantedByUserId = grantedByUserId,
        GrantedAt = now.AddMinutes(-1),
        ExpiresAt = now.AddHours(1),
        VersionNo = 1
    };

    private sealed record AuthorityGraph(
        Guid TenantId,
        string TenantSlug,
        Guid WorkspaceId,
        Guid GroupId,
        Guid OwnerUserId,
        Guid GroupManagerUserId,
        Guid DelegatedUserId,
        Guid SystemAdminUserId,
        DateTimeOffset Now);

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class TestCurrentUser(Guid userId, SystemRole systemRole) : ICurrentUser
    {
        public Guid? UserId { get; } = userId;
        public Guid? SessionId => null;
        public string? Email => null;
        public SystemRole? SystemRole { get; } = systemRole;
        public bool IsAuthenticated => true;
    }

    private sealed class ServiceScope(AppDbContext db, CanonicalProjectCreateService service) : IAsyncDisposable
    {
        private AppDbContext Db { get; } = db;
        public CanonicalProjectCreateService Service { get; } = service;
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
