using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Realtime;
using Coglatas.Application.Groups;
using Coglatas.Application.Messaging;
using Coglatas.Application.Planning;
using Coglatas.Application.Projects;
using Coglatas.Application.Search;
using Coglatas.Application.Tenancy;
using Coglatas.Application.Workspaces;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Audit;
using Coglatas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Tests.PostgreSql;

public sealed class Wpc01WorkspaceCreationPostgreSqlTests
{
    private const string PreviousMigration = "20260803041347_AddTaskDeadlineDigestLedger";
    private const string Wpc01Migration = "20260813100711_Wpc01WorkspaceCreateIdempotency";

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "WPC01")]
    public async Task MigrationUpgradesAndRollsBackWithoutChangingProjectVisibilitySchema()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database, PreviousMigration);
            var graph = await TaskV1MigrationRawSqlSeed.CreateGraphAsync(database, "wpc01-upgrade");

            Assert.False(await TableExistsAsync(database, "idempotency_records"));
            Assert.False(await ColumnExistsAsync(database, "projects", "Visibility"));

            await PostgreSqlMigrationTestDatabase.MigrateAsync(database, Wpc01Migration);

            Assert.True(await TableExistsAsync(database, "idempotency_records"));
            Assert.True(await IndexExistsAsync(database, "UX_idempotency_tenant_actor_operation_key"));
            Assert.False(await ColumnExistsAsync(database, "projects", "Visibility"));
            Assert.Equal(
                1,
                await PostgreSqlMigrationTestDatabase.ScalarAsync<long>(
                    database,
                    "SELECT COUNT(*) FROM projects WHERE \"Id\" = @id;",
                    ("id", graph.ProjectId)));
            // Current-model pending-migration/model checks belong to the current
            // WPC-02A acceptance suite, not this historical WPC-01 boundary test.

            await PostgreSqlMigrationTestDatabase.MigrateAsync(database, PreviousMigration);
            Assert.False(await TableExistsAsync(database, "idempotency_records"));
            Assert.Equal(
                1,
                await PostgreSqlMigrationTestDatabase.ScalarAsync<long>(
                    database,
                    "SELECT COUNT(*) FROM projects WHERE \"Id\" = @id;",
                    ("id", graph.ProjectId)));

            await PostgreSqlMigrationTestDatabase.MigrateAsync(database, Wpc01Migration);
            Assert.True(await TableExistsAsync(database, "idempotency_records"));
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "WPC01")]
    public async Task CoordinatorSeamConcurrentRetryCommitsOneLogicalWorkspaceAndOneSideEffectSet()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithMigratedTemporaryDatabaseAsync(connectionString, async database =>
        {
            var graph = await SeedAuthorityAsync(database, "concurrent");
            await using var firstScope = CreateServiceScope(database, graph);
            await using var secondScope = CreateServiceScope(database, graph);
            var request = new CreateWorkspaceRequest("Concurrent Workspace", "Created once", null);

            var results = await Task.WhenAll(
                firstScope.Service.CreateAsync(request, "wpc01-concurrent-key"),
                secondScope.Service.CreateAsync(request, "wpc01-concurrent-key"));

            Assert.All(results, result => Assert.True(result.IsSuccess, result.Error));
            var workspaceId = results[0].Value!.Id;
            Assert.Equal(workspaceId, results[1].Value!.Id);

            await using var verification = CreateTenantContext(database, graph.TenantId, graph.TenantSlug);
            Assert.Equal(1, await verification.Workspaces.CountAsync());
            var owner = Assert.Single(await verification.WorkspaceMembers.AsNoTracking().ToListAsync());
            Assert.Equal(workspaceId, owner.WorkspaceId);
            Assert.Equal(graph.UserId, owner.UserId);
            Assert.Equal(WorkspaceRole.Owner, owner.Role);
            Assert.Equal(MembershipStatus.Active, owner.Status);
            Assert.Equal(1, await verification.IdempotencyRecords.CountAsync());
            Assert.Equal(1, await verification.AuditLogs.CountAsync(item => item.Action == "WorkspaceCreated"));
            Assert.Equal(1, await verification.OutboxEvents.CountAsync(item => item.EventType == "Security.AuthorizationStateChanged.v1"));
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "WPC01")]
    public async Task InitializationFailureRollsBackClaimWorkspaceOwnerAuditAndOutbox()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithMigratedTemporaryDatabaseAsync(connectionString, async database =>
        {
            var graph = await SeedAuthorityAsync(database, "rollback");
            var request = new CreateWorkspaceRequest("Rollback Workspace", null, null);

            await using (var failing = CreateServiceScope(
                             database,
                             graph,
                             requiredInitialization: new FailingRequiredInitialization()))
            {
                var failure = await failing.Service.CreateAsync(request, "wpc01-rollback-key");
                Assert.False(failure.IsSuccess);
                Assert.Equal("DependencyUnavailable", failure.ErrorDetail?.Code);
            }

            await AssertCreationCountsAsync(database, graph, expected: 0);

            await using (var retry = CreateServiceScope(database, graph))
            {
                var result = await retry.Service.CreateAsync(request, "wpc01-rollback-key");
                Assert.True(result.IsSuccess, result.Error);
            }

            await AssertCreationCountsAsync(database, graph, expected: 1);
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "WPC01")]
    public async Task UnavailableCanonicalGeneralFailsClosedWithoutCreatingAnyRows()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithMigratedTemporaryDatabaseAsync(connectionString, async database =>
        {
            var graph = await SeedAuthorityAsync(database, "general-gate");
            await using var scope = CreateServiceScope(
                database,
                graph,
                requiredInitialization: new UnavailableWorkspaceRequiredInitialization());

            var capability = await scope.Service.GetCapabilitiesAsync();
            var failure = await scope.Service.CreateAsync(
                new CreateWorkspaceRequest("Unavailable general", null, null),
                "wpc01-general-gate");

            Assert.True(capability.IsSuccess);
            Assert.False(capability.Value!.CanCreate);
            Assert.False(failure.IsSuccess);
            Assert.Equal("DependencyUnavailable", failure.ErrorDetail?.Code);
            await AssertCreationCountsAsync(database, graph, expected: 0);
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "WPC01")]
    public async Task ConcurrentRetryWithUnavailableGeneralLeavesNoCreateSideEffects()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithMigratedTemporaryDatabaseAsync(connectionString, async database =>
        {
            var graph = await SeedAuthorityAsync(database, "general-concurrent-gate");
            await using var first = CreateServiceScope(
                database,
                graph,
                requiredInitialization: new UnavailableWorkspaceRequiredInitialization());
            await using var second = CreateServiceScope(
                database,
                graph,
                requiredInitialization: new UnavailableWorkspaceRequiredInitialization());
            var request = new CreateWorkspaceRequest("Unavailable concurrent general", null, null);

            var results = await Task.WhenAll(
                first.Service.CreateAsync(request, "wpc01-general-concurrent"),
                second.Service.CreateAsync(request, "wpc01-general-concurrent"));

            Assert.All(results, result =>
            {
                Assert.False(result.IsSuccess);
                Assert.Equal("DependencyUnavailable", result.ErrorDetail?.Code);
            });
            await AssertCreationCountsAsync(database, graph, expected: 0);
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "WPC01")]
    public async Task CoordinatorSeamDuplicateDisplayNamesPersistWithDistinctBoundedSlugs()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithMigratedTemporaryDatabaseAsync(connectionString, async database =>
        {
            var graph = await SeedAuthorityAsync(database, "duplicate-slugs");
            await using var scope = CreateServiceScope(database, graph);
            var request = new CreateWorkspaceRequest(new string('A', 160), null, null);

            var first = await scope.Service.CreateAsync(request, "wpc01-duplicate-slug-1");
            scope.Db.ChangeTracker.Clear();
            var second = await scope.Service.CreateAsync(request, "wpc01-duplicate-slug-2");

            Assert.True(first.IsSuccess, first.Error);
            Assert.True(second.IsSuccess, second.Error);
            Assert.NotEqual(first.Value!.Id, second.Value!.Id);
            await using var verification = CreateTenantContext(database, graph.TenantId, graph.TenantSlug);
            var workspaces = await verification.Workspaces.AsNoTracking().ToListAsync();
            Assert.Equal(2, workspaces.Count);
            Assert.All(workspaces, item => Assert.True(item.Slug.Length <= 120));
            Assert.Equal(2, workspaces.Select(item => item.Slug).Distinct(StringComparer.Ordinal).Count());
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "WPC01")]
    public async Task PlanningProjectDiscoveryRequiresExplicitProjectMembership()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithMigratedTemporaryDatabaseAsync(connectionString, async database =>
        {
            var graph = await SeedPlanningAccessGraphAsync(database);
            await using var db = CreateTenantContext(database, graph.TenantId, graph.TenantSlug);
            var users = new UserRepository(db);
            var workspaces = new WorkspaceRepository(db);
            var groups = new GroupRepository(db);
            var projectRepository = new ProjectRepository(db);
            var workspaceAuthorization = new WorkspaceAuthorizationService(
                users,
                workspaces,
                new TenantAuthorizationService(new TenantRepository(db)));
            var groupAuthorization = new GroupAuthorizationService(groups, workspaces, workspaceAuthorization);
            var projectAuthorization = new ProjectAuthorizationService(
                projectRepository,
                workspaceAuthorization,
                groupAuthorization,
                groups);
            var messaging = new MessagingRepository(db);
            var planning = new PlanningRepository(db);

            var invalidProjectChannel = new Conversation
            {
                TenantId = graph.TenantId,
                WorkspaceId = graph.WorkspaceId,
                Type = ConversationType.ProjectChannel,
                ProjectId = null,
                Title = "Invalid unscoped project channel",
                CreatedByUserId = graph.OwnerUserId
            };
            db.AddRange(
                invalidProjectChannel,
                new ConversationMember
                {
                    TenantId = graph.TenantId,
                    ConversationId = invalidProjectChannel.Id,
                    UserId = graph.OwnerUserId,
                    Role = ConversationMemberRole.Admin,
                    JoinedAt = TestClock.Value
                },
                new Message
                {
                    TenantId = graph.TenantId,
                    WorkspaceId = graph.WorkspaceId,
                    ConversationId = invalidProjectChannel.Id,
                    AuthorUserId = graph.OwnerUserId,
                    Body = "Must remain hidden"
                });
            await db.SaveChangesAsync();
            var conversationAuthorization = new ConversationAuthorizationService(messaging, projectAuthorization);

            Assert.False(await projectAuthorization.CanViewProject(graph.OrdinaryUserId, graph.ProjectId));
            Assert.False(await projectAuthorization.CanManageProject(graph.OrdinaryUserId, graph.ProjectId));
            Assert.False(await projectAuthorization.CanViewProject(graph.SystemAdminUserId, graph.ProjectId));
            Assert.True(await projectAuthorization.CanViewProject(graph.OwnerUserId, graph.ProjectId));
            Assert.DoesNotContain(
                await projectRepository.ListVisibleAsync(graph.OrdinaryUserId),
                project => project.Id == graph.ProjectId);
            Assert.DoesNotContain(
                (await messaging.ListForUserAsync(graph.OrdinaryUserId, 1, 20)).Items,
                conversation => conversation.Id == graph.ConversationId);
            Assert.Contains(
                (await messaging.ListForUserAsync(graph.OwnerUserId, 1, 20)).Items,
                conversation => conversation.Id == graph.ConversationId);
            Assert.DoesNotContain(
                (await messaging.ListForUserAsync(graph.OwnerUserId, 1, 20)).Items,
                conversation => conversation.Id == invalidProjectChannel.Id);
            Assert.False(await conversationAuthorization.CanViewConversation(
                graph.OwnerUserId,
                invalidProjectChannel.Id));
            Assert.False(await planning.CanViewMyTasksProjectAsync(graph.OrdinaryUserId, graph.ProjectId));
            Assert.True(await planning.CanViewMyTasksProjectAsync(graph.OwnerUserId, graph.ProjectId));
            Assert.DoesNotContain(
                (await planning.ListMyTasksAsync(
                    graph.OrdinaryUserId,
                    new MyTasksQuery(
                        View: MyTasksRelationshipView.Assigned,
                        WorkspaceId: graph.WorkspaceId,
                        ProjectId: graph.ProjectId),
                    TestClock.Value)).Items,
                item => item.TaskId == graph.TaskId);
            Assert.Contains(
                await projectRepository.ListVisibleAsync(graph.OwnerUserId),
                project => project.Id == graph.ProjectId);

            var ordinarySearch = await new DbSearchService(
                    db,
                    new TestCurrentUser(graph.OrdinaryUserId),
                    messaging)
                .SearchAsync(new SearchRequest(
                    WorkspaceId: graph.WorkspaceId,
                    ProjectId: graph.ProjectId,
                    PageSize: 50));
            var systemAdminSearch = await new DbSearchService(
                    db,
                    new TestCurrentUser(graph.SystemAdminUserId, SystemRole.SystemAdmin),
                    messaging)
                .SearchAsync(new SearchRequest(
                    WorkspaceId: graph.WorkspaceId,
                    ProjectId: graph.ProjectId,
                    PageSize: 50));
            var ownerSearch = await new DbSearchService(
                    db,
                    new TestCurrentUser(graph.OwnerUserId),
                    messaging)
                .SearchAsync(new SearchRequest(
                    WorkspaceId: graph.WorkspaceId,
                    ProjectId: graph.ProjectId,
                    PageSize: 50));

            Assert.True(ordinarySearch.IsSuccess, ordinarySearch.Error);
            Assert.True(systemAdminSearch.IsSuccess, systemAdminSearch.Error);
            Assert.True(ownerSearch.IsSuccess, ownerSearch.Error);
            var protectedIds = new[]
            {
                graph.ProjectId,
                graph.TaskId,
                graph.ArtifactId,
                graph.ActivityLogId,
                graph.CommentId,
                graph.MessageId
            };
            Assert.DoesNotContain(ordinarySearch.Value!.Items, item => protectedIds.Contains(item.Id));
            Assert.DoesNotContain(systemAdminSearch.Value!.Items, item => protectedIds.Contains(item.Id));
            Assert.All(protectedIds, id => Assert.Contains(ownerSearch.Value!.Items, item => item.Id == id));

            var project = await db.Projects.SingleAsync(item => item.Id == graph.ProjectId);
            project.Status = ProjectStatus.Suspended;
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            Assert.False(await projectAuthorization.CanViewProject(graph.OrdinaryUserId, graph.ProjectId));
            Assert.False(await projectAuthorization.CanManageProject(graph.OrdinaryUserId, graph.ProjectId));
            Assert.True(await projectAuthorization.CanViewProject(graph.OwnerUserId, graph.ProjectId));
            Assert.DoesNotContain(
                await projectRepository.ListVisibleAsync(graph.OrdinaryUserId),
                item => item.Id == graph.ProjectId);
            Assert.DoesNotContain(
                (await messaging.ListForUserAsync(graph.OrdinaryUserId, 1, 20)).Items,
                conversation => conversation.Id == graph.ConversationId);
            Assert.False(await planning.CanViewMyTasksProjectAsync(graph.OrdinaryUserId, graph.ProjectId));

            var suspendedSearch = await new DbSearchService(
                    db,
                    new TestCurrentUser(graph.OrdinaryUserId),
                    messaging)
                .SearchAsync(new SearchRequest(
                    WorkspaceId: graph.WorkspaceId,
                    ProjectId: graph.ProjectId,
                    PageSize: 50));
            Assert.True(suspendedSearch.IsSuccess, suspendedSearch.Error);
            Assert.DoesNotContain(suspendedSearch.Value!.Items, item => protectedIds.Contains(item.Id));
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "WPC01")]
    public async Task ActiveGroupedProjectReadBoundaryIsEquivalentAcrossDetailListSearchMessagingAndMyTasks()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithMigratedTemporaryDatabaseAsync(connectionString, async database =>
        {
            var graph = await SeedActiveVisibilityGraphAsync(database);
            await using var db = CreateTenantContext(database, graph.TenantId, graph.TenantSlug);
            var users = new UserRepository(db);
            var workspaces = new WorkspaceRepository(db);
            var groups = new GroupRepository(db);
            var projectRepository = new ProjectRepository(db);
            var workspaceAuthorization = new WorkspaceAuthorizationService(
                users,
                workspaces,
                new TenantAuthorizationService(new TenantRepository(db)));
            var projectAuthorization = new ProjectAuthorizationService(
                projectRepository,
                workspaceAuthorization,
                new GroupAuthorizationService(groups, workspaces, workspaceAuthorization),
                groups);
            var messaging = new MessagingRepository(db);
            var conversationAuthorization = new ConversationAuthorizationService(messaging, projectAuthorization);
            var planning = new PlanningRepository(db);

            var groupedExpectations = new[]
            {
                new ProjectReadExpectation("explicit ProjectMember", graph.ExplicitProjectMemberUserId, SystemRole.NormalUser, true),
                new ProjectReadExpectation("authorized GroupMember", graph.GroupMemberUserId, SystemRole.NormalUser, true),
                new ProjectReadExpectation("Workspace Owner", graph.WorkspaceOwnerUserId, SystemRole.NormalUser, true),
                new ProjectReadExpectation("Workspace Admin", graph.WorkspaceAdminUserId, SystemRole.NormalUser, true),
                new ProjectReadExpectation("ordinary Workspace Member outside Group", graph.OrdinaryWorkspaceMemberUserId, SystemRole.NormalUser, false),
                new ProjectReadExpectation("Workspace Adviser outside Group", graph.AdviserUserId, SystemRole.NormalUser, false),
                new ProjectReadExpectation("Project owner field without policy membership", graph.OwnerFieldOnlyUserId, SystemRole.NormalUser, false),
                new ProjectReadExpectation("active SystemAdmin", graph.SystemAdminUserId, SystemRole.SystemAdmin, true),
                new ProjectReadExpectation("revoked Workspace member with stale memberships", graph.RevokedWorkspaceMemberUserId, SystemRole.NormalUser, false)
            };

            foreach (var expectation in groupedExpectations)
            {
                await AssertProjectReadParityAsync(
                    db,
                    projectAuthorization,
                    projectRepository,
                    messaging,
                    graph.GroupedProject,
                    expectation);
            }

            Assert.Equal(
                groupedExpectations
                    .Where(expectation => expectation.Expected)
                    .Select(expectation => expectation.UserId)
                    .Order()
                    .ToArray(),
                (await projectRepository.ListCurrentReaderUserIdsAsync(graph.GroupedProject.ProjectId))
                    .Order()
                    .ToArray());

            await AssertProjectReadParityAsync(
                db,
                projectAuthorization,
                projectRepository,
                messaging,
                graph.UngroupedProject,
                new ProjectReadExpectation(
                    "ordinary Workspace Member on ungrouped Active Project",
                    graph.OrdinaryWorkspaceMemberUserId,
                    SystemRole.NormalUser,
                    true));

            var resolverTenant = new CurrentTenantService();
            resolverTenant.SetTenant(graph.TenantId, graph.TenantSlug);
            var realtimeResolver = new CurrentAuthorizationTargetResolver(db, resolverTenant);
            var projectEnvelope = new DurableEventEnvelope(
                Guid.NewGuid(),
                "Projects.ProjectChanged.v1",
                RealtimeEventCatalog.PayloadSchemaVersion1,
                TestClock.Value,
                graph.TenantId,
                "Project",
                graph.GroupedProject.ProjectId,
                1,
                RealtimeActor.System(),
                null,
                null,
                System.Text.Json.JsonSerializer.SerializeToElement(new
                {
                    projectId = graph.GroupedProject.ProjectId,
                    workspaceId = graph.WorkspaceId
                }));
            var taskEnvelope = new DurableEventEnvelope(
                Guid.NewGuid(),
                "Projects.TaskChanged.v1",
                RealtimeEventCatalog.PayloadSchemaVersion1,
                TestClock.Value,
                graph.TenantId,
                "Task",
                graph.GroupedProject.TaskId,
                1,
                RealtimeActor.System(),
                null,
                null,
                System.Text.Json.JsonSerializer.SerializeToElement(new
                {
                    taskId = graph.GroupedProject.TaskId,
                    projectId = graph.GroupedProject.ProjectId
                }));
            Assert.False(await realtimeResolver.CanReceiveProjectEventAsync(
                graph.TenantId,
                graph.OrdinaryWorkspaceMemberUserId,
                RealtimeSubscriptionType.Project,
                graph.GroupedProject.ProjectId,
                projectEnvelope));
            Assert.False(await realtimeResolver.CanReceiveTaskEventAsync(
                graph.TenantId,
                graph.OrdinaryWorkspaceMemberUserId,
                RealtimeSubscriptionType.Project,
                graph.GroupedProject.ProjectId,
                taskEnvelope));
            Assert.True(await realtimeResolver.CanReceiveProjectEventAsync(
                graph.TenantId,
                graph.GroupMemberUserId,
                RealtimeSubscriptionType.Project,
                graph.GroupedProject.ProjectId,
                projectEnvelope));
            Assert.True(await realtimeResolver.CanReceiveTaskEventAsync(
                graph.TenantId,
                graph.GroupMemberUserId,
                RealtimeSubscriptionType.Project,
                graph.GroupedProject.ProjectId,
                taskEnvelope));

            Assert.False(await planning.CanViewMyTasksProjectAsync(
                graph.OrdinaryWorkspaceMemberUserId,
                graph.GroupedProject.ProjectId));
            Assert.False(await planning.CanViewMyTasksProjectAsync(
                graph.AdviserUserId,
                graph.GroupedProject.ProjectId));
            Assert.False(await planning.CanViewMyTasksProjectAsync(
                graph.OwnerFieldOnlyUserId,
                graph.GroupedProject.ProjectId));
            Assert.False(await planning.CanViewMyTasksProjectAsync(
                graph.RevokedWorkspaceMemberUserId,
                graph.GroupedProject.ProjectId));
            Assert.True(await planning.CanViewMyTasksProjectAsync(
                graph.OrdinaryWorkspaceMemberUserId,
                graph.UngroupedProject.ProjectId));

            var ordinaryMyTasks = await planning.ListMyTasksAsync(
                graph.OrdinaryWorkspaceMemberUserId,
                new MyTasksQuery(
                    View: MyTasksRelationshipView.Assigned,
                    WorkspaceId: graph.WorkspaceId),
                TestClock.Value);
            Assert.DoesNotContain(ordinaryMyTasks.Items, item => item.TaskId == graph.GroupedProject.TaskId);
            Assert.Contains(ordinaryMyTasks.Items, item => item.TaskId == graph.UngroupedProject.TaskId);

            var adviserMyTasks = await planning.ListMyTasksAsync(
                graph.AdviserUserId,
                new MyTasksQuery(
                    View: MyTasksRelationshipView.Assigned,
                    WorkspaceId: graph.WorkspaceId,
                    ProjectId: graph.GroupedProject.ProjectId),
                TestClock.Value);
            Assert.DoesNotContain(adviserMyTasks.Items, item => item.TaskId == graph.AdviserTaskId);

            var ownerFieldMyTasks = await planning.ListMyTasksAsync(
                graph.OwnerFieldOnlyUserId,
                new MyTasksQuery(
                    View: MyTasksRelationshipView.Assigned,
                    WorkspaceId: graph.WorkspaceId,
                    ProjectId: graph.GroupedProject.ProjectId),
                TestClock.Value);
            Assert.DoesNotContain(ownerFieldMyTasks.Items, item => item.TaskId == graph.OwnerFieldTaskId);

            const string nestedNeedle = "WpcNestedThreadAuthorizationNeedle";
            var firstThread = new Conversation
            {
                TenantId = graph.TenantId,
                WorkspaceId = graph.WorkspaceId,
                ProjectId = graph.GroupedProject.ProjectId,
                Type = ConversationType.Thread,
                ParentConversationId = graph.GroupedProject.ConversationId,
                RootConversationId = graph.GroupedProject.ConversationId,
                Title = "WPC first Project thread",
                CreatedByUserId = graph.WorkspaceOwnerUserId
            };
            var secondThread = new Conversation
            {
                TenantId = graph.TenantId,
                WorkspaceId = graph.WorkspaceId,
                ProjectId = graph.GroupedProject.ProjectId,
                Type = ConversationType.Thread,
                ParentConversationId = firstThread.Id,
                RootConversationId = graph.GroupedProject.ConversationId,
                Title = "WPC second Project thread",
                CreatedByUserId = graph.WorkspaceOwnerUserId
            };
            var nestedThread = new Conversation
            {
                TenantId = graph.TenantId,
                WorkspaceId = graph.WorkspaceId,
                ProjectId = graph.GroupedProject.ProjectId,
                Type = ConversationType.Thread,
                ParentConversationId = secondThread.Id,
                RootConversationId = graph.GroupedProject.ConversationId,
                Title = nestedNeedle,
                CreatedByUserId = graph.WorkspaceOwnerUserId
            };
            var nestedMessage = new Message
            {
                TenantId = graph.TenantId,
                WorkspaceId = graph.WorkspaceId,
                ConversationId = nestedThread.Id,
                AuthorUserId = graph.WorkspaceOwnerUserId,
                Body = $"{nestedNeedle} protected message body"
            };
            db.Conversations.AddRange(firstThread, secondThread, nestedThread);
            db.ConversationMembers.AddRange(
                NewReadableConversationMember(graph, firstThread.Id, graph.ExplicitProjectMemberUserId),
                NewReadableConversationMember(graph, secondThread.Id, graph.ExplicitProjectMemberUserId),
                NewReadableConversationMember(graph, nestedThread.Id, graph.ExplicitProjectMemberUserId));
            db.Messages.Add(nestedMessage);
            await db.SaveChangesAsync();

            Assert.True(await conversationAuthorization.CanViewConversation(
                graph.ExplicitProjectMemberUserId,
                nestedThread.Id));
            var readableNestedPage = await messaging.ListForUserAsync(
                graph.ExplicitProjectMemberUserId,
                1,
                100);
            Assert.Contains(
                readableNestedPage.Items,
                conversation => conversation.Id == nestedThread.Id);
            var readableNestedSearch = await new DbSearchService(
                    db,
                    new TestCurrentUser(graph.ExplicitProjectMemberUserId),
                    messaging)
                .SearchAsync(new SearchRequest(
                    nestedNeedle,
                    SearchResultType.Message,
                    WorkspaceId: graph.WorkspaceId,
                    ProjectId: graph.GroupedProject.ProjectId,
                    PageSize: 50));
            Assert.True(readableNestedSearch.IsSuccess, readableNestedSearch.Error);
            Assert.Contains(readableNestedSearch.Value!.Items, item => item.Id == nestedMessage.Id);

            var revokedAncestorMember = await db.ConversationMembers.SingleAsync(member =>
                member.ConversationId == firstThread.Id &&
                member.UserId == graph.ExplicitProjectMemberUserId);
            revokedAncestorMember.RemovedAt = TestClock.Value;
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            Assert.False(await conversationAuthorization.CanViewConversation(
                graph.ExplicitProjectMemberUserId,
                nestedThread.Id));
            var authorizedConversationPage = await messaging.ListForUserAsync(
                graph.ExplicitProjectMemberUserId,
                1,
                100);
            var authorizedConversationCount = 0;
            foreach (var conversation in authorizedConversationPage.Items)
            {
                if (await conversationAuthorization.CanViewConversation(
                    graph.ExplicitProjectMemberUserId,
                    conversation.Id))
                {
                    authorizedConversationCount++;
                }
            }

            Assert.DoesNotContain(
                authorizedConversationPage.Items,
                conversation => conversation.Id == nestedThread.Id);
            Assert.Equal(authorizedConversationCount, authorizedConversationPage.TotalCount);
            var firstConversationPage = await messaging.ListForUserAsync(
                graph.ExplicitProjectMemberUserId,
                1,
                1);
            var secondConversationPage = await messaging.ListForUserAsync(
                graph.ExplicitProjectMemberUserId,
                2,
                1);
            Assert.Single(firstConversationPage.Items);
            Assert.Equal(1, firstConversationPage.TotalCount);
            Assert.Empty(secondConversationPage.Items);
            Assert.Equal(firstConversationPage.TotalCount, secondConversationPage.TotalCount);

            var nestedSearch = await new DbSearchService(
                    db,
                    new TestCurrentUser(graph.ExplicitProjectMemberUserId),
                    messaging)
                .SearchAsync(new SearchRequest(
                    nestedNeedle,
                    SearchResultType.Message,
                    WorkspaceId: graph.WorkspaceId,
                    ProjectId: graph.GroupedProject.ProjectId,
                    PageSize: 50));
            Assert.True(nestedSearch.IsSuccess, nestedSearch.Error);
            Assert.DoesNotContain(nestedSearch.Value!.Items, item => item.Id == nestedMessage.Id);

            const string depthNeedle = "WpcThreadDepthBoundNeedle";
            var deepThreads = new List<Conversation>();
            var deepMembers = new List<ConversationMember>();
            var parentId = graph.GroupedProject.ConversationId;
            for (var depth = 1; depth <= 33; depth++)
            {
                var thread = new Conversation
                {
                    TenantId = graph.TenantId,
                    WorkspaceId = graph.WorkspaceId,
                    ProjectId = graph.GroupedProject.ProjectId,
                    Type = ConversationType.Thread,
                    ParentConversationId = parentId,
                    RootConversationId = graph.GroupedProject.ConversationId,
                    Title = $"WPC bounded thread {depth}",
                    CreatedByUserId = graph.WorkspaceOwnerUserId
                };
                deepThreads.Add(thread);
                deepMembers.Add(NewReadableConversationMember(
                    graph,
                    thread.Id,
                    graph.ExplicitProjectMemberUserId));
                parentId = thread.Id;
            }

            var overDepthThread = deepThreads[^1];
            var overDepthMessage = new Message
            {
                TenantId = graph.TenantId,
                WorkspaceId = graph.WorkspaceId,
                ConversationId = overDepthThread.Id,
                AuthorUserId = graph.WorkspaceOwnerUserId,
                Body = $"{depthNeedle} protected message body"
            };
            db.Conversations.AddRange(deepThreads);
            db.ConversationMembers.AddRange(deepMembers);
            db.Messages.Add(overDepthMessage);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            Assert.True(await conversationAuthorization.CanCreateThread(
                graph.ExplicitProjectMemberUserId,
                deepThreads[30].Id));
            Assert.False(await conversationAuthorization.CanCreateThread(
                graph.ExplicitProjectMemberUserId,
                deepThreads[31].Id));
            Assert.False(await conversationAuthorization.CanViewConversation(
                graph.ExplicitProjectMemberUserId,
                overDepthThread.Id));
            var boundedConversationPage = await messaging.ListForUserAsync(
                graph.ExplicitProjectMemberUserId,
                1,
                100);
            Assert.DoesNotContain(
                boundedConversationPage.Items,
                conversation => conversation.Id == overDepthThread.Id);
            Assert.Equal(33, boundedConversationPage.TotalCount);

            var depthSearch = await new DbSearchService(
                    db,
                    new TestCurrentUser(graph.ExplicitProjectMemberUserId),
                    messaging)
                .SearchAsync(new SearchRequest(
                    depthNeedle,
                    SearchResultType.Message,
                    WorkspaceId: graph.WorkspaceId,
                    ProjectId: graph.GroupedProject.ProjectId,
                    PageSize: 50));
            Assert.True(depthSearch.IsSuccess, depthSearch.Error);
            Assert.DoesNotContain(depthSearch.Value!.Items, item => item.Id == overDepthMessage.Id);
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "WPC01")]
    public async Task MessageSearchAuthorizesAllMatchingConversationsBeforeDeterministicLimit()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithMigratedTemporaryDatabaseAsync(connectionString, async database =>
        {
            var graph = await SeedActiveVisibilityGraphAsync(database);
            await using var db = CreateTenantContext(database, graph.TenantId, graph.TenantSlug);
            var messaging = new MessagingRepository(db);
            const string needle = "WpcSetBasedMessageSearchNeedle";
            const string unauthorizedTitle = "Unauthorized recursive title marker";
            const string unauthorizedBody = "Unauthorized recursive body marker";
            var baseline = TestClock.Value.AddDays(1);
            var conversations = new List<Conversation>();
            var members = new List<ConversationMember>();
            var messages = new List<Message>();

            for (var index = 0; index < 125; index++)
            {
                var conversation = new Conversation
                {
                    TenantId = graph.TenantId,
                    WorkspaceId = graph.WorkspaceId,
                    Type = ConversationType.DirectMessage,
                    Title = $"{needle} authorized conversation {index:D3}",
                    CreatedByUserId = graph.WorkspaceOwnerUserId,
                    CreatedAt = baseline.AddHours(-2)
                };
                var message = new Message
                {
                    TenantId = graph.TenantId,
                    WorkspaceId = graph.WorkspaceId,
                    ConversationId = conversation.Id,
                    AuthorUserId = graph.GroupMemberUserId,
                    Body = $"{needle} authorized message {index:D3}",
                    CreatedAt = baseline.AddMinutes(-index - 1)
                };
                conversations.Add(conversation);
                members.Add(NewReadableConversationMember(
                    graph,
                    conversation.Id,
                    graph.ExplicitProjectMemberUserId));
                members.Add(NewReadableConversationMember(
                    graph,
                    conversation.Id,
                    graph.GroupMemberUserId));
                messages.Add(message);
            }

            var root = conversations[0];
            var revokedAncestor = new Conversation
            {
                TenantId = graph.TenantId,
                WorkspaceId = graph.WorkspaceId,
                Type = ConversationType.Thread,
                ParentConversationId = root.Id,
                RootConversationId = root.Id,
                Title = $"{needle} revoked ancestor",
                CreatedByUserId = graph.WorkspaceOwnerUserId,
                CreatedAt = baseline.AddHours(-2)
            };
            var readableParent = new Conversation
            {
                TenantId = graph.TenantId,
                WorkspaceId = graph.WorkspaceId,
                Type = ConversationType.Thread,
                ParentConversationId = revokedAncestor.Id,
                RootConversationId = root.Id,
                Title = $"{needle} readable immediate parent",
                CreatedByUserId = graph.WorkspaceOwnerUserId,
                CreatedAt = baseline.AddHours(-2)
            };
            var unauthorizedNestedThread = new Conversation
            {
                TenantId = graph.TenantId,
                WorkspaceId = graph.WorkspaceId,
                Type = ConversationType.Thread,
                ParentConversationId = readableParent.Id,
                RootConversationId = root.Id,
                Title = $"{needle} {unauthorizedTitle}",
                CreatedByUserId = graph.WorkspaceOwnerUserId,
                CreatedAt = baseline.AddHours(-2)
            };
            var revokedMember = NewReadableConversationMember(
                graph,
                revokedAncestor.Id,
                graph.ExplicitProjectMemberUserId);
            revokedMember.RemovedAt = baseline.AddHours(-1);
            var unauthorizedMessage = new Message
            {
                TenantId = graph.TenantId,
                WorkspaceId = graph.WorkspaceId,
                ConversationId = unauthorizedNestedThread.Id,
                AuthorUserId = graph.GroupMemberUserId,
                Body = $"{needle} {unauthorizedBody}",
                CreatedAt = baseline.AddHours(3)
            };

            db.Conversations.AddRange(conversations);
            db.Conversations.AddRange(revokedAncestor, readableParent, unauthorizedNestedThread);
            db.ConversationMembers.AddRange(members);
            db.ConversationMembers.AddRange(
                revokedMember,
                NewReadableConversationMember(graph, readableParent.Id, graph.ExplicitProjectMemberUserId),
                NewReadableConversationMember(graph, unauthorizedNestedThread.Id, graph.ExplicitProjectMemberUserId),
                // Keep author attribution structurally valid so this row is
                // excluded specifically by the reader's revoked ancestry.
                NewReadableConversationMember(graph, unauthorizedNestedThread.Id, graph.GroupMemberUserId));
            db.Messages.AddRange(messages);
            db.Messages.Add(unauthorizedMessage);
            await db.SaveChangesAsync();

            // AppDbContext assigns CreatedAt while inserting. Reapply the deliberately
            // distinct timestamps after persistence so the regression never depends on
            // wall-clock timing or on messages created by the shared visibility fixture.
            for (var index = 0; index < messages.Count; index++)
            {
                messages[index].CreatedAt = baseline.AddMinutes(-index - 1);
            }

            unauthorizedMessage.CreatedAt = baseline.AddHours(3);

            IQueryable<Guid> LegacyCandidateConversationIds()
            {
                var memberConversationIds = db.ConversationMembers
                    .Where(member =>
                        member.UserId == graph.ExplicitProjectMemberUserId &&
                        member.LeftAt == null &&
                        member.RemovedAt == null &&
                        member.CanRead)
                    .Select(member => member.ConversationId);
                return db.Messages
                    .AsNoTracking()
                    .Where(message =>
                        message.DeletedAt == null &&
                        message.AuthorUserId == graph.GroupMemberUserId &&
                        memberConversationIds.Contains(message.ConversationId))
                    .Join(
                        db.Conversations,
                        message => message.ConversationId,
                        conversation => conversation.Id,
                        (message, conversation) => new { message, conversation })
                    .Where(item =>
                        (item.conversation.Type != ConversationType.ProjectChannel ||
                         item.conversation.ProjectId.HasValue) &&
                        (item.conversation.Type != ConversationType.Thread ||
                         item.conversation.ParentConversationId.HasValue &&
                         item.conversation.RootConversationId.HasValue &&
                         db.ConversationMembers.Any(parentMember =>
                             parentMember.ConversationId == item.conversation.ParentConversationId.Value &&
                             parentMember.UserId == graph.ExplicitProjectMemberUserId &&
                             parentMember.LeftAt == null &&
                             parentMember.RemovedAt == null &&
                             parentMember.CanRead) &&
                         db.ConversationMembers.Any(rootMember =>
                             rootMember.ConversationId == item.conversation.RootConversationId.Value &&
                             rootMember.UserId == graph.ExplicitProjectMemberUserId &&
                             rootMember.LeftAt == null &&
                             rootMember.RemovedAt == null &&
                             rootMember.CanRead) &&
                         item.conversation.ParentConversation!.ProjectId == item.conversation.ProjectId &&
                         item.conversation.RootConversation!.ProjectId == item.conversation.ProjectId &&
                         (item.conversation.RootConversation.Type != ConversationType.ProjectChannel ||
                          item.conversation.RootConversation.ProjectId.HasValue)) &&
                        !item.conversation.ProjectId.HasValue &&
                        (EF.Functions.ILike(item.message.Body, $"%{needle}%") ||
                         item.conversation.Title != null &&
                         EF.Functions.ILike(item.conversation.Title, $"%{needle}%")))
                    .Select(item => item.conversation.Id)
                    .Distinct()
                    .OrderBy(conversationId => conversationId);
            }

            var legacyCandidatePopulation = await LegacyCandidateConversationIds().ToListAsync();
            Assert.True(legacyCandidatePopulation.Count > 100);
            Assert.Contains(unauthorizedNestedThread.Id, legacyCandidatePopulation);
            var legacyFirstOneHundred = (await LegacyCandidateConversationIds()
                    .Take(100)
                    .ToListAsync())
                .ToHashSet();
            var newestAuthorizedMessage = messages.First(message =>
                !legacyFirstOneHundred.Contains(message.ConversationId));
            var tiedMessages = messages
                .Where(message => message.Id != newestAuthorizedMessage.Id)
                .Take(2)
                .ToArray();
            newestAuthorizedMessage.CreatedAt = baseline.AddHours(2);
            foreach (var tiedMessage in tiedMessages)
            {
                tiedMessage.CreatedAt = baseline.AddHours(1);
            }

            await db.SaveChangesAsync();
            Assert.DoesNotContain(
                newestAuthorizedMessage.ConversationId,
                await LegacyCandidateConversationIds().Take(100).ToListAsync());
            var tiedMessageIds = tiedMessages.Select(message => message.Id).ToArray();
            var expectedTiedOrder = await db.Messages
                .Where(message => Enumerable.Contains(tiedMessageIds, message.Id))
                .OrderBy(message => message.Id)
                .Select(message => message.Id)
                .ToListAsync();
            db.ChangeTracker.Clear();

            var result = await new DbSearchService(
                    db,
                    new TestCurrentUser(graph.ExplicitProjectMemberUserId),
                    messaging)
                .SearchAsync(new SearchRequest(
                    needle,
                    SearchResultType.Message,
                    WorkspaceId: graph.WorkspaceId,
                    AuthorUserId: graph.GroupMemberUserId,
                    PageSize: 50));

            Assert.True(result.IsSuccess, result.Error);
            var response = result.Value!;
            Assert.Equal(100, response.TotalCount);
            Assert.Equal(newestAuthorizedMessage.Id, response.Items[0].Id);
            Assert.Equal(
                expectedTiedOrder,
                response.Items.Skip(1).Take(2).Select(item => item.Id));
            Assert.All(
                response.Items.Zip(response.Items.Skip(1)),
                pair => Assert.True(pair.First.CreatedAt >= pair.Second.CreatedAt));
            Assert.DoesNotContain(response.Items, item => item.Id == unauthorizedMessage.Id);
            Assert.DoesNotContain(response.Items, item => item.Title.Contains(unauthorizedTitle, StringComparison.Ordinal));
            Assert.DoesNotContain(response.Items, item => item.Snippet?.Contains(unauthorizedBody, StringComparison.Ordinal) == true);
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "WPC01")]
    public async Task ExplicitMemberCanListArchivedHistoryWithoutSearchOrDetailDisclosure()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithMigratedTemporaryDatabaseAsync(connectionString, async database =>
        {
            var graph = await SeedActiveVisibilityGraphAsync(database);
            await using var db = CreateTenantContext(database, graph.TenantId, graph.TenantSlug);
            var users = new UserRepository(db);
            var workspaces = new WorkspaceRepository(db);
            var groups = new GroupRepository(db);
            var projectRepository = new ProjectRepository(db);
            var workspaceAuthorization = new WorkspaceAuthorizationService(
                users,
                workspaces,
                new TenantAuthorizationService(new TenantRepository(db)));
            var projectAuthorization = new ProjectAuthorizationService(
                projectRepository,
                workspaceAuthorization,
                new GroupAuthorizationService(groups, workspaces, workspaceAuthorization),
                groups);
            var messaging = new MessagingRepository(db);

            var project = await db.Projects.SingleAsync(item =>
                item.Id == graph.GroupedProject.ProjectId);
            project.Status = ProjectStatus.Archived;
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            Assert.Contains(
                await projectRepository.ListVisibleAsync(graph.ExplicitProjectMemberUserId),
                item => item.Id == graph.GroupedProject.ProjectId);
            Assert.DoesNotContain(
                await projectRepository.ListVisibleAsync(graph.GroupMemberUserId),
                item => item.Id == graph.GroupedProject.ProjectId);
            Assert.DoesNotContain(
                await projectRepository.ListVisibleAsync(graph.WorkspaceOwnerUserId),
                item => item.Id == graph.GroupedProject.ProjectId);
            Assert.DoesNotContain(
                await projectRepository.ListVisibleAsync(graph.SystemAdminUserId),
                item => item.Id == graph.GroupedProject.ProjectId);
            Assert.DoesNotContain(
                await projectRepository.ListVisibleAsync(graph.RevokedWorkspaceMemberUserId),
                item => item.Id == graph.GroupedProject.ProjectId);
            Assert.Equal(
                [graph.ExplicitProjectMemberUserId],
                await projectRepository.ListCurrentReaderUserIdsAsync(graph.GroupedProject.ProjectId));
            Assert.False(await projectAuthorization.CanViewProject(
                graph.ExplicitProjectMemberUserId,
                graph.GroupedProject.ProjectId));

            var search = await new DbSearchService(
                    db,
                    new TestCurrentUser(graph.ExplicitProjectMemberUserId),
                    messaging)
                .SearchAsync(new SearchRequest(
                    graph.GroupedProject.Needle,
                    WorkspaceId: graph.WorkspaceId,
                    ProjectId: graph.GroupedProject.ProjectId,
                    PageSize: 50));
            Assert.True(search.IsSuccess, search.Error);
            Assert.DoesNotContain(
                search.Value!.Items,
                item => item.Id == graph.GroupedProject.ProjectId ||
                    item.Id == graph.GroupedProject.TaskId ||
                    item.Id == graph.GroupedProject.ArtifactId ||
                    item.Id == graph.GroupedProject.ActivityLogId ||
                    item.Id == graph.GroupedProject.CommentId ||
                    item.Id == graph.GroupedProject.MessageId);
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "WPC01")]
    public async Task RecursiveConversationReadScopeRejectsCyclesAndInconsistentProjectScope()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithMigratedTemporaryDatabaseAsync(connectionString, async database =>
        {
            var graph = await SeedActiveVisibilityGraphAsync(database);
            await using var db = CreateTenantContext(database, graph.TenantId, graph.TenantSlug);
            var users = new UserRepository(db);
            var workspaces = new WorkspaceRepository(db);
            var groups = new GroupRepository(db);
            var projectRepository = new ProjectRepository(db);
            var workspaceAuthorization = new WorkspaceAuthorizationService(
                users,
                workspaces,
                new TenantAuthorizationService(new TenantRepository(db)));
            var projectAuthorization = new ProjectAuthorizationService(
                projectRepository,
                workspaceAuthorization,
                new GroupAuthorizationService(groups, workspaces, workspaceAuthorization),
                groups);
            var messaging = new MessagingRepository(db);
            var conversationAuthorization = new ConversationAuthorizationService(messaging, projectAuthorization);
            var groupedRootMember = await db.ConversationMembers.SingleAsync(member =>
                member.ConversationId == graph.GroupedProject.ConversationId &&
                member.UserId == graph.ExplicitProjectMemberUserId);
            groupedRootMember.CanManageMembers = true;

            var inconsistentThread = new Conversation
            {
                TenantId = graph.TenantId,
                WorkspaceId = graph.WorkspaceId,
                ProjectId = graph.UngroupedProject.ProjectId,
                Type = ConversationType.Thread,
                ParentConversationId = graph.GroupedProject.ConversationId,
                RootConversationId = graph.GroupedProject.ConversationId,
                Title = "WPC inconsistent Project thread",
                CreatedByUserId = graph.ExplicitProjectMemberUserId
            };
            var cycleA = new Conversation
            {
                TenantId = graph.TenantId,
                WorkspaceId = graph.WorkspaceId,
                ProjectId = graph.GroupedProject.ProjectId,
                Type = ConversationType.Thread,
                ParentConversationId = graph.GroupedProject.ConversationId,
                RootConversationId = graph.GroupedProject.ConversationId,
                Title = "WPC cycle A",
                CreatedByUserId = graph.ExplicitProjectMemberUserId
            };
            var cycleB = new Conversation
            {
                TenantId = graph.TenantId,
                WorkspaceId = graph.WorkspaceId,
                ProjectId = graph.GroupedProject.ProjectId,
                Type = ConversationType.Thread,
                ParentConversationId = graph.GroupedProject.ConversationId,
                RootConversationId = graph.GroupedProject.ConversationId,
                Title = "WPC cycle B",
                CreatedByUserId = graph.ExplicitProjectMemberUserId
            };
            var inconsistentMember = NewReadableConversationMember(
                graph,
                inconsistentThread.Id,
                graph.ExplicitProjectMemberUserId);
            var cycleAMember = NewReadableConversationMember(
                graph,
                cycleA.Id,
                graph.ExplicitProjectMemberUserId);
            var cycleBMember = NewReadableConversationMember(
                graph,
                cycleB.Id,
                graph.ExplicitProjectMemberUserId);
            foreach (var member in new[] { inconsistentMember, cycleAMember, cycleBMember })
            {
                member.CanCreateThread = true;
                member.CanManageMembers = true;
            }

            db.Conversations.AddRange(inconsistentThread, cycleA, cycleB);
            db.ConversationMembers.AddRange(inconsistentMember, cycleAMember, cycleBMember);
            await db.SaveChangesAsync();

            cycleA.ParentConversationId = cycleB.Id;
            cycleB.ParentConversationId = cycleA.Id;
            var cycleMessage = new Message
            {
                TenantId = graph.TenantId,
                WorkspaceId = graph.WorkspaceId,
                ConversationId = cycleA.Id,
                AuthorUserId = graph.ExplicitProjectMemberUserId,
                Body = "WpcCycleConversationNeedle protected body"
            };
            var inconsistentMessage = new Message
            {
                TenantId = graph.TenantId,
                WorkspaceId = graph.WorkspaceId,
                ConversationId = inconsistentThread.Id,
                AuthorUserId = graph.ExplicitProjectMemberUserId,
                Body = "WpcInconsistentConversationNeedle protected body"
            };
            db.Messages.AddRange(cycleMessage, inconsistentMessage);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            Assert.False(await conversationAuthorization.CanViewConversation(
                graph.ExplicitProjectMemberUserId,
                inconsistentThread.Id));
            Assert.False(await conversationAuthorization.CanViewConversation(
                graph.ExplicitProjectMemberUserId,
                cycleA.Id));
            foreach (var conversationId in new[] { inconsistentThread.Id, cycleA.Id })
            {
                Assert.False(await conversationAuthorization.CanSendMessage(
                    graph.ExplicitProjectMemberUserId,
                    conversationId));
                Assert.False(await conversationAuthorization.CanModerateConversation(
                    graph.ExplicitProjectMemberUserId,
                    conversationId));
                Assert.False(await conversationAuthorization.CanCreateThread(
                    graph.ExplicitProjectMemberUserId,
                    conversationId));
            }
            var page = await messaging.ListForUserAsync(graph.ExplicitProjectMemberUserId, 1, 100);
            Assert.DoesNotContain(page.Items, item => item.Id == inconsistentThread.Id || item.Id == cycleA.Id);

            foreach (var (needle, messageId, projectId) in new[]
                     {
                         ("WpcCycleConversationNeedle", cycleMessage.Id, graph.GroupedProject.ProjectId),
                         ("WpcInconsistentConversationNeedle", inconsistentMessage.Id, graph.UngroupedProject.ProjectId)
                     })
            {
                var search = await new DbSearchService(
                        db,
                        new TestCurrentUser(graph.ExplicitProjectMemberUserId),
                        messaging)
                    .SearchAsync(new SearchRequest(
                        needle,
                        SearchResultType.Message,
                        WorkspaceId: graph.WorkspaceId,
                        ProjectId: projectId,
                        PageSize: 50));
                Assert.True(search.IsSuccess, search.Error);
                Assert.DoesNotContain(search.Value!.Items, item => item.Id == messageId);
            }
        });
    }

    private static ConversationMember NewReadableConversationMember(
        ActiveVisibilityGraph graph,
        Guid conversationId,
        Guid userId) => new()
    {
        TenantId = graph.TenantId,
        ConversationId = conversationId,
        UserId = userId,
        Role = ConversationMemberRole.Member,
        CanRead = true,
        CanPost = true,
        JoinedAt = TestClock.Value
    };

    private static async Task AssertProjectReadParityAsync(
        AppDbContext db,
        ProjectAuthorizationService projectAuthorization,
        ProjectRepository projectRepository,
        MessagingRepository messaging,
        ProjectResourceGraph project,
        ProjectReadExpectation expectation)
    {
        var canView = await projectAuthorization.CanViewProject(expectation.UserId, project.ProjectId);
        var listed = (await projectRepository.ListVisibleAsync(expectation.UserId))
            .Any(item => item.Id == project.ProjectId);
        var search = await new DbSearchService(
                db,
                new TestCurrentUser(expectation.UserId, expectation.SystemRole),
                messaging)
            .SearchAsync(new SearchRequest(
                project.Needle,
                WorkspaceId: project.WorkspaceId,
                ProjectId: project.ProjectId,
                PageSize: 50));
        Assert.True(search.IsSuccess, $"{expectation.Label}: {search.Error}");

        var searchIds = search.Value!.Items.Select(item => item.Id).ToHashSet();
        var protectedIds = new[]
        {
            project.ProjectId,
            project.TaskId,
            project.ArtifactId,
            project.ActivityLogId,
            project.CommentId,
            project.MessageId
        };
        var conversationVisible = (await messaging.ListForUserAsync(expectation.UserId, 1, 100))
            .Items.Any(item => item.Id == project.ConversationId);

        Assert.Equal(expectation.Expected, canView);
        Assert.Equal(expectation.Expected, listed);
        Assert.Equal(expectation.Expected, conversationVisible);
        foreach (var protectedId in protectedIds)
        {
            Assert.Equal(
                expectation.Expected,
                searchIds.Contains(protectedId));
        }
    }

    private static async Task AssertCreationCountsAsync(
        string connectionString,
        AuthorityGraph graph,
        int expected)
    {
        await using var verification = CreateTenantContext(connectionString, graph.TenantId, graph.TenantSlug);
        Assert.Equal(expected, await verification.Workspaces.CountAsync());
        Assert.Equal(expected, await verification.WorkspaceMembers.CountAsync());
        Assert.Equal(0, await verification.Conversations.CountAsync());
        Assert.Equal(0, await verification.ConversationMembers.CountAsync());
        Assert.Equal(expected, await verification.IdempotencyRecords.CountAsync());
        Assert.Equal(expected, await verification.AuditLogs.CountAsync(item => item.Action == "WorkspaceCreated"));
        Assert.Equal(expected, await verification.OutboxEvents.CountAsync(item => item.EventType == "Security.AuthorizationStateChanged.v1"));
    }

    private static async Task<AuthorityGraph> SeedAuthorityAsync(string connectionString, string suffix)
    {
        await using var context = PostgreSqlMigrationTestDatabase.CreatePlatformContext(connectionString);
        var tenant = new Tenant
        {
            Name = $"WPC Tenant {suffix}",
            DisplayName = $"WPC Tenant {suffix}",
            Slug = $"wpc-{suffix}-{Guid.NewGuid():N}",
            Status = TenantStatus.Active
        };
        var user = new User
        {
            DisplayName = "WPC Owner",
            Email = $"wpc-{suffix}-{Guid.NewGuid():N}@example.test",
            NormalizedEmail = $"WPC-{suffix}-{Guid.NewGuid():N}@EXAMPLE.TEST",
            PasswordHash = "test-hash",
            Status = UserStatus.Active,
            SystemRole = SystemRole.NormalUser
        };
        context.Tenants.Add(tenant);
        context.Users.Add(user);
        context.TenantUsers.Add(new TenantUser
        {
            TenantId = tenant.Id,
            UserId = user.Id,
            Role = TenantUserRole.Owner,
            Status = TenantUserStatus.Active,
            JoinedAt = TestClock.Value
        });
        await context.SaveChangesAsync();
        return new AuthorityGraph(tenant.Id, tenant.Slug, user.Id);
    }

    private static async Task<PlanningAccessGraph> SeedPlanningAccessGraphAsync(string connectionString)
    {
        await using var context = PostgreSqlMigrationTestDatabase.CreatePlatformContext(connectionString);
        var tenant = new Tenant
        {
            Name = "WPC Draft Tenant",
            DisplayName = "WPC Draft Tenant",
            Slug = $"wpc-draft-{Guid.NewGuid():N}",
            Status = TenantStatus.Active
        };
        var owner = NewUser("draft-owner", SystemRole.NormalUser);
        var ordinary = NewUser("draft-ordinary", SystemRole.NormalUser);
        var systemAdmin = NewUser("draft-system-admin", SystemRole.SystemAdmin);
        var workspace = new Workspace
        {
            TenantId = tenant.Id,
            Name = "Draft Workspace",
            Slug = $"draft-workspace-{Guid.NewGuid():N}",
            Status = WorkspaceStatus.Active,
            CreatedByUserId = owner.Id
        };
        var group = new Group
        {
            TenantId = tenant.Id,
            WorkspaceId = workspace.Id,
            Name = "Draft Group",
            Slug = $"draft-group-{Guid.NewGuid():N}",
            Status = GroupStatus.Active,
            CreatedByUserId = owner.Id
        };
        var project = new Project
        {
            TenantId = tenant.Id,
            WorkspaceId = workspace.Id,
            GroupId = group.Id,
            OwnerUserId = owner.Id,
            CreatedByUserId = owner.Id,
            Name = "WPC protected draft",
            Slug = $"wpc-protected-draft-{Guid.NewGuid():N}",
            Status = ProjectStatus.Planning
        };
        var task = new TaskItem
        {
            TenantId = tenant.Id,
            WorkspaceId = workspace.Id,
            ProjectId = project.Id,
            CreatedByUserId = owner.Id,
            PrimaryAssigneeUserId = ordinary.Id,
            Title = "WPC protected draft task"
        };
        var artifact = new Artifact
        {
            TenantId = tenant.Id,
            ProjectId = project.Id,
            TaskItemId = task.Id,
            CreatedByUserId = owner.Id,
            Name = "WPC protected draft artifact"
        };
        var activityLog = new ActivityLog
        {
            TenantId = tenant.Id,
            ProjectId = project.Id,
            TaskItemId = task.Id,
            AuthorUserId = owner.Id,
            ActivityType = ActivityLogType.Note,
            Body = "WPC protected draft activity",
            OccurredAt = TestClock.Value
        };
        var comment = new Comment
        {
            TenantId = tenant.Id,
            WorkspaceId = workspace.Id,
            AuthorUserId = owner.Id,
            TargetType = CommentTargetType.Project,
            TargetId = project.Id,
            Body = "WPC protected draft comment"
        };
        var conversation = new Conversation
        {
            TenantId = tenant.Id,
            WorkspaceId = workspace.Id,
            ProjectId = project.Id,
            Type = ConversationType.ProjectChannel,
            Title = "WPC protected draft conversation",
            CreatedByUserId = owner.Id
        };
        var message = new Message
        {
            TenantId = tenant.Id,
            WorkspaceId = workspace.Id,
            ConversationId = conversation.Id,
            AuthorUserId = owner.Id,
            Body = "WPC protected draft message"
        };
        context.AddRange(
            tenant,
            owner,
            ordinary,
            systemAdmin,
            new TenantUser { TenantId = tenant.Id, UserId = owner.Id, Role = TenantUserRole.Owner, Status = TenantUserStatus.Active, JoinedAt = TestClock.Value },
            new TenantUser { TenantId = tenant.Id, UserId = ordinary.Id, Role = TenantUserRole.Member, Status = TenantUserStatus.Active, JoinedAt = TestClock.Value },
            new TenantUser { TenantId = tenant.Id, UserId = systemAdmin.Id, Role = TenantUserRole.Member, Status = TenantUserStatus.Active, JoinedAt = TestClock.Value },
            workspace,
            new WorkspaceMember { TenantId = tenant.Id, WorkspaceId = workspace.Id, UserId = owner.Id, Role = WorkspaceRole.Owner, Status = MembershipStatus.Active, JoinedAt = TestClock.Value },
            new WorkspaceMember { TenantId = tenant.Id, WorkspaceId = workspace.Id, UserId = ordinary.Id, Role = WorkspaceRole.Admin, Status = MembershipStatus.Active, JoinedAt = TestClock.Value },
            group,
            new GroupMember { TenantId = tenant.Id, GroupId = group.Id, UserId = ordinary.Id, Role = GroupRole.Admin, JoinedAt = TestClock.Value },
            project,
            new ProjectMember { TenantId = tenant.Id, ProjectId = project.Id, UserId = owner.Id, Role = ProjectRole.Owner, JoinedAt = TestClock.Value },
            task,
            artifact,
            activityLog,
            comment,
            conversation,
            new ConversationMember
            {
                TenantId = tenant.Id,
                ConversationId = conversation.Id,
                UserId = owner.Id,
                Role = ConversationMemberRole.Admin,
                JoinedAt = TestClock.Value
            },
            new ConversationMember
            {
                TenantId = tenant.Id,
                ConversationId = conversation.Id,
                UserId = ordinary.Id,
                Role = ConversationMemberRole.Member,
                JoinedAt = TestClock.Value
            },
            message);
        await context.SaveChangesAsync();
        return new PlanningAccessGraph(
            tenant.Id,
            tenant.Slug,
            workspace.Id,
            project.Id,
            task.Id,
            artifact.Id,
            activityLog.Id,
            comment.Id,
            conversation.Id,
            message.Id,
            owner.Id,
            ordinary.Id,
            systemAdmin.Id);
    }

    private static async Task<ActiveVisibilityGraph> SeedActiveVisibilityGraphAsync(string connectionString)
    {
        await using var context = PostgreSqlMigrationTestDatabase.CreatePlatformContext(connectionString);
        var tenant = new Tenant
        {
            Name = "WPC Project visibility tenant",
            DisplayName = "WPC Project visibility tenant",
            Slug = $"wpc-project-visibility-{Guid.NewGuid():N}",
            Status = TenantStatus.Active
        };
        var explicitProjectMember = NewUser("visibility-project-member", SystemRole.NormalUser);
        var groupMember = NewUser("visibility-group-member", SystemRole.NormalUser);
        var workspaceOwner = NewUser("visibility-workspace-owner", SystemRole.NormalUser);
        var workspaceAdmin = NewUser("visibility-workspace-admin", SystemRole.NormalUser);
        var ordinaryWorkspaceMember = NewUser("visibility-ordinary-member", SystemRole.NormalUser);
        var adviser = NewUser("visibility-adviser", SystemRole.NormalUser);
        var ownerFieldOnly = NewUser("visibility-owner-field-only", SystemRole.NormalUser);
        var systemAdmin = NewUser("visibility-system-admin", SystemRole.SystemAdmin);
        var revokedWorkspaceMember = NewUser("visibility-revoked-member", SystemRole.NormalUser);
        var allUsers = new[]
        {
            explicitProjectMember,
            groupMember,
            workspaceOwner,
            workspaceAdmin,
            ordinaryWorkspaceMember,
            adviser,
            ownerFieldOnly,
            systemAdmin,
            revokedWorkspaceMember
        };
        var workspace = new Workspace
        {
            TenantId = tenant.Id,
            Name = "WPC Project visibility workspace",
            Slug = $"wpc-project-visibility-workspace-{Guid.NewGuid():N}",
            Status = WorkspaceStatus.Active,
            CreatedByUserId = workspaceOwner.Id
        };
        var group = new Group
        {
            TenantId = tenant.Id,
            WorkspaceId = workspace.Id,
            Name = "WPC Project visibility group",
            Slug = $"wpc-project-visibility-group-{Guid.NewGuid():N}",
            Status = GroupStatus.Active,
            CreatedByUserId = workspaceOwner.Id
        };

        const string groupedNeedle = "WpcParityGroupedNeedle";
        var groupedProject = NewActiveProjectResourceGraph(
            tenant.Id,
            workspace.Id,
            group.Id,
            ownerFieldOnly.Id,
            workspaceOwner.Id,
            ordinaryWorkspaceMember.Id,
            groupedNeedle);
        const string ungroupedNeedle = "WpcParityUngroupedNeedle";
        var ungroupedProject = NewActiveProjectResourceGraph(
            tenant.Id,
            workspace.Id,
            null,
            workspaceOwner.Id,
            workspaceOwner.Id,
            ordinaryWorkspaceMember.Id,
            ungroupedNeedle);
        var adviserTask = new TaskItem
        {
            TenantId = tenant.Id,
            WorkspaceId = workspace.Id,
            ProjectId = groupedProject.Project.Id,
            PrimaryAssigneeUserId = adviser.Id,
            CreatedByUserId = workspaceOwner.Id,
            Title = $"{groupedNeedle} adviser-only assignment"
        };
        var ownerFieldTask = new TaskItem
        {
            TenantId = tenant.Id,
            WorkspaceId = workspace.Id,
            ProjectId = groupedProject.Project.Id,
            PrimaryAssigneeUserId = ownerFieldOnly.Id,
            CreatedByUserId = workspaceOwner.Id,
            Title = $"{groupedNeedle} owner-field-only assignment"
        };

        context.Add(tenant);
        context.Users.AddRange(allUsers);
        context.TenantUsers.AddRange(allUsers.Select(user => new TenantUser
        {
            TenantId = tenant.Id,
            UserId = user.Id,
            Role = user.Id == workspaceOwner.Id ? TenantUserRole.Owner : TenantUserRole.Member,
            Status = TenantUserStatus.Active,
            JoinedAt = TestClock.Value
        }));
        context.Workspaces.Add(workspace);
        context.WorkspaceMembers.AddRange(
            new WorkspaceMember { TenantId = tenant.Id, WorkspaceId = workspace.Id, UserId = explicitProjectMember.Id, Role = WorkspaceRole.Member, Status = MembershipStatus.Active, JoinedAt = TestClock.Value },
            new WorkspaceMember { TenantId = tenant.Id, WorkspaceId = workspace.Id, UserId = groupMember.Id, Role = WorkspaceRole.Member, Status = MembershipStatus.Active, JoinedAt = TestClock.Value },
            new WorkspaceMember { TenantId = tenant.Id, WorkspaceId = workspace.Id, UserId = workspaceOwner.Id, Role = WorkspaceRole.Owner, Status = MembershipStatus.Active, JoinedAt = TestClock.Value },
            new WorkspaceMember { TenantId = tenant.Id, WorkspaceId = workspace.Id, UserId = workspaceAdmin.Id, Role = WorkspaceRole.Admin, Status = MembershipStatus.Active, JoinedAt = TestClock.Value },
            new WorkspaceMember { TenantId = tenant.Id, WorkspaceId = workspace.Id, UserId = ordinaryWorkspaceMember.Id, Role = WorkspaceRole.Member, Status = MembershipStatus.Active, JoinedAt = TestClock.Value },
            new WorkspaceMember { TenantId = tenant.Id, WorkspaceId = workspace.Id, UserId = adviser.Id, Role = WorkspaceRole.Adviser, Status = MembershipStatus.Active, JoinedAt = TestClock.Value },
            new WorkspaceMember { TenantId = tenant.Id, WorkspaceId = workspace.Id, UserId = ownerFieldOnly.Id, Role = WorkspaceRole.Member, Status = MembershipStatus.Active, JoinedAt = TestClock.Value },
            new WorkspaceMember { TenantId = tenant.Id, WorkspaceId = workspace.Id, UserId = revokedWorkspaceMember.Id, Role = WorkspaceRole.Member, Status = MembershipStatus.Suspended, JoinedAt = TestClock.Value });
        context.Groups.Add(group);
        context.GroupMembers.AddRange(
            new GroupMember { TenantId = tenant.Id, GroupId = group.Id, UserId = groupMember.Id, Role = GroupRole.Member, JoinedAt = TestClock.Value },
            new GroupMember { TenantId = tenant.Id, GroupId = group.Id, UserId = revokedWorkspaceMember.Id, Role = GroupRole.Member, JoinedAt = TestClock.Value });
        context.Projects.AddRange(groupedProject.Project, ungroupedProject.Project);
        context.ProjectMembers.AddRange(
            new ProjectMember { TenantId = tenant.Id, ProjectId = groupedProject.Project.Id, UserId = explicitProjectMember.Id, Role = ProjectRole.Viewer, JoinedAt = TestClock.Value },
            new ProjectMember { TenantId = tenant.Id, ProjectId = groupedProject.Project.Id, UserId = revokedWorkspaceMember.Id, Role = ProjectRole.Viewer, JoinedAt = TestClock.Value });
        context.TaskItems.AddRange(groupedProject.Task, ungroupedProject.Task, adviserTask, ownerFieldTask);
        context.Artifacts.AddRange(groupedProject.Artifact, ungroupedProject.Artifact);
        context.ActivityLogs.AddRange(groupedProject.ActivityLog, ungroupedProject.ActivityLog);
        context.Comments.AddRange(groupedProject.Comment, ungroupedProject.Comment);
        context.Conversations.AddRange(groupedProject.Conversation, ungroupedProject.Conversation);
        context.ConversationMembers.AddRange(allUsers.Select(user => new ConversationMember
        {
            TenantId = tenant.Id,
            ConversationId = groupedProject.Conversation.Id,
            UserId = user.Id,
            Role = ConversationMemberRole.Member,
            CanRead = true,
            JoinedAt = TestClock.Value
        }));
        context.ConversationMembers.Add(new ConversationMember
        {
            TenantId = tenant.Id,
            ConversationId = ungroupedProject.Conversation.Id,
            UserId = ordinaryWorkspaceMember.Id,
            Role = ConversationMemberRole.Member,
            CanRead = true,
            JoinedAt = TestClock.Value
        });
        context.Messages.AddRange(groupedProject.Message, ungroupedProject.Message);
        await context.SaveChangesAsync();

        return new ActiveVisibilityGraph(
            tenant.Id,
            tenant.Slug,
            workspace.Id,
            groupedProject.ToIds(),
            ungroupedProject.ToIds(),
            explicitProjectMember.Id,
            groupMember.Id,
            workspaceOwner.Id,
            workspaceAdmin.Id,
            ordinaryWorkspaceMember.Id,
            adviser.Id,
            ownerFieldOnly.Id,
            systemAdmin.Id,
            revokedWorkspaceMember.Id,
            adviserTask.Id,
            ownerFieldTask.Id);
    }

    private static MutableProjectResourceGraph NewActiveProjectResourceGraph(
        Guid tenantId,
        Guid workspaceId,
        Guid? groupId,
        Guid ownerUserId,
        Guid createdByUserId,
        Guid primaryAssigneeUserId,
        string needle)
    {
        var project = new Project
        {
            TenantId = tenantId,
            WorkspaceId = workspaceId,
            GroupId = groupId,
            OwnerUserId = ownerUserId,
            CreatedByUserId = createdByUserId,
            Name = $"{needle} Project",
            Slug = $"{needle.ToLowerInvariant()}-{Guid.NewGuid():N}",
            Description = $"{needle} protected description",
            Status = ProjectStatus.Active
        };
        var task = new TaskItem
        {
            TenantId = tenantId,
            WorkspaceId = workspaceId,
            ProjectId = project.Id,
            PrimaryAssigneeUserId = primaryAssigneeUserId,
            CreatedByUserId = createdByUserId,
            Title = $"{needle} Task",
            Description = $"{needle} protected task body"
        };
        var artifact = new Artifact
        {
            TenantId = tenantId,
            ProjectId = project.Id,
            TaskItemId = task.Id,
            CreatedByUserId = createdByUserId,
            Name = $"{needle} Artifact",
            Description = $"{needle} protected artifact metadata"
        };
        var activityLog = new ActivityLog
        {
            TenantId = tenantId,
            ProjectId = project.Id,
            TaskItemId = task.Id,
            AuthorUserId = createdByUserId,
            ActivityType = ActivityLogType.Note,
            Body = $"{needle} protected activity",
            OccurredAt = TestClock.Value
        };
        var comment = new Comment
        {
            TenantId = tenantId,
            WorkspaceId = workspaceId,
            AuthorUserId = createdByUserId,
            TargetType = CommentTargetType.Project,
            TargetId = project.Id,
            Body = $"{needle} protected comment"
        };
        var conversation = new Conversation
        {
            TenantId = tenantId,
            WorkspaceId = workspaceId,
            ProjectId = project.Id,
            Type = ConversationType.ProjectChannel,
            Title = $"{needle} Project Channel",
            CreatedByUserId = createdByUserId
        };
        var message = new Message
        {
            TenantId = tenantId,
            WorkspaceId = workspaceId,
            ConversationId = conversation.Id,
            AuthorUserId = createdByUserId,
            Body = $"{needle} protected message"
        };
        return new MutableProjectResourceGraph(
            project,
            task,
            artifact,
            activityLog,
            comment,
            conversation,
            message,
            needle);
    }

    private static User NewUser(string prefix, SystemRole role) => new()
    {
        DisplayName = prefix,
        Email = $"{prefix}-{Guid.NewGuid():N}@example.test",
        NormalizedEmail = $"{prefix}-{Guid.NewGuid():N}@example.test".ToUpperInvariant(),
        PasswordHash = "test-hash",
        Status = UserStatus.Active,
        SystemRole = role
    };

    private static ServiceScope CreateServiceScope(
        string connectionString,
        AuthorityGraph graph,
        IAuthorizationStateChangePublisher? authorizationChanges = null,
        IWorkspaceRequiredInitialization? requiredInitialization = null)
    {
        var currentTenant = new CurrentTenantService();
        currentTenant.SetTenant(graph.TenantId, graph.TenantSlug);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        var db = new AppDbContext(options, currentTenant);
        var users = new UserRepository(db);
        var workspaces = new WorkspaceRepository(db);
        var currentUser = new TestCurrentUser(graph.UserId);
        var clock = new TestClock();
        var publisher = authorizationChanges ?? new AuthorizationStateChangePublisher(
            new TransactionalOutbox(new OutboxEventRepository(db), currentTenant, clock),
            currentTenant,
            clock);
        var service = new WorkspaceService(
            workspaces,
            users,
            new WorkspaceAuthorizationService(
                users,
                workspaces,
                new TenantAuthorizationService(new TenantRepository(db))),
            currentUser,
            clock,
            new DbAuditLogger(db, clock, currentUser, currentTenant),
            new EfUnitOfWork(db),
            currentTenant,
            publisher,
            new EfCreateIdempotencyCoordinator(db),
            requiredInitialization ?? new NoopRequiredInitializationForCoordinatorTests());
        return new ServiceScope(db, service);
    }

    private static AppDbContext CreateTenantContext(
        string connectionString,
        Guid tenantId,
        string tenantSlug)
    {
        var currentTenant = new CurrentTenantService();
        currentTenant.SetTenant(tenantId, tenantSlug);
        return new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options,
            currentTenant);
    }

    private static Task<bool> TableExistsAsync(string connectionString, string table) =>
        PostgreSqlMigrationTestDatabase.ScalarAsync<bool>(
            connectionString,
            "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = @table);",
            ("table", table));

    private static Task<bool> ColumnExistsAsync(string connectionString, string table, string column) =>
        PostgreSqlMigrationTestDatabase.ScalarAsync<bool>(
            connectionString,
            "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @table AND column_name = @column);",
            ("table", table),
            ("column", column));

    private static Task<bool> IndexExistsAsync(string connectionString, string index) =>
        PostgreSqlMigrationTestDatabase.ScalarAsync<bool>(
            connectionString,
            "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'public' AND indexname = @index);",
            ("index", index));

    private sealed record AuthorityGraph(Guid TenantId, string TenantSlug, Guid UserId);

    private sealed record PlanningAccessGraph(
        Guid TenantId,
        string TenantSlug,
        Guid WorkspaceId,
        Guid ProjectId,
        Guid TaskId,
        Guid ArtifactId,
        Guid ActivityLogId,
        Guid CommentId,
        Guid ConversationId,
        Guid MessageId,
        Guid OwnerUserId,
        Guid OrdinaryUserId,
        Guid SystemAdminUserId);

    private sealed record ProjectReadExpectation(
        string Label,
        Guid UserId,
        SystemRole SystemRole,
        bool Expected);

    private sealed record ProjectResourceGraph(
        Guid WorkspaceId,
        Guid ProjectId,
        Guid TaskId,
        Guid ArtifactId,
        Guid ActivityLogId,
        Guid CommentId,
        Guid ConversationId,
        Guid MessageId,
        string Needle);

    private sealed record MutableProjectResourceGraph(
        Project Project,
        TaskItem Task,
        Artifact Artifact,
        ActivityLog ActivityLog,
        Comment Comment,
        Conversation Conversation,
        Message Message,
        string Needle)
    {
        public ProjectResourceGraph ToIds() => new(
            Project.WorkspaceId,
            Project.Id,
            Task.Id,
            Artifact.Id,
            ActivityLog.Id,
            Comment.Id,
            Conversation.Id,
            Message.Id,
            Needle);
    }

    private sealed record ActiveVisibilityGraph(
        Guid TenantId,
        string TenantSlug,
        Guid WorkspaceId,
        ProjectResourceGraph GroupedProject,
        ProjectResourceGraph UngroupedProject,
        Guid ExplicitProjectMemberUserId,
        Guid GroupMemberUserId,
        Guid WorkspaceOwnerUserId,
        Guid WorkspaceAdminUserId,
        Guid OrdinaryWorkspaceMemberUserId,
        Guid AdviserUserId,
        Guid OwnerFieldOnlyUserId,
        Guid SystemAdminUserId,
        Guid RevokedWorkspaceMemberUserId,
        Guid AdviserTaskId,
        Guid OwnerFieldTaskId);

    private sealed record ServiceScope(AppDbContext Db, WorkspaceService Service) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class TestCurrentUser(
        Guid userId,
        SystemRole systemRole = SystemRole.NormalUser) : ICurrentUser
    {
        public Guid? UserId => userId;
        public Guid? SessionId => Guid.NewGuid();
        public string Email => "wpc-owner@example.test";
        public SystemRole? SystemRole => systemRole;
        public bool IsAuthenticated => true;
    }

    private sealed class TestClock : IClock
    {
        public static DateTimeOffset Value { get; } = new(2026, 8, 13, 0, 0, 0, TimeSpan.Zero);
        public DateTimeOffset UtcNow => Value;
    }

    private sealed class NoopRequiredInitializationForCoordinatorTests : IWorkspaceRequiredInitialization
    {
        public bool IsAvailable => true;

        public Task<Result> StageAsync(
            Workspace workspace,
            Guid creatorUserId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());
    }

    private sealed class FailingRequiredInitialization : IWorkspaceRequiredInitialization
    {
        public bool IsAvailable => true;

        public Task<Result> StageAsync(
            Workspace workspace,
            Guid creatorUserId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Failure("Canonical general provisioning failed."));
    }
}
