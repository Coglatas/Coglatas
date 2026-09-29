using System.Data.Common;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Groups;
using Coglatas.Application.Planning;
using Coglatas.Application.Projects;
using Coglatas.Application.Workspaces;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Persistence;
using Coglatas.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Coglatas.Tests.PostgreSql;

[Collection("PostgreSqlTaskV1")]
public sealed class TaskV1Pr04MyTasksPostgreSqlTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR04")]
    public async Task ProjectionIsAuthorizedRelationshipAwareStableFilteredAndCountConsistent()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        var graph = await SeedAsync(connectionString);
        var commands = new CommandCaptureInterceptor();
        await using var db = CreateTenantContext(connectionString, graph.Tenant, commands);
        var repository = new PlanningRepository(db);
        var current = new MyTasksQuery(WorkspaceId: graph.Workspace.Id, PageSize: 100);

        commands.Clear();
        var assigned = await repository.ListMyTasksAsync(graph.Actor.Id, current, Now);
        Assert.Contains(assigned.Items, item => item.TaskId == graph.Shared.Id);
        Assert.DoesNotContain(assigned.Items, item => item.TaskId == graph.Hidden.Id);
        Assert.DoesNotContain(assigned.Items, item => item.TaskId == graph.Archived.Id);
        Assert.DoesNotContain(assigned.Items, item => item.TaskId == graph.InactiveWorkspaceTask.Id);
        Assert.Equal(4, commands.Commands.Count);
        Assert.Single(commands.Commands, sql => sql.Contains("work_item_labels", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(commands.Commands, sql => sql.Contains("task_assignments", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(commands.Commands, sql => sql.Contains("task_item_collaborators", StringComparison.OrdinalIgnoreCase));

        var participating = await repository.ListMyTasksAsync(
            graph.Actor.Id,
            current with { View = MyTasksRelationshipView.Participating },
            Now);
        var reviews = await repository.ListMyTasksAsync(
            graph.Actor.Id,
            current with { View = MyTasksRelationshipView.Reviews },
            Now);
        var created = await repository.ListMyTasksAsync(
            graph.Actor.Id,
            current with { View = MyTasksRelationshipView.Created },
            Now);
        var watching = await repository.ListMyTasksAsync(
            graph.Actor.Id,
            current with { View = MyTasksRelationshipView.Watching },
            Now);
        var queue = await repository.ListMyTasksAsync(
            graph.Actor.Id,
            current with { View = MyTasksRelationshipView.TeamQueue },
            Now);
        var completed = await repository.ListMyTasksAsync(
            graph.Actor.Id,
            current with { View = MyTasksRelationshipView.Completed },
            Now);

        Assert.Equal(1, participating.Items.Count(item => item.TaskId == graph.Shared.Id));
        Assert.Contains(reviews.Items, item => item.TaskId == graph.Review.Id);
        Assert.Contains(created.Items, item => item.TaskId == graph.Shared.Id);
        Assert.Contains(watching.Items, item => item.TaskId == graph.EffectiveAutomaticWatch.Id);
        Assert.Contains(watching.Items, item => item.TaskId == graph.ManualWatch.Id);
        Assert.DoesNotContain(watching.Items, item => item.TaskId == graph.OptedOutWatch.Id);
        Assert.Contains(queue.Items, item => item.TaskId == graph.Queue.Id);
        Assert.DoesNotContain(queue.Items, item => item.TaskId == graph.InProgressQueue.Id);
        Assert.False(
            Assert.Single(queue.Items, item => item.TaskId == graph.Queue.Id)
                .QuickEditPermissions.CanClaim);
        Assert.Contains(completed.Items, item => item.TaskId == graph.Completed.Id);
        Assert.Equal(completed.Items.Count, completed.Items.Select(item => item.TaskId).Distinct().Count());

        var reviewProjection = Assert.Single(reviews.Items, item => item.TaskId == graph.Review.Id);
        Assert.False(reviewProjection.QuickEditPermissions.CanChangeStage);
        Assert.False(reviewProjection.QuickEditPermissions.CanUpdateDeadline);
        var parentProjection = Assert.Single(assigned.Items, item => item.TaskId == graph.Parent.Id);
        Assert.True(parentProjection.ProgressIsDerived);
        Assert.False(parentProjection.QuickEditPermissions.CanUpdateProgress);
        Assert.False(parentProjection.QuickEditPermissions.CanUpdatePlannedEnd);

        var filtered = await repository.ListMyTasksAsync(
            graph.Actor.Id,
            current with
            {
                Priority = TaskPriority.Critical,
                Blocked = true,
                Search = "critical-filter"
            },
            Now);
        Assert.Equal([graph.CriticalBlocked.Id], filtered.Items.Select(item => item.TaskId));

        var allWorkspaces = await repository.ListMyTasksAsync(
            graph.Actor.Id,
            current with { Scope = MyTasksScope.AllWorkspaces, WorkspaceId = null },
            Now);
        Assert.Contains(allWorkspaces.Items, item => item.TaskId == graph.SecondWorkspaceTask.Id);
        Assert.All(allWorkspaces.Items, item => Assert.False(string.IsNullOrWhiteSpace(item.WorkspaceTitle)));

        await AssertUrgencyAsync(repository, graph, current);
        await AssertStablePagingAsync(repository, graph, current);

        commands.Clear();
        var countQuery = current with { TimeGroup = MyTasksTimeGroup.Today };
        var todayPage = await repository.ListMyTasksAsync(graph.Actor.Id, countQuery, Now);
        commands.Clear();
        var counts = await repository.GetMyTaskCountsAsync(graph.Actor.Id, countQuery, Now);
        Assert.Equal(3, commands.Commands.Count);
        Assert.Contains(commands.Commands, sql => sql.Contains("UNION ALL", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            todayPage.TotalCount,
            counts.Views.Single(item => item.View == MyTasksRelationshipView.Assigned).Count);
        Assert.Equal(
            todayPage.TotalCount,
            counts.TimeGroups.Single(item => item.TimeGroup == MyTasksTimeGroup.Today).Count);

        var member = await db.WorkspaceMembers.SingleAsync(item =>
            item.WorkspaceId == graph.Workspace.Id && item.UserId == graph.Actor.Id);
        member.Status = MembershipStatus.Suspended;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var afterRevocation = await repository.ListMyTasksAsync(graph.Actor.Id, current, Now);
        var countsAfterRevocation = await repository.GetMyTaskCountsAsync(graph.Actor.Id, current, Now);
        Assert.Empty(afterRevocation.Items);
        Assert.Equal(0, afterRevocation.TotalCount);
        Assert.All(countsAfterRevocation.Views, item => Assert.Equal(0, item.Count));
        Assert.All(countsAfterRevocation.TimeGroups, item => Assert.Equal(0, item.Count));
        var sessionWorkspaces = await new WorkspaceRepository(db).ListForUserAsync(graph.Actor.Id, includeAll: false);
        Assert.DoesNotContain(sessionWorkspaces, item => item.Id == graph.Workspace.Id);
        Assert.Contains(sessionWorkspaces, item => item.Id == graph.SecondWorkspace.Id);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            repository.ListMyTasksAsync(graph.Actor.Id, current, Now, cancelled.Token));
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR04")]
    public async Task EffectiveWatchIndexAppliesOnCleanAndUpgradeSchemasWithoutPendingModelChanges()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database, "20260719071017_MyTasksProjectionIndexes");
            Assert.False(await IndexExistsAsync(database));

            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            Assert.True(await IndexExistsAsync(database));

            await using var context = PostgreSqlMigrationTestDatabase.CreatePlatformContext(database);
            Assert.Empty(await context.Database.GetPendingMigrationsAsync());
            Assert.False(context.Database.HasPendingModelChanges());
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR04")]
    [Trait("Scope", "TaskV1PR05")]
    [Trait("Scope", "TaskV1PR06")]
    public async Task BrowserSmokeSeedIsIdempotentAndCoversCanonicalViewsAcrossTwoWorkspacesPr05KanbanAndPr06Gantt()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        var suffix = Guid.NewGuid().ToString("N");
        var tenant = new Tenant
        {
            Name = $"PR04 browser {suffix}",
            DisplayName = "PR04 browser",
            Slug = $"pr04-browser-{suffix}"
        };
        await using (var platform = CreatePlatformContext(connectionString))
        {
            platform.Tenants.Add(tenant);
            await platform.SaveChangesAsync();
        }

        await using var db = CreateTenantContext(connectionString, tenant);
        var storage = new MemoryFileStorage();
        var email = $"pr04-browser-{suffix}@example.test";
        await AppDbContextSeed.SeedBrowserSmokeAsync(
            db,
            new Pbkdf2PasswordHasher(),
            storage,
            tenant.Id,
            email,
            "PR04-browser-password!",
            CancellationToken.None);
        await AppDbContextSeed.SeedBrowserSmokeAsync(
            db,
            new Pbkdf2PasswordHasher(),
            storage,
            tenant.Id,
            email,
            "PR04-browser-password!",
            CancellationToken.None);

        var actor = await db.Users.SingleAsync(user => user.Email == email);
        var workspaces = await db.Workspaces.OrderBy(workspace => workspace.CreatedAt).ToListAsync();
        Assert.Equal(2, workspaces.Count);
        var primary = workspaces.Single(workspace => workspace.Slug == "browser-smoke-workspace");
        var repository = new PlanningRepository(db);
        var query = new MyTasksQuery(WorkspaceId: primary.Id, PageSize: 100);
        var expected = new Dictionary<MyTasksRelationshipView, string>
        {
            [MyTasksRelationshipView.Assigned] = "Browser smoke task",
            [MyTasksRelationshipView.Participating] = "PR04 participating task",
            [MyTasksRelationshipView.Reviews] = "PR04 review task",
            [MyTasksRelationshipView.Created] = "PR04 created task",
            [MyTasksRelationshipView.Watching] = "PR04 watching task",
            [MyTasksRelationshipView.TeamQueue] = "PR04 team queue task",
            [MyTasksRelationshipView.Completed] = "PR04 completed task"
        };
        foreach (var (view, title) in expected)
        {
            var page = await repository.ListMyTasksAsync(actor.Id, query with { View = view }, Now);
            Assert.Contains(page.Items, item => item.Title == title);
            Assert.Equal(page.Items.Count, page.Items.Select(item => item.TaskId).Distinct().Count());
        }

        var all = await repository.ListMyTasksAsync(
            actor.Id,
            query with { Scope = MyTasksScope.AllWorkspaces, WorkspaceId = null },
            Now);
        Assert.Contains(all.Items, item => item.Title == "PR04 second workspace assigned");

        var pr05Manager = await db.Users.SingleAsync(
            user => user.Email == "browser-smoke-pr05-manager@example.test");
        var pr05Project = await db.Projects.SingleAsync(
            project => project.TenantId == tenant.Id && project.Slug == "browser-smoke-pr05-kanban");
        var pr05Group = await db.Groups.SingleAsync(
            group => group.TenantId == tenant.Id && group.Slug == "browser-smoke-pr04-queue");
        Assert.Equal(primary.Id, pr05Project.WorkspaceId);
        Assert.Equal(pr05Group.Id, pr05Project.GroupId);
        Assert.Equal(
            ProjectRole.Manager,
            (await db.ProjectMembers.SingleAsync(
                member => member.ProjectId == pr05Project.Id && member.UserId == pr05Manager.Id)).Role);
        Assert.False(await db.GroupMembers.AnyAsync(
            member => member.GroupId == pr05Group.Id && member.UserId == pr05Manager.Id));

        var pr06Project = await db.Projects.SingleAsync(
            project => project.TenantId == tenant.Id && project.Slug == "browser-smoke-pr06-gantt");
        var pr06ManagerMember = await db.ProjectMembers.SingleAsync(
            member => member.ProjectId == pr06Project.Id && member.UserId == pr05Manager.Id);
        var pr06Viewer = await db.Users.SingleAsync(
            user => user.Email == "browser-smoke-recipient@example.test");
        var pr06ViewerMember = await db.ProjectMembers.SingleAsync(
            member => member.ProjectId == pr06Project.Id && member.UserId == pr06Viewer.Id);
        Assert.Equal(primary.Id, pr06Project.WorkspaceId);
        Assert.Equal(pr05Group.Id, pr06Project.GroupId);
        Assert.Equal(ProjectRole.Manager, pr06ManagerMember.Role);
        Assert.Equal(ProjectRole.Viewer, pr06ViewerMember.Role);
        Assert.False(await db.GroupMembers.AnyAsync(
            member => member.GroupId == pr05Group.Id && member.UserId == pr05Manager.Id));

        db.ProjectMembers.Remove(pr06ManagerMember);
        await db.SaveChangesAsync();
        var users = new UserRepository(db);
        var workspaceRepository = new WorkspaceRepository(db);
        var workspaceAuthorization = new WorkspaceAuthorizationService(users, workspaceRepository);
        var groups = new GroupRepository(db);
        var groupAuthorization = new GroupAuthorizationService(groups, workspaceRepository, workspaceAuthorization);
        var projectAuthorization = new ProjectAuthorizationService(
            new ProjectRepository(db),
            workspaceAuthorization,
            groupAuthorization,
            groups);
        Assert.False(await projectAuthorization.CanViewProject(pr05Manager.Id, pr06Project.Id));

        var pr05Workflow = await db.TaskWorkflowDefinitions.SingleAsync(
            definition => definition.ProjectId == pr05Project.Id);
        var pr05Stages = await db.TaskWorkflowStages
            .Where(stage => stage.ProjectId == pr05Project.Id)
            .OrderBy(stage => stage.SortKey)
            .ToListAsync();
        Assert.Equal(
            [TaskStageCategory.Todo, TaskStageCategory.Done, TaskStageCategory.Cancelled],
            pr05Stages.Select(stage => stage.InternalCategory));
        Assert.Equal(4, pr05Stages[0].WipWarningLimit);
        var pr05Tasks = await db.TaskItems
            .Where(task => task.ProjectId == pr05Project.Id)
            .OrderBy(task => task.SortKey)
            .ToListAsync();
        Assert.Equal(5, pr05Tasks.Count);
        Assert.All(pr05Tasks, task => Assert.Equal(pr05Stages[0].Id, task.WorkflowStageId));
        Assert.Equal(
            [
                "PR05 real move card",
                "PR05 stable reorder card",
                "PR05 stable neighbor card",
                "PR05 cancellation card",
                "PR05 stale conflict card"
            ],
            pr05Tasks.Select(task => task.Title));
        Assert.Equal(1, pr05Workflow.VersionNo);
        Assert.True(storage.SaveCount >= 2);
    }

    private static async Task AssertUrgencyAsync(
        PlanningRepository repository,
        Graph graph,
        MyTasksQuery query)
    {
        var overdue = await repository.ListMyTasksAsync(
            graph.Actor.Id,
            query with { TimeGroup = MyTasksTimeGroup.Overdue },
            Now);
        var today = await repository.ListMyTasksAsync(
            graph.Actor.Id,
            query with { TimeGroup = MyTasksTimeGroup.Today },
            Now);
        var next = await repository.ListMyTasksAsync(
            graph.Actor.Id,
            query with { TimeGroup = MyTasksTimeGroup.Next7Days },
            Now);
        var later = await repository.ListMyTasksAsync(
            graph.Actor.Id,
            query with { TimeGroup = MyTasksTimeGroup.Later },
            Now);
        var none = await repository.ListMyTasksAsync(
            graph.Actor.Id,
            query with { TimeGroup = MyTasksTimeGroup.NoDeadline },
            Now);

        Assert.Contains(overdue.Items, item => item.TaskId == graph.JustOverdue.Id);
        Assert.DoesNotContain(today.Items, item => item.TaskId == graph.JustOverdue.Id);
        Assert.Contains(today.Items, item => item.TaskId == graph.ExactNow.Id);
        Assert.Contains(today.Items, item => item.TaskId == graph.TodayEnd.Id);
        Assert.Contains(next.Items, item => item.TaskId == graph.Tomorrow.Id);
        Assert.Contains(next.Items, item => item.TaskId == graph.DaySeven.Id);
        Assert.Contains(later.Items, item => item.TaskId == graph.DayEight.Id);
        Assert.Contains(none.Items, item => item.TaskId == graph.NoDeadline.Id);
        Assert.All(
            overdue.Items.Concat(today.Items).Concat(next.Items).Concat(later.Items).Concat(none.Items)
                .GroupBy(item => item.TaskId),
            group => Assert.Single(group));
    }

    private static async Task AssertStablePagingAsync(
        PlanningRepository repository,
        Graph graph,
        MyTasksQuery query)
    {
        var all = await repository.ListMyTasksAsync(graph.Actor.Id, query, Now);
        var traversed = new List<Guid>();
        var lastPage = Math.Max(1, (int)Math.Ceiling(all.TotalCount / 2m));
        for (var page = 1; page <= lastPage; page++)
        {
            var result = await repository.ListMyTasksAsync(
                graph.Actor.Id,
                query with { Page = page, PageSize = 2 },
                Now);
            Assert.Equal(all.TotalCount, result.TotalCount);
            traversed.AddRange(result.Items.Select(item => item.TaskId));
        }

        Assert.Equal(all.Items.Select(item => item.TaskId), traversed);
        Assert.Equal(traversed.Count, traversed.Distinct().Count());

        var priorityOrder = all.Items
            .Where(item => item.TaskId == graph.CriticalBlocked.Id ||
                           item.TaskId == graph.High.Id ||
                           item.TaskId == graph.Medium.Id ||
                           item.TaskId == graph.Low.Id)
            .Select(item => item.TaskId)
            .ToList();
        Assert.Equal(
            [graph.CriticalBlocked.Id, graph.High.Id, graph.Medium.Id, graph.Low.Id],
            priorityOrder);
    }

    private static async Task<bool> IndexExistsAsync(string connectionString) =>
        await PostgreSqlMigrationTestDatabase.ScalarAsync<bool>(
            connectionString,
            """
            SELECT EXISTS (
                SELECT 1
                FROM pg_indexes
                WHERE schemaname = 'public'
                  AND tablename = 'work_item_watch_states'
                  AND indexname = 'IX_work_item_watch_states_effective_watch');
            """);

    private static async Task<Graph> SeedAsync(string connectionString)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var tenant = new Tenant
        {
            Name = $"PR04 {suffix}",
            DisplayName = "PR04",
            Slug = $"pr04-{suffix}"
        };
        var otherTenant = new Tenant
        {
            Name = $"Other {suffix}",
            DisplayName = "Other",
            Slug = $"pr04-other-{suffix}"
        };
        var actor = User("Actor", suffix);
        var other = User("Other", suffix);

        await using (var platform = CreatePlatformContext(connectionString))
        {
            platform.Tenants.AddRange(tenant, otherTenant);
            platform.Users.AddRange(actor, other);
            await platform.SaveChangesAsync();
        }

        await using var db = CreateTenantContext(connectionString, tenant);
        db.TenantUsers.AddRange(
            new TenantUser
            {
                TenantId = tenant.Id,
                UserId = actor.Id,
                Role = TenantUserRole.Member,
                Status = TenantUserStatus.Active,
                JoinedAt = Now
            },
            new TenantUser
            {
                TenantId = tenant.Id,
                UserId = other.Id,
                Role = TenantUserRole.Member,
                Status = TenantUserStatus.Active,
                JoinedAt = Now
            });
        await db.SaveChangesAsync();

        var workspace = Workspace(tenant, actor, $"primary-{suffix}", "Primary");
        var secondWorkspace = Workspace(tenant, actor, $"second-{suffix}", "Second");
        var inactiveWorkspace = Workspace(tenant, actor, $"inactive-{suffix}", "Inactive");
        inactiveWorkspace.Status = WorkspaceStatus.Archived;
        db.Workspaces.AddRange(workspace, secondWorkspace, inactiveWorkspace);
        db.WorkspaceMembers.AddRange(
            WorkspaceMember(tenant, workspace, actor),
            WorkspaceMember(tenant, secondWorkspace, actor),
            WorkspaceMember(tenant, inactiveWorkspace, actor));

        var group = new Group
        {
            TenantId = tenant.Id,
            WorkspaceId = workspace.Id,
            Name = "PR04 group",
            Slug = $"group-{suffix}",
            CreatedByUserId = actor.Id
        };
        var hiddenGroup = new Group
        {
            TenantId = tenant.Id,
            WorkspaceId = workspace.Id,
            Name = "Hidden group",
            Slug = $"hidden-{suffix}",
            CreatedByUserId = other.Id
        };
        db.Groups.AddRange(group, hiddenGroup);
        db.GroupMembers.Add(new GroupMember
        {
            TenantId = tenant.Id,
            GroupId = group.Id,
            UserId = actor.Id,
            Role = GroupRole.Member,
            JoinedAt = Now
        });

        var project = Project(tenant, workspace, group.Id, other, $"project-{suffix}", "Visible project");
        var secondProject = Project(tenant, secondWorkspace, null, actor, $"second-project-{suffix}", "Second project");
        var hiddenProject = Project(tenant, workspace, hiddenGroup.Id, other, $"hidden-project-{suffix}", "Hidden project");
        var archivedProject = Project(tenant, workspace, group.Id, actor, $"archived-project-{suffix}", "Archived project");
        archivedProject.Status = ProjectStatus.Archived;
        var inactiveProject = Project(tenant, inactiveWorkspace, null, actor, $"inactive-project-{suffix}", "Inactive project");
        db.Projects.AddRange(project, secondProject, hiddenProject, archivedProject, inactiveProject);

        var shared = Task(tenant, workspace, project, actor, "Shared", primary: actor.Id);
        var review = Task(tenant, workspace, project, other, "Review", primary: other.Id, reviewer: actor.Id);
        var effectiveAutomaticWatch = Task(tenant, workspace, project, other, "Effective automatic watch", primary: other.Id);
        var manualWatch = Task(tenant, workspace, project, other, "Manual watch", primary: other.Id);
        var optedOutWatch = Task(tenant, workspace, project, other, "Opted out watch", primary: other.Id);
        var queue = Task(tenant, workspace, project, other, "Queue", targetGroup: group.Id);
        var inProgressQueue = Task(tenant, workspace, project, other, "In-progress queue", targetGroup: group.Id);
        inProgressQueue.Status = TaskItemStatus.InProgress;
        var completed = Task(tenant, workspace, project, other, "Completed", primary: actor.Id);
        completed.Status = TaskItemStatus.Completed;
        var hidden = Task(tenant, workspace, hiddenProject, other, "Hidden", primary: actor.Id);
        var archived = Task(tenant, workspace, archivedProject, actor, "Archived", primary: actor.Id);
        var inactiveWorkspaceTask = Task(tenant, inactiveWorkspace, inactiveProject, actor, "Inactive Workspace", primary: actor.Id);
        var secondWorkspaceTask = Task(tenant, secondWorkspace, secondProject, actor, "Second Workspace", primary: actor.Id);
        var parent = Task(tenant, workspace, project, actor, "Parent", primary: actor.Id);
        var child = Task(tenant, workspace, project, actor, "Child", primary: other.Id);
        child.ParentTaskItemId = parent.Id;

        var criticalBlocked = Task(tenant, workspace, project, actor, "critical-filter", primary: actor.Id);
        criticalBlocked.Priority = TaskPriority.Critical;
        criticalBlocked.IsBlocked = true;
        var high = Task(tenant, workspace, project, actor, "High", primary: actor.Id);
        high.Priority = TaskPriority.High;
        var medium = Task(tenant, workspace, project, actor, "Medium", primary: actor.Id);
        medium.Priority = TaskPriority.Medium;
        var low = Task(tenant, workspace, project, actor, "Low", primary: actor.Id);
        low.Priority = TaskPriority.Low;

        var justOverdue = Task(tenant, workspace, project, actor, "Just overdue", primary: actor.Id);
        justOverdue.DeadlineAt = Now.AddTicks(-1);
        var exactNow = Task(tenant, workspace, project, actor, "Exact now", primary: actor.Id);
        exactNow.DeadlineAt = Now;
        var todayEnd = Task(tenant, workspace, project, actor, "Today end", primary: actor.Id);
        todayEnd.DeadlineAt = new DateTimeOffset(2026, 7, 29, 23, 59, 59, TimeSpan.Zero);
        var tomorrow = Task(tenant, workspace, project, actor, "Tomorrow", primary: actor.Id);
        tomorrow.DeadlineAt = new DateTimeOffset(2026, 7, 30, 0, 0, 0, TimeSpan.Zero);
        var daySeven = Task(tenant, workspace, project, actor, "Day seven", primary: actor.Id);
        daySeven.DeadlineAt = new DateTimeOffset(2026, 8, 5, 23, 59, 59, TimeSpan.Zero);
        var dayEight = Task(tenant, workspace, project, actor, "Day eight", primary: actor.Id);
        dayEight.DeadlineAt = new DateTimeOffset(2026, 8, 6, 0, 0, 0, TimeSpan.Zero);
        var noDeadline = Task(tenant, workspace, project, actor, "No deadline", primary: actor.Id);

        db.TaskItems.AddRange(
            shared, review, effectiveAutomaticWatch, manualWatch, optedOutWatch, queue, inProgressQueue,
            completed, hidden, archived, inactiveWorkspaceTask, secondWorkspaceTask, parent, child,
            criticalBlocked, high, medium, low, justOverdue, exactNow, todayEnd, tomorrow, daySeven,
            dayEight, noDeadline);
        db.WorkItemCollaborators.Add(new WorkItemCollaborator
        {
            TenantId = tenant.Id,
            TaskItemId = shared.Id,
            UserId = actor.Id,
            AddedByUserId = actor.Id,
            AddedAt = Now
        });
        db.WorkItemWatchStates.AddRange(
            new WorkItemWatchState
            {
                TenantId = tenant.Id,
                TaskItemId = effectiveAutomaticWatch.Id,
                UserId = actor.Id,
                AutomaticSources = WorkItemWatchAutomaticSource.Creator,
                IsWatching = false,
                UpdatedAt = Now
            },
            new WorkItemWatchState
            {
                TenantId = tenant.Id,
                TaskItemId = manualWatch.Id,
                UserId = actor.Id,
                IsManualWatch = true,
                IsWatching = false,
                UpdatedAt = Now
            },
            new WorkItemWatchState
            {
                TenantId = tenant.Id,
                TaskItemId = optedOutWatch.Id,
                UserId = actor.Id,
                AutomaticSources = WorkItemWatchAutomaticSource.Collaborator,
                IsExplicitOptOut = true,
                IsWatching = true,
                UpdatedAt = Now
            });
        var label = new ProjectTaskLabel
        {
            TenantId = tenant.Id,
            WorkspaceId = workspace.Id,
            ProjectId = project.Id,
            Name = "PR04 label",
            SortKey = 1024
        };
        db.ProjectTaskLabels.Add(label);
        db.WorkItemLabels.Add(new WorkItemLabel
        {
            TenantId = tenant.Id,
            TaskItemId = shared.Id,
            LabelId = label.Id,
            AddedAt = Now,
            AddedByUserId = actor.Id
        });
        await db.SaveChangesAsync();

        return new Graph(
            tenant, actor, workspace, secondWorkspace, shared, review, effectiveAutomaticWatch, manualWatch, optedOutWatch,
            queue, inProgressQueue, completed, hidden, archived, inactiveWorkspaceTask,
            secondWorkspaceTask, parent, criticalBlocked, high, medium, low, justOverdue, exactNow,
            todayEnd, tomorrow, daySeven, dayEight, noDeadline);
    }

    private static User User(string name, string suffix) => new()
    {
        DisplayName = name,
        Email = $"{name.ToLowerInvariant()}-{suffix}@example.test",
        NormalizedEmail = $"{name.ToUpperInvariant()}-{suffix}@EXAMPLE.TEST",
        PasswordHash = "hash",
        Status = UserStatus.Active
    };

    private static Workspace Workspace(Tenant tenant, User owner, string slug, string name) => new()
    {
        TenantId = tenant.Id,
        Name = name,
        Slug = slug,
        Status = WorkspaceStatus.Active,
        CreatedByUserId = owner.Id
    };

    private static WorkspaceMember WorkspaceMember(Tenant tenant, Workspace workspace, User actor) => new()
    {
        TenantId = tenant.Id,
        WorkspaceId = workspace.Id,
        UserId = actor.Id,
        Role = WorkspaceRole.Member,
        Status = MembershipStatus.Active,
        JoinedAt = Now
    };

    private static Project Project(
        Tenant tenant,
        Workspace workspace,
        Guid? groupId,
        User owner,
        string slug,
        string name) => new()
    {
        TenantId = tenant.Id,
        WorkspaceId = workspace.Id,
        GroupId = groupId,
        OwnerUserId = owner.Id,
        CreatedByUserId = owner.Id,
        Name = name,
        Slug = slug,
        Status = ProjectStatus.Active
    };

    private static TaskItem Task(
        Tenant tenant,
        Workspace workspace,
        Project project,
        User creator,
        string title,
        Guid? primary = null,
        Guid? reviewer = null,
        Guid? targetGroup = null) => new()
    {
        TenantId = tenant.Id,
        WorkspaceId = workspace.Id,
        ProjectId = project.Id,
        Title = title,
        CreatedByUserId = creator.Id,
        PrimaryAssigneeUserId = primary,
        ReviewerUserId = reviewer,
        TargetGroupId = targetGroup,
        Status = TaskItemStatus.NotStarted,
        Priority = TaskPriority.Medium,
        CreatedAt = Now,
        UpdatedAt = Now,
        VersionNo = 1
    };

    private static AppDbContext CreateTenantContext(
        string connectionString,
        Tenant tenant,
        IInterceptor? interceptor = null)
    {
        var currentTenant = new CurrentTenantService();
        currentTenant.SetTenant(tenant.Id, tenant.Slug);
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString);
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new AppDbContext(options.Options, currentTenant);
    }

    private static AppDbContext CreatePlatformContext(string connectionString)
    {
        var currentTenant = new CurrentTenantService();
        currentTenant.SetPlatformScope();
        return new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options,
            currentTenant);
    }

    private sealed class CommandCaptureInterceptor : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public void Clear() => Commands.Clear();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class MemoryFileStorage : IFileStorageService
    {
        public int SaveCount { get; private set; }

        public async Task<Result> SaveAsync(
            string storageKey,
            Stream stream,
            string contentType,
            CancellationToken cancellationToken = default)
        {
            await stream.CopyToAsync(Stream.Null, cancellationToken);
            SaveCount++;
            return Result.Success();
        }

        public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) =>
            System.Threading.Tasks.Task.FromResult<Stream>(Stream.Null);

        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default) =>
            System.Threading.Tasks.Task.CompletedTask;

        public Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken = default) =>
            System.Threading.Tasks.Task.FromResult(true);

        public Task<string?> CreateSignedReadUrlAsync(
            string storageKey,
            TimeSpan expiresIn,
            CancellationToken cancellationToken = default) =>
            System.Threading.Tasks.Task.FromResult<string?>(null);
    }

    private sealed record Graph(
        Tenant Tenant,
        User Actor,
        Workspace Workspace,
        Workspace SecondWorkspace,
        TaskItem Shared,
        TaskItem Review,
        TaskItem EffectiveAutomaticWatch,
        TaskItem ManualWatch,
        TaskItem OptedOutWatch,
        TaskItem Queue,
        TaskItem InProgressQueue,
        TaskItem Completed,
        TaskItem Hidden,
        TaskItem Archived,
        TaskItem InactiveWorkspaceTask,
        TaskItem SecondWorkspaceTask,
        TaskItem Parent,
        TaskItem CriticalBlocked,
        TaskItem High,
        TaskItem Medium,
        TaskItem Low,
        TaskItem JustOverdue,
        TaskItem ExactNow,
        TaskItem TodayEnd,
        TaskItem Tomorrow,
        TaskItem DaySeven,
        TaskItem DayEight,
        TaskItem NoDeadline);
}
