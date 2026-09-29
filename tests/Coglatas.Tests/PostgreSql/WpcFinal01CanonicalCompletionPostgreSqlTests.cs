using System.Text.Json;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Groups;
using Coglatas.Application.Projects;
using Coglatas.Application.Realtime;
using Coglatas.Application.Tenancy;
using Coglatas.Application.Workspaces;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Audit;
using Coglatas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Tests.PostgreSql;

[Trait("Scope", "WPCFINAL01")]
[Trait("Category", "PostgreSQLIntegration")]
public sealed class WpcFinal01CanonicalCompletionPostgreSqlTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 22, 4, 0, 0, TimeSpan.Zero);

    [PostgreSqlFact]
    public async Task LegacyUnknownVisibilityCanBeExplicitlyClassifiedThenActivated()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            var graph = await SeedProjectGraphAsync(
                database,
                "classify-activate",
                visibility: null,
                status: ProjectStatus.Planning,
                activationState: ProjectActivationState.NeverActivated,
                includeProjectGeneral: false);

            await using (var visibility = CreateVisibilityScope(database, graph, graph.OwnerUserId))
            {
                var result = await visibility.Service.UpdateAsync(
                    graph.ProjectId,
                    new ProjectVisibilityMutationRequest(ProjectVisibility.MembersOnly, 1));
                Assert.True(result.IsSuccess, result.Error);
                Assert.Equal(2, result.Value!.VersionNo);
            }

            await using (var activation = CreateActivationScope(database, graph, graph.OwnerUserId))
            {
                var result = await activation.Service.ActivateAsync(graph.ProjectId, expectedVersion: 2);
                Assert.True(result.IsSuccess, result.Error);
            }

            await using var verify = CreateTenantContext(database, graph);
            var project = await verify.Projects.SingleAsync(item => item.Id == graph.ProjectId);
            Assert.Equal(ProjectVisibility.MembersOnly, project.Visibility);
            Assert.Equal(ProjectStatus.Active, project.Status);
            Assert.Equal(ProjectActivationState.Activated, project.ActivationState);
            Assert.NotNull(project.ActivatedAtUtc);
            Assert.Equal(1, await verify.Conversations.CountAsync(item =>
                item.ProjectId == graph.ProjectId &&
                item.DefaultKind == ConversationDefaultKind.ProjectGeneral));
            Assert.Equal(1, await verify.AuditLogs.CountAsync(item =>
                item.Action == "ProjectVisibilityChanged" && item.EntityId == graph.ProjectId));
            Assert.Equal(1, await verify.AuditLogs.CountAsync(item =>
                item.Action == "ProjectActivated" && item.EntityId == graph.ProjectId));
        });
    }

    [PostgreSqlFact]
    public Task VisibilityClassificationRequiresWorkspaceGovernanceOrCurrentVisibilityGrant() =>
        new WpcFinal01CorrectivePostgreSqlTests()
            .NonDefaultVisibilityMutationRequiresWorkspaceGovernanceOrVisibilityCapability();

    [PostgreSqlFact]
    public async Task ProjectCreateGrantDoesNotAuthorizeVisibilityChange()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            var graph = await SeedProjectGraphAsync(database, "create-not-visibility", ProjectVisibility.MembersOnly);
            await AddCapabilityAsync(database, graph, graph.ReaderUserId, CapabilityKeys.ProjectCreate);

            await using var visibility = CreateVisibilityScope(database, graph, graph.ReaderUserId);
            var result = await visibility.Service.UpdateAsync(
                graph.ProjectId,
                new ProjectVisibilityMutationRequest(ProjectVisibility.WorkspaceVisible, 1));

            Assert.False(result.IsSuccess);
            Assert.Equal("CapabilityDenied", result.ErrorDetail?.Code);
            await using var verify = CreateTenantContext(database, graph);
            Assert.Equal(ProjectVisibility.MembersOnly, (await verify.Projects.SingleAsync()).Visibility);
            Assert.Equal(0, await verify.AuditLogs.CountAsync(item => item.Action == "ProjectVisibilityChanged"));
        });
    }

    [PostgreSqlFact]
    public async Task StaleVisibilityVersionProducesNoMutationAuditOrOutbox()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            var graph = await SeedProjectGraphAsync(database, "stale-visibility", ProjectVisibility.MembersOnly);

            await using var visibility = CreateVisibilityScope(database, graph, graph.OwnerUserId);
            var result = await visibility.Service.UpdateAsync(
                graph.ProjectId,
                new ProjectVisibilityMutationRequest(ProjectVisibility.Restricted, 999));

            Assert.False(result.IsSuccess);
            Assert.Equal("ConcurrentModification", result.ErrorDetail?.Code);
            await using var verify = CreateTenantContext(database, graph);
            var project = await verify.Projects.SingleAsync(item => item.Id == graph.ProjectId);
            Assert.Equal(ProjectVisibility.MembersOnly, project.Visibility);
            Assert.Equal(1, project.VersionNo);
            Assert.Equal(0, await verify.AuditLogs.CountAsync(item => item.Action == "ProjectVisibilityChanged"));
            Assert.Equal(0, await verify.OutboxEvents.CountAsync());
        });
    }

    [PostgreSqlFact]
    public async Task VisibilityChangeAuditOrOutboxFailureRollsBack()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            var graph = await SeedProjectGraphAsync(database, "visibility-outbox-fail", ProjectVisibility.MembersOnly);

            await using var visibility = CreateVisibilityScope(
                database,
                graph,
                graph.OwnerUserId,
                new FailingOutbox());
            var result = await visibility.Service.UpdateAsync(
                graph.ProjectId,
                new ProjectVisibilityMutationRequest(ProjectVisibility.Restricted, 1));

            Assert.False(result.IsSuccess);
            Assert.Equal("DependencyUnavailable", result.ErrorDetail?.Code);
            await using var verify = CreateTenantContext(database, graph);
            var project = await verify.Projects.SingleAsync(item => item.Id == graph.ProjectId);
            Assert.Equal(ProjectVisibility.MembersOnly, project.Visibility);
            Assert.Equal(1, project.VersionNo);
            Assert.Equal(0, await verify.AuditLogs.CountAsync(item => item.Action == "ProjectVisibilityChanged"));
            Assert.Equal(0, await verify.OutboxEvents.CountAsync());
        });
    }

    [PostgreSqlFact]
    public async Task ActivatedProjectMemberAddCreatesProjectGeneralParticipantAtomically()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            var graph = await SeedProjectGraphAsync(
                database,
                "member-add",
                ProjectVisibility.MembersOnly,
                includeReaderProjectMember: false);

            await using var membership = CreateMembershipScope(database, graph, graph.OwnerUserId);
            var result = await membership.Service.AddAsync(
                graph.ProjectId,
                new AddProjectMemberRequest(graph.ReaderUserId, ProjectRole.Contributor));
            Assert.True(result.IsSuccess, result.Error);

            await using var verify = CreateTenantContext(database, graph);
            Assert.True(await verify.ProjectMembers.AnyAsync(item =>
                item.ProjectId == graph.ProjectId && item.UserId == graph.ReaderUserId));
            var participant = await verify.ConversationMembers.SingleAsync(item =>
                item.ConversationId == graph.ProjectGeneralId && item.UserId == graph.ReaderUserId);
            Assert.Equal(ConversationMemberRole.Member, participant.Role);
            Assert.True(participant.CanRead);
            Assert.True(participant.CanPost);
            Assert.Equal(1, await verify.AuditLogs.CountAsync(item => item.Action == "ProjectMemberAdded"));
        });
    }

    [PostgreSqlFact]
    public async Task ProjectMemberViewerDowngradeRemovesProjectGeneralPostRights()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            var graph = await SeedProjectGraphAsync(database, "viewer-downgrade", ProjectVisibility.MembersOnly);

            await using var membership = CreateMembershipScope(database, graph, graph.OwnerUserId);
            var result = await membership.Service.UpdateAsync(
                graph.ProjectId,
                graph.ReaderUserId,
                new UpdateProjectMemberRequest(ProjectRole.Viewer));
            Assert.True(result.IsSuccess, result.Error);

            await using var verify = CreateTenantContext(database, graph);
            var participant = await verify.ConversationMembers.SingleAsync(item =>
                item.ConversationId == graph.ProjectGeneralId && item.UserId == graph.ReaderUserId);
            Assert.Equal(ConversationMemberRole.ReadOnly, participant.Role);
            Assert.True(participant.CanRead);
            Assert.False(participant.CanPost);
            Assert.False(participant.CanCreateThread);
        });
    }

    [PostgreSqlFact]
    public async Task WorkspaceVisibleProjectMemberRemovalKeepsBroadReadButRevokesParticipantRights()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            var graph = await SeedProjectGraphAsync(database, "workspace-visible-remove", ProjectVisibility.WorkspaceVisible);

            await using (var membership = CreateMembershipScope(database, graph, graph.OwnerUserId))
            {
                var result = await membership.Service.RemoveAsync(graph.ProjectId, graph.ReaderUserId);
                Assert.True(result.IsSuccess, result.Error);
            }

            await using var verify = CreateTenantContext(database, graph);
            Assert.False(await verify.ProjectMembers.AnyAsync(item =>
                item.ProjectId == graph.ProjectId && item.UserId == graph.ReaderUserId));
            Assert.True(await verify.VisibleProjectsFor(graph.ReaderUserId).AnyAsync(item => item.Id == graph.ProjectId));
            var participant = await verify.ConversationMembers.SingleAsync(item =>
                item.ConversationId == graph.ProjectGeneralId && item.UserId == graph.ReaderUserId);
            Assert.False(participant.CanRead);
            Assert.False(participant.CanPost);
            Assert.NotNull(participant.RemovedAt);
            Assert.Equal(graph.OwnerUserId, participant.RemovedByUserId);
        });
    }

    [PostgreSqlFact]
    public async Task MembersOnlyProjectMemberRemovalRevokesConversationAndTaskNotificationAccess()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            var graph = await SeedProjectGraphAsync(database, "members-only-remove", ProjectVisibility.MembersOnly);

            await using (var membership = CreateMembershipScope(database, graph, graph.OwnerUserId))
            {
                var result = await membership.Service.RemoveAsync(graph.ProjectId, graph.ReaderUserId);
                Assert.True(result.IsSuccess, result.Error);
            }

            await using var resolverScope = CreateResolverScope(database, graph);
            var resolution = await resolverScope.Resolver.ResolveAsync(
                graph.TenantId,
                graph.ReaderUserId,
                graph.TaskNotificationId);
            Assert.True(resolution.IsOwned);
            Assert.False(resolution.IsAvailable);

            var participant = await resolverScope.Db.ConversationMembers.SingleAsync(item =>
                item.ConversationId == graph.ProjectGeneralId && item.UserId == graph.ReaderUserId);
            Assert.False(participant.CanRead);
            Assert.False(participant.CanPost);
        });
    }

    [PostgreSqlFact]
    public async Task ActivatedProjectMissingProjectGeneralFailsMemberMutationClosed()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            var graph = await SeedProjectGraphAsync(
                database,
                "missing-general",
                ProjectVisibility.MembersOnly,
                includeProjectGeneral: false);

            await using var membership = CreateMembershipScope(database, graph, graph.OwnerUserId);
            var result = await membership.Service.UpdateAsync(
                graph.ProjectId,
                graph.ReaderUserId,
                new UpdateProjectMemberRequest(ProjectRole.Viewer));
            Assert.False(result.IsSuccess);
            Assert.Equal("InvalidProjectGeneral", result.ErrorDetail?.Code);

            await using var verify = CreateTenantContext(database, graph);
            Assert.Equal(
                ProjectRole.Contributor,
                (await verify.ProjectMembers.SingleAsync(item =>
                    item.ProjectId == graph.ProjectId && item.UserId == graph.ReaderUserId)).Role);
            Assert.Equal(0, await verify.AuditLogs.CountAsync(item => item.Action == "ProjectMemberUpdated"));
            Assert.Equal(0, await verify.OutboxEvents.CountAsync());
        });
    }

    [PostgreSqlFact]
    public async Task ArchivedProjectRejectsAddUpdateAndRemoveMemberWithoutSideEffects()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            var graph = await SeedProjectGraphAsync(
                database,
                "archived-member-mutations",
                ProjectVisibility.MembersOnly,
                status: ProjectStatus.Archived,
                activationState: ProjectActivationState.Activated);

            await using var membership = CreateMembershipScope(database, graph, graph.OwnerUserId);
            var add = await membership.Service.AddAsync(
                graph.ProjectId,
                new AddProjectMemberRequest(graph.CandidateUserId, ProjectRole.Contributor));
            var update = await membership.Service.UpdateAsync(
                graph.ProjectId,
                graph.ReaderUserId,
                new UpdateProjectMemberRequest(ProjectRole.Viewer));
            var remove = await membership.Service.RemoveAsync(graph.ProjectId, graph.ReaderUserId);

            Assert.All(new[] { add.ErrorDetail?.Code, update.ErrorDetail?.Code, remove.ErrorDetail?.Code },
                code => Assert.Equal("InvalidStateTransition", code));

            await using var verify = CreateTenantContext(database, graph);
            Assert.False(await verify.ProjectMembers.AnyAsync(item => item.UserId == graph.CandidateUserId));
            Assert.Equal(ProjectRole.Contributor, (await verify.ProjectMembers.SingleAsync(item =>
                item.ProjectId == graph.ProjectId && item.UserId == graph.ReaderUserId)).Role);
            Assert.Equal(0, await verify.AuditLogs.CountAsync(item => item.Action.StartsWith("ProjectMember")));
            Assert.Equal(0, await verify.OutboxEvents.CountAsync());
        });
    }

    [PostgreSqlFact]
    public Task TaskNotificationResolverUsesCanonicalMembersOnlyAndRestrictedVisibility() =>
        new WpcFinal01CorrectivePostgreSqlTests()
            .TaskNotificationAndRealtimeUseCanonicalProjectVisibilityAuthorization();

    [PostgreSqlFact]
    public async Task ProjectRealtimeResolverUsesCanonicalVisibilityScope()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            var graph = await SeedProjectGraphAsync(
                database,
                "group-bound-realtime",
                ProjectVisibility.WorkspaceVisible,
                includeReaderProjectMember: false,
                groupBound: true);

            await using var scope = CreateResolverScope(database, graph);
            var allowed = await scope.Resolver.CanReceiveProjectEventAsync(
                graph.TenantId,
                graph.ReaderUserId,
                RealtimeSubscriptionType.Workspace,
                graph.WorkspaceId,
                NewProjectEvent(graph));
            Assert.True(allowed);
        });
    }

    [PostgreSqlFact]
    public async Task ArchivedWorkspaceCurrentMemberUsesCanonicalHistoricalProjectReadScope()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            var graph = await SeedProjectGraphAsync(
                database,
                "archived-workspace-read",
                ProjectVisibility.WorkspaceVisible,
                includeReaderProjectMember: false,
                workspaceStatus: WorkspaceStatus.Archived);

            await using var scope = CreateResolverScope(database, graph);
            Assert.True(await scope.Db.VisibleProjectsFor(graph.ReaderUserId).AnyAsync(item => item.Id == graph.ProjectId));
            Assert.True(await scope.Resolver.CanReceiveProjectEventAsync(
                graph.TenantId,
                graph.ReaderUserId,
                RealtimeSubscriptionType.Workspace,
                graph.WorkspaceId,
                NewProjectEvent(graph)));
        });
    }

    [PostgreSqlFact]
    public Task ArtifactAndMessageCanonicalNavigationRemainUnchanged() =>
        new Wpc02FNotificationNavigationPostgreSqlTests()
            .ArtifactAndMessageOpenUseCurrentAuthorizedCanonicalNavigationAndUnavailableTargetStaysUnread();

    [PostgreSqlFact]
    public async Task FinalCreateActivateMembershipNotificationFlowCommitsOnlyAuthorizedOutcomes()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            var authority = await SeedWorkspaceOnlyAsync(database, "final-flow");
            await using var scope = CreateFullFlowScope(database, authority);

            var created = await scope.Create.CreateAsync(
                authority.WorkspaceId,
                new CanonicalCreateProjectRequest("Final01 integrated project"),
                "wpc-final01-integrated-flow");
            Assert.True(created.IsSuccess, created.Error);
            var projectId = created.Value!.Id;

            scope.Db.ChangeTracker.Clear();
            var draft = await scope.Db.Projects.SingleAsync(item => item.Id == projectId);
            var activated = await scope.Activation.ActivateAsync(projectId, draft.VersionNo);
            Assert.True(activated.IsSuccess, activated.Error);

            scope.Db.ChangeTracker.Clear();
            var memberAdded = await scope.Membership.AddAsync(
                projectId,
                new AddProjectMemberRequest(authority.ReaderUserId, ProjectRole.Contributor));
            Assert.True(memberAdded.IsSuccess, memberAdded.Error);

            scope.Db.ChangeTracker.Clear();
            var activeProject = await scope.Db.Projects.SingleAsync(item => item.Id == projectId);
            var task = new TaskItem
            {
                TenantId = authority.TenantId,
                WorkspaceId = authority.WorkspaceId,
                ProjectId = projectId,
                Title = "Final01 authorized task",
                Status = TaskItemStatus.NotStarted,
                Priority = TaskPriority.Medium,
                VersionNo = 1,
                CreatedByUserId = authority.OwnerUserId
            };
            var notification = new Notification
            {
                TenantId = authority.TenantId,
                UserId = authority.ReaderUserId,
                NotificationType = NotificationType.TaskAssigned,
                Title = "Task assigned",
                RelatedEntityType = "Task",
                RelatedEntityId = task.Id,
                CreatedAt = Now,
                StateVersion = 1
            };
            scope.Db.AddRange(task, notification);
            await scope.Db.SaveChangesAsync();

            var beforeRemoval = await scope.Resolver.ResolveAsync(
                authority.TenantId,
                authority.ReaderUserId,
                notification.Id);
            Assert.True(beforeRemoval.IsAvailable);
            Assert.Equal(authority.WorkspaceId, beforeRemoval.WorkspaceId);

            var removed = await scope.Membership.RemoveAsync(projectId, authority.ReaderUserId);
            Assert.True(removed.IsSuccess, removed.Error);

            scope.Db.ChangeTracker.Clear();
            var afterRemoval = await scope.Resolver.ResolveAsync(
                authority.TenantId,
                authority.ReaderUserId,
                notification.Id);
            Assert.True(afterRemoval.IsOwned);
            Assert.False(afterRemoval.IsAvailable);

            Assert.Equal(ProjectStatus.Active, activeProject.Status);
            Assert.Equal(1, await scope.Db.Conversations.CountAsync(item =>
                item.ProjectId == projectId && item.DefaultKind == ConversationDefaultKind.ProjectGeneral));
            var participant = await scope.Db.ConversationMembers.SingleAsync(item =>
                item.UserId == authority.ReaderUserId && item.Conversation!.ProjectId == projectId);
            Assert.False(participant.CanRead);
            Assert.False(participant.CanPost);
            Assert.Equal(1, await scope.Db.AuditLogs.CountAsync(item => item.Action == "ProjectCreated"));
            Assert.Equal(1, await scope.Db.AuditLogs.CountAsync(item => item.Action == "ProjectActivated"));
            Assert.Equal(1, await scope.Db.AuditLogs.CountAsync(item => item.Action == "ProjectMemberAdded"));
            Assert.Equal(1, await scope.Db.AuditLogs.CountAsync(item => item.Action == "ProjectMemberRemoved"));
        });
    }

    private static async Task<ProjectGraph> SeedProjectGraphAsync(
        string database,
        string suffix,
        ProjectVisibility? visibility,
        ProjectStatus status = ProjectStatus.Active,
        ProjectActivationState activationState = ProjectActivationState.Activated,
        bool includeProjectGeneral = true,
        bool includeReaderProjectMember = true,
        bool groupBound = false,
        WorkspaceStatus workspaceStatus = WorkspaceStatus.Active)
    {
        var authority = await SeedWorkspaceOnlyAsync(database, suffix, workspaceStatus);
        await using var db = CreateTenantContext(database, authority);

        Group? group = null;
        if (groupBound)
        {
            group = new Group
            {
                TenantId = authority.TenantId,
                WorkspaceId = authority.WorkspaceId,
                Name = $"Final01 Group {suffix}",
                Slug = $"final01-group-{suffix}-{Guid.NewGuid():N}",
                GroupType = GroupType.Team,
                Status = GroupStatus.Active,
                CreatedByUserId = authority.OwnerUserId
            };
            db.Groups.Add(group);
        }

        var activated = activationState == ProjectActivationState.Activated;
        var project = new Project
        {
            TenantId = authority.TenantId,
            WorkspaceId = authority.WorkspaceId,
            GroupId = group?.Id,
            OwnerUserId = authority.OwnerUserId,
            Name = $"Final01 Project {suffix}",
            Slug = $"final01-project-{suffix}-{Guid.NewGuid():N}",
            Status = status,
            Visibility = visibility,
            ActivationState = activationState,
            ActivatedAtUtc = activated ? Now : null,
            ActivationVersion = activated ? 1 : null,
            ArchivedFromStatus = status == ProjectStatus.Archived ? ProjectStatus.Active : null,
            VersionNo = 1,
            CreatedByUserId = authority.OwnerUserId
        };
        db.Projects.Add(project);
        db.ProjectMembers.Add(new ProjectMember
        {
            TenantId = authority.TenantId,
            ProjectId = project.Id,
            UserId = authority.OwnerUserId,
            Role = ProjectRole.Owner,
            JoinedAt = Now
        });
        if (includeReaderProjectMember)
        {
            db.ProjectMembers.Add(new ProjectMember
            {
                TenantId = authority.TenantId,
                ProjectId = project.Id,
                UserId = authority.ReaderUserId,
                Role = ProjectRole.Contributor,
                JoinedAt = Now
            });
        }

        Conversation? general = null;
        if (includeProjectGeneral)
        {
            general = new Conversation
            {
                TenantId = authority.TenantId,
                WorkspaceId = authority.WorkspaceId,
                ProjectId = project.Id,
                Type = ConversationType.ProjectChannel,
                Title = "general",
                Visibility = ConversationVisibility.PublicWithinScope,
                DefaultKind = ConversationDefaultKind.ProjectGeneral,
                CreatedByUserId = authority.OwnerUserId
            };
            db.Conversations.Add(general);
            db.ConversationMembers.Add(new ConversationMember
            {
                TenantId = authority.TenantId,
                ConversationId = general.Id,
                UserId = authority.OwnerUserId,
                Role = ConversationMemberRole.Admin,
                CanRead = true,
                CanPost = true,
                CanManageMembers = true,
                CanCreateThread = true,
                JoinedAt = Now
            });
            if (includeReaderProjectMember)
            {
                db.ConversationMembers.Add(new ConversationMember
                {
                    TenantId = authority.TenantId,
                    ConversationId = general.Id,
                    UserId = authority.ReaderUserId,
                    Role = ConversationMemberRole.Member,
                    CanRead = true,
                    CanPost = true,
                    CanManageMembers = false,
                    CanCreateThread = true,
                    JoinedAt = Now
                });
            }
        }

        var taskId = Guid.Empty;
        var taskNotificationId = Guid.Empty;
        var operationalProject =
            activationState == ProjectActivationState.Activated &&
            status is ProjectStatus.Active or ProjectStatus.Review;
        if (operationalProject)
        {
            var task = new TaskItem
            {
                TenantId = authority.TenantId,
                WorkspaceId = authority.WorkspaceId,
                ProjectId = project.Id,
                Title = $"Final01 Task {suffix}",
                Status = TaskItemStatus.NotStarted,
                Priority = TaskPriority.Medium,
                VersionNo = 1,
                CreatedByUserId = authority.OwnerUserId
            };
            var notification = new Notification
            {
                TenantId = authority.TenantId,
                UserId = authority.ReaderUserId,
                NotificationType = NotificationType.TaskAssigned,
                Title = "Task assigned",
                RelatedEntityType = "Task",
                RelatedEntityId = task.Id,
                CreatedAt = Now,
                StateVersion = 1
            };
            db.AddRange(task, notification);
            taskId = task.Id;
            taskNotificationId = notification.Id;
        }
        await db.SaveChangesAsync();

        return new ProjectGraph(
            authority.TenantId,
            authority.TenantSlug,
            authority.WorkspaceId,
            project.Id,
            taskId,
            taskNotificationId,
            general?.Id ?? Guid.Empty,
            authority.OwnerUserId,
            authority.ReaderUserId,
            authority.CandidateUserId);
    }

    private static async Task<AuthorityGraph> SeedWorkspaceOnlyAsync(
        string database,
        string suffix,
        WorkspaceStatus workspaceStatus = WorkspaceStatus.Active)
    {
        var run = Guid.NewGuid().ToString("N");
        var tenant = new Tenant
        {
            Name = $"Final01 Tenant {suffix} {run}",
            DisplayName = $"Final01 Tenant {suffix}",
            Slug = $"final01-{suffix}-{run}",
            Status = TenantStatus.Active
        };
        var owner = NewUser($"owner-{suffix}-{run}");
        var reader = NewUser($"reader-{suffix}-{run}");
        var candidate = NewUser($"candidate-{suffix}-{run}");

        await using (var platform = PostgreSqlMigrationTestDatabase.CreatePlatformContext(database))
        {
            platform.AddRange(tenant, owner, reader, candidate);
            await platform.SaveChangesAsync();
        }

        var authority = new AuthorityGraph(
            tenant.Id,
            tenant.Slug,
            Guid.NewGuid(),
            owner.Id,
            reader.Id,
            candidate.Id);
        await using var db = CreateTenantContext(database, authority);
        db.TenantUsers.AddRange(
            NewTenantUser(tenant.Id, owner.Id, TenantUserRole.Owner),
            NewTenantUser(tenant.Id, reader.Id, TenantUserRole.Member),
            NewTenantUser(tenant.Id, candidate.Id, TenantUserRole.Member));
        var workspace = new Workspace
        {
            Id = authority.WorkspaceId,
            TenantId = tenant.Id,
            Name = $"Final01 Workspace {suffix}",
            Slug = $"final01-workspace-{suffix}-{run}",
            Status = workspaceStatus,
            CreatedByUserId = owner.Id
        };
        db.Workspaces.Add(workspace);
        db.WorkspaceMembers.AddRange(
            NewWorkspaceMember(tenant.Id, workspace.Id, owner.Id, WorkspaceRole.Owner),
            NewWorkspaceMember(tenant.Id, workspace.Id, reader.Id, WorkspaceRole.Member),
            NewWorkspaceMember(tenant.Id, workspace.Id, candidate.Id, WorkspaceRole.Member));
        await db.SaveChangesAsync();
        return authority;
    }

    private static MembershipScope CreateMembershipScope(string database, ProjectGraph graph, Guid actorUserId)
    {
        var currentTenant = TenantScope(graph);
        var db = new AppDbContext(Options(database), currentTenant);
        var clock = new FixedClock(Now);
        var currentUser = new TestCurrentUser(actorUserId);
        var projects = new ProjectRepository(db);
        var workspaces = new WorkspaceRepository(db);
        var groups = new GroupRepository(db);
        var users = new UserRepository(db);
        var workspaceAuthorization = new WorkspaceAuthorizationService(users, workspaces);
        var groupAuthorization = new GroupAuthorizationService(groups, workspaces, workspaceAuthorization);
        var projectAuthorization = new ProjectAuthorizationService(
            projects,
            workspaceAuthorization,
            groupAuthorization,
            groups);
        var outbox = new TransactionalOutbox(new OutboxEventRepository(db), currentTenant, clock);
        var authorizationChanges = new AuthorizationStateChangePublisher(outbox, currentTenant, clock);
        var service = new ProjectMembershipService(
            projects,
            workspaces,
            groups,
            users,
            projectAuthorization,
            new ProjectGeneralMembershipSynchronizer(
                new DefaultConversationStore(db),
                currentTenant,
                clock,
                authorizationChanges),
            currentUser,
            clock,
            new DbAuditLogger(db, clock, currentUser, currentTenant),
            new BusinessInvalidationPublisher(outbox, currentTenant, clock),
            authorizationChanges,
            new EfUnitOfWork(db));
        return new MembershipScope(db, service);
    }

    private static VisibilityScope CreateVisibilityScope(
        string database,
        ProjectGraph graph,
        Guid actorUserId,
        ITransactionalOutbox? outboxOverride = null)
    {
        var currentTenant = TenantScope(graph);
        var db = new AppDbContext(Options(database), currentTenant);
        var clock = new FixedClock(Now);
        var currentUser = new TestCurrentUser(actorUserId);
        var projects = new ProjectRepository(db);
        var workspaces = new WorkspaceRepository(db);
        var tenants = new TenantRepository(db);
        var outbox = outboxOverride ?? new TransactionalOutbox(new OutboxEventRepository(db), currentTenant, clock);
        var capability = new CapabilityGrantEvaluator(
            new CapabilityGrantRepository(db),
            tenants,
            workspaces,
            currentTenant,
            clock);
        var service = new ProjectVisibilityService(
            projects,
            workspaces,
            capability,
            currentUser,
            currentTenant,
            new DbAuditLogger(db, clock, currentUser, currentTenant),
            new BusinessInvalidationPublisher(outbox, currentTenant, clock),
            new AuthorizationStateChangePublisher(outbox, currentTenant, clock),
            new EfUnitOfWork(db));
        return new VisibilityScope(db, service);
    }

    private static ActivationScope CreateActivationScope(string database, ProjectGraph graph, Guid actorUserId)
    {
        var currentTenant = TenantScope(graph);
        var db = new AppDbContext(Options(database), currentTenant);
        var clock = new FixedClock(Now);
        var currentUser = new TestCurrentUser(actorUserId);
        var projects = new ProjectRepository(db);
        var workspaces = new WorkspaceRepository(db);
        var groups = new GroupRepository(db);
        var users = new UserRepository(db);
        var workspaceAuthorization = new WorkspaceAuthorizationService(users, workspaces);
        var groupAuthorization = new GroupAuthorizationService(groups, workspaces, workspaceAuthorization);
        var projectAuthorization = new ProjectAuthorizationService(projects, workspaceAuthorization, groupAuthorization, groups);
        var outbox = new TransactionalOutbox(new OutboxEventRepository(db), currentTenant, clock);
        var authorizationChanges = new AuthorizationStateChangePublisher(outbox, currentTenant, clock);
        var service = new ProjectActivationService(
            projects,
            workspaces,
            new TenantRepository(db),
            projectAuthorization,
            new ProjectGeneralActivationProvisioner(
                new DefaultConversationStore(db),
                projects,
                currentTenant,
                clock,
                authorizationChanges),
            new ProjectTaskWorkflowActivationProvisioner(
                new ProjectActivationWorkflowStore(db),
                new ProjectTaskWorkflowResolver(new NoConfiguredProjectTaskWorkflowSource())),
            new ProjectActivationUnitOfWork(db),
            currentUser,
            currentTenant,
            clock,
            new DbAuditLogger(db, clock, currentUser, currentTenant),
            new BusinessInvalidationPublisher(outbox, currentTenant, clock));
        return new ActivationScope(db, service);
    }

    private static ResolverScope CreateResolverScope(string database, ProjectGraph graph)
    {
        var currentTenant = TenantScope(graph);
        var db = new AppDbContext(Options(database), currentTenant);
        var inner = new CurrentAuthorizationTargetResolver(db, currentTenant, new MessagingRepository(db));
        return new ResolverScope(
            db,
            new CanonicalCurrentAuthorizationTargetResolver(db, currentTenant, inner));
    }

    private static FullFlowScope CreateFullFlowScope(string database, AuthorityGraph authority)
    {
        var currentTenant = TenantScope(authority);
        var db = new AppDbContext(Options(database), currentTenant);
        var clock = new FixedClock(Now);
        var currentUser = new TestCurrentUser(authority.OwnerUserId);
        var projects = new ProjectRepository(db);
        var workspaces = new WorkspaceRepository(db);
        var groups = new GroupRepository(db);
        var users = new UserRepository(db);
        var tenants = new TenantRepository(db);
        var workspaceAuthorization = new WorkspaceAuthorizationService(users, workspaces);
        var groupAuthorization = new GroupAuthorizationService(groups, workspaces, workspaceAuthorization);
        var projectAuthorization = new ProjectAuthorizationService(projects, workspaceAuthorization, groupAuthorization, groups);
        var outbox = new TransactionalOutbox(new OutboxEventRepository(db), currentTenant, clock);
        var authorizationChanges = new AuthorizationStateChangePublisher(outbox, currentTenant, clock);
        var capability = new CapabilityGrantEvaluator(
            new CapabilityGrantRepository(db),
            tenants,
            workspaces,
            currentTenant,
            clock);
        var audit = new DbAuditLogger(db, clock, currentUser, currentTenant);
        var business = new BusinessInvalidationPublisher(outbox, currentTenant, clock);
        var create = new CanonicalProjectCreateService(
            projects,
            workspaces,
            groups,
            tenants,
            capability,
            currentUser,
            currentTenant,
            clock,
            audit,
            authorizationChanges,
            new EfCreateIdempotencyCoordinator(db));
        var activation = new ProjectActivationService(
            projects,
            workspaces,
            tenants,
            projectAuthorization,
            new ProjectGeneralActivationProvisioner(
                new DefaultConversationStore(db),
                projects,
                currentTenant,
                clock,
                authorizationChanges),
            new ProjectTaskWorkflowActivationProvisioner(
                new ProjectActivationWorkflowStore(db),
                new ProjectTaskWorkflowResolver(new NoConfiguredProjectTaskWorkflowSource())),
            new ProjectActivationUnitOfWork(db),
            currentUser,
            currentTenant,
            clock,
            audit,
            business);
        var membership = new ProjectMembershipService(
            projects,
            workspaces,
            groups,
            users,
            projectAuthorization,
            new ProjectGeneralMembershipSynchronizer(
                new DefaultConversationStore(db),
                currentTenant,
                clock,
                authorizationChanges),
            currentUser,
            clock,
            audit,
            business,
            authorizationChanges,
            new EfUnitOfWork(db));
        var inner = new CurrentAuthorizationTargetResolver(db, currentTenant, new MessagingRepository(db));
        var resolver = new CanonicalCurrentAuthorizationTargetResolver(db, currentTenant, inner);
        return new FullFlowScope(db, create, activation, membership, resolver);
    }

    private static async Task AddCapabilityAsync(
        string database,
        ProjectGraph graph,
        Guid subjectUserId,
        string capabilityKey)
    {
        await using var db = CreateTenantContext(database, graph);
        db.Set<CapabilityGrant>().Add(new CapabilityGrant
        {
            TenantId = graph.TenantId,
            SubjectUserId = subjectUserId,
            CapabilityKey = capabilityKey,
            ScopeType = CapabilityScopeType.Workspace,
            ScopeId = graph.WorkspaceId,
            GrantedByUserId = graph.OwnerUserId,
            GrantedAt = Now.AddMinutes(-1),
            ExpiresAt = Now.AddHours(1),
            VersionNo = 1
        });
        await db.SaveChangesAsync();
    }

    private static DurableEventEnvelope NewProjectEvent(ProjectGraph graph) => new(
        Guid.NewGuid(),
        "Projects.ProjectChanged.v1",
        RealtimeEventCatalog.PayloadSchemaVersion1,
        Now,
        graph.TenantId,
        "Project",
        graph.ProjectId,
        1,
        RealtimeActor.System(),
        null,
        null,
        JsonSerializer.SerializeToElement(new
        {
            workspaceId = graph.WorkspaceId,
            projectId = graph.ProjectId,
            requiresRefetch = true
        }));

    private static AppDbContext CreateTenantContext(string database, ProjectGraph graph) =>
        new(Options(database), TenantScope(graph));

    private static AppDbContext CreateTenantContext(string database, AuthorityGraph graph) =>
        new(Options(database), TenantScope(graph));

    private static DbContextOptions<AppDbContext> Options(string database) =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(database)
            .AddInterceptors(new ProjectGovernanceSaveChangesInterceptor())
            .Options;

    private static CurrentTenantService TenantScope(ProjectGraph graph)
    {
        var currentTenant = new CurrentTenantService();
        currentTenant.SetTenant(graph.TenantId, graph.TenantSlug);
        return currentTenant;
    }

    private static CurrentTenantService TenantScope(AuthorityGraph graph)
    {
        var currentTenant = new CurrentTenantService();
        currentTenant.SetTenant(graph.TenantId, graph.TenantSlug);
        return currentTenant;
    }

    private static User NewUser(string suffix) => new()
    {
        DisplayName = $"Final01 User {suffix}",
        Email = $"{suffix}@example.test".ToLowerInvariant(),
        NormalizedEmail = $"{suffix}@example.test".ToUpperInvariant(),
        PasswordHash = "hash",
        Status = UserStatus.Active,
        SystemRole = SystemRole.NormalUser
    };

    private static TenantUser NewTenantUser(Guid tenantId, Guid userId, TenantUserRole role) => new()
    {
        TenantId = tenantId,
        UserId = userId,
        Role = role,
        Status = TenantUserStatus.Active,
        JoinedAt = Now
    };

    private static WorkspaceMember NewWorkspaceMember(Guid tenantId, Guid workspaceId, Guid userId, WorkspaceRole role) => new()
    {
        TenantId = tenantId,
        WorkspaceId = workspaceId,
        UserId = userId,
        Role = role,
        Status = MembershipStatus.Active,
        JoinedAt = Now
    };

    private sealed record AuthorityGraph(
        Guid TenantId,
        string TenantSlug,
        Guid WorkspaceId,
        Guid OwnerUserId,
        Guid ReaderUserId,
        Guid CandidateUserId);

    private sealed record ProjectGraph(
        Guid TenantId,
        string TenantSlug,
        Guid WorkspaceId,
        Guid ProjectId,
        Guid TaskId,
        Guid TaskNotificationId,
        Guid ProjectGeneralId,
        Guid OwnerUserId,
        Guid ReaderUserId,
        Guid CandidateUserId);

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

    private sealed class FailingOutbox : ITransactionalOutbox
    {
        public Task<Result<Guid>> EnqueueAsync(
            DurableEventEnvelope envelope,
            IReadOnlyCollection<RealtimeRoutingTarget> routingTargets,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<Guid>.Failure("forced-final01-outbox-failure"));
    }

    private sealed class MembershipScope(AppDbContext db, ProjectMembershipService service) : IAsyncDisposable
    {
        private AppDbContext Db { get; } = db;
        public ProjectMembershipService Service { get; } = service;
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class VisibilityScope(AppDbContext db, ProjectVisibilityService service) : IAsyncDisposable
    {
        private AppDbContext Db { get; } = db;
        public ProjectVisibilityService Service { get; } = service;
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class ActivationScope(AppDbContext db, ProjectActivationService service) : IAsyncDisposable
    {
        private AppDbContext Db { get; } = db;
        public ProjectActivationService Service { get; } = service;
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class ResolverScope(AppDbContext db, CanonicalCurrentAuthorizationTargetResolver resolver) : IAsyncDisposable
    {
        public AppDbContext Db { get; } = db;
        public CanonicalCurrentAuthorizationTargetResolver Resolver { get; } = resolver;
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class FullFlowScope(
        AppDbContext db,
        CanonicalProjectCreateService create,
        ProjectActivationService activation,
        ProjectMembershipService membership,
        CanonicalCurrentAuthorizationTargetResolver resolver) : IAsyncDisposable
    {
        public AppDbContext Db { get; } = db;
        public CanonicalProjectCreateService Create { get; } = create;
        public ProjectActivationService Activation { get; } = activation;
        public ProjectMembershipService Membership { get; } = membership;
        public CanonicalCurrentAuthorizationTargetResolver Resolver { get; } = resolver;
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
