using System.Data.Common;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit.Abstractions;

namespace Coglatas.Tests.PostgreSql;

[Collection("PostgreSqlTaskV1")]
public sealed class TaskV1Pr06GanttPostgreSqlTests(ITestOutputHelper output)
{
    private const string PreviousMigration = "20260729140506_AddProjectKanbanDefaultSwimlane";

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR06")]
    public async Task GanttVersionMigrationAppliesToEmptyAndPr05UpgradeAndRollsBackAdditively()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async emptyDatabase =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(emptyDatabase);
            Assert.True(await MilestoneVersionColumnExistsAsync(emptyDatabase));
            Assert.True(await ProjectVersionColumnExistsAsync(emptyDatabase));
            await using var context = PostgreSqlMigrationTestDatabase.CreatePlatformContext(emptyDatabase);
            Assert.Empty(await context.Database.GetPendingMigrationsAsync());
            Assert.False(context.Database.HasPendingModelChanges());
        });

        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async upgradeDatabase =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(upgradeDatabase, PreviousMigration);
            Assert.False(await MilestoneVersionColumnExistsAsync(upgradeDatabase));
            Assert.False(await ProjectVersionColumnExistsAsync(upgradeDatabase));
            var legacy = await SeedLegacyMilestoneAsync(upgradeDatabase);

            await PostgreSqlMigrationTestDatabase.MigrateAsync(upgradeDatabase);
            Assert.True(await MilestoneVersionColumnExistsAsync(upgradeDatabase));
            Assert.True(await ProjectVersionColumnExistsAsync(upgradeDatabase));
            Assert.Equal(1L, await PostgreSqlMigrationTestDatabase.ScalarAsync<long>(
                upgradeDatabase,
                """SELECT "VersionNo" FROM milestones WHERE "Id" = @id""",
                ("id", legacy.MilestoneId)));
            Assert.Equal(1L, await PostgreSqlMigrationTestDatabase.ScalarAsync<long>(
                upgradeDatabase,
                """SELECT "VersionNo" FROM projects WHERE "Id" = @id""",
                ("id", legacy.ProjectId)));

            await PostgreSqlMigrationTestDatabase.MigrateAsync(upgradeDatabase, PreviousMigration);
            Assert.False(await MilestoneVersionColumnExistsAsync(upgradeDatabase));
            Assert.False(await ProjectVersionColumnExistsAsync(upgradeDatabase));
            Assert.Equal(1, await PostgreSqlMigrationTestDatabase.ScalarAsync<int>(
                upgradeDatabase,
                """SELECT COUNT(*)::int FROM milestones WHERE "Id" = @id""",
                ("id", legacy.MilestoneId)));
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR06")]
    public async Task SnapshotIsCanonicalBoundedDeterministicAndUsesFixedQueryCount()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            var graph = await SeedGanttAsync(database);
            var interceptor = new CommandCaptureInterceptor();
            await using var context = CreateTenantContext(database, graph.Tenant, interceptor);
            var repository = new PlanningRepository(context);

            interceptor.Clear();
            var first = await repository.GetGanttAsync(
                graph.Project.Id,
                graph.User.Id,
                canManageProject: true,
                canContributeToOwnedTasks: true,
                workspaceTimeZone: "Asia/Tokyo",
                maximumItems: 500);

            var snapshot = Assert.IsType<Coglatas.Application.Planning.ProjectGanttResponse>(first.Snapshot);
            Assert.False(first.ItemLimitExceeded);
            Assert.False(first.DependencyLimitExceeded);
            Assert.Equal(8, snapshot.TotalItems);
            Assert.Equal(500, snapshot.MaximumItems);
            Assert.Equal("Asia/Tokyo", snapshot.Calendar.TimeZone);
            Assert.False(snapshot.Calendar.HolidaysAvailable);
            Assert.Empty(snapshot.Calendar.WorkingDays);
            Assert.Equal(5, snapshot.ScheduledItems.Count);
            Assert.Single(snapshot.UnscheduledItems);
            Assert.Equal(2, snapshot.Milestones.Count);
            Assert.All(
                snapshot.Milestones,
                milestone => Assert.False(milestone.ScheduleEditPermissions.CanOpen));
            Assert.Equal(2, snapshot.Dependencies.Count);
            Assert.Equal(7, interceptor.Commands.Count);
            for (var index = 0; index < interceptor.Commands.Count; index++)
                output.WriteLine($"PR06 snapshot SQL {index + 1:D2}: {NormalizeSql(interceptor.Commands[index])}");
            Assert.Contains(interceptor.Commands, sql => sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(interceptor.Commands, sql => sql.Contains("task_assignments", StringComparison.OrdinalIgnoreCase));

            var parent = snapshot.ScheduledItems.Single(item => item.TaskId == graph.ParentTaskId);
            Assert.True(parent.ProgressIsDerived);
            Assert.Equal(new DateOnly(2026, 8, 1), parent.PlannedStartDate);
            Assert.Equal(new DateOnly(2026, 8, 10), parent.PlannedEndDate);
            Assert.False(parent.ScheduleEditPermissions.CanEditSchedule);
            Assert.Contains(parent.Warnings, warning => warning.Code == "PARENT_DERIVED");
            Assert.False(snapshot.ScheduledItems
                .Single(item => item.TaskId == graph.DoneTaskId)
                .ScheduleEditPermissions.CanEditProgress);
            Assert.False(snapshot.ScheduledItems
                .Single(item => item.TaskId == graph.CancelledTaskId)
                .ScheduleEditPermissions.CanEditProgress);
            var unscheduled = Assert.Single(snapshot.UnscheduledItems);
            Assert.Equal(graph.UnscheduledTaskId, unscheduled.TaskId);
            Assert.Contains(unscheduled.Warnings, warning => warning.Code == "UNSCHEDULED");
            Assert.Contains(snapshot.Warnings, warning => warning.Code == "MISSING_ACTIVE_PLANNED_END");
            Assert.Contains(snapshot.Warnings, warning => warning.Code == "DEPENDENCY_VIOLATION");
            Assert.Contains(snapshot.Warnings, warning => warning.Code == "LEGACY_DEPENDENCY_TYPE");
            Assert.Contains(snapshot.Warnings, warning => warning.Code == "MILESTONE_DATE_REQUIRED");
            Assert.All(snapshot.Dependencies.Where(item => item.Type != TaskDependencyType.FinishToStart), item => Assert.False(item.Editable));
            Assert.All(snapshot.ScheduledItems.Concat(snapshot.UnscheduledItems), item => Assert.True(item.Version > 0));
            var allProjectedIds = snapshot.ScheduledItems
                .Concat(snapshot.UnscheduledItems)
                .Concat(snapshot.Milestones)
                .Select(item => item.TaskId)
                .ToList();
            Assert.Equal(snapshot.TotalItems, allProjectedIds.Count);
            Assert.Equal(snapshot.TotalItems, allProjectedIds.Distinct().Count());

            context.ChangeTracker.Clear();
            interceptor.Clear();
            var boundedItemCount = await new ProjectRepository(context)
                .CountGanttItemsBoundedAsync(graph.Project.Id, 501);
            Assert.Equal(snapshot.TotalItems, boundedItemCount);
            Assert.Single(interceptor.Commands);
            Assert.Contains(
                "LIMIT",
                interceptor.Commands.Single(),
                StringComparison.OrdinalIgnoreCase);

            interceptor.Clear();
            var incidentDependencies = await new ProjectRepository(context)
                .ListDependenciesBoundedAsync(graph.UnscheduledTaskId, 1);
            Assert.Single(incidentDependencies);
            Assert.Single(interceptor.Commands);
            Assert.Contains(
                "LIMIT",
                interceptor.Commands.Single(),
                StringComparison.OrdinalIgnoreCase);

            context.ChangeTracker.Clear();
            interceptor.Clear();
            var second = await repository.GetGanttAsync(
                graph.Project.Id,
                graph.User.Id,
                true,
                true,
                "Asia/Tokyo",
                500);
            Assert.Equal(
                snapshot.ScheduledItems.Select(item => item.TaskId),
                second.Snapshot!.ScheduledItems.Select(item => item.TaskId));
            Assert.Equal(7, interceptor.Commands.Count);

            var hiddenNeighbor = await context.TaskItems.SingleAsync(task =>
                task.Id == graph.UnscheduledTaskId);
            hiddenNeighbor.MarkDeleted(DateTimeOffset.UtcNow, graph.User.Id, "test");
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
            var activeGraph = await new ProjectRepository(context)
                .ListProjectDependenciesBoundedAsync(graph.Project.Id, 2_001);
            var remaining = Assert.Single(activeGraph);
            Assert.Equal(TaskDependencyType.FinishToStart, remaining.DependencyType);

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.GetGanttAsync(
                graph.Project.Id,
                graph.User.Id,
                true,
                true,
                "Asia/Tokyo",
                500,
                cancelled.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProjectRepository(context)
                .ListDependenciesBoundedAsync(graph.UnscheduledTaskId, 1, cancelled.Token));
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR06")]
    public async Task CompletedParentProjectsCanonicalDerivedProgressAtOneHundred()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            var graph = await SeedGanttAsync(database);
            await using (var write = CreateTenantContext(database, graph.Tenant))
            {
                var doneStageId = await write.TaskWorkflowStages
                    .Where(stage =>
                        stage.ProjectId == graph.Project.Id &&
                        stage.InternalCategory == TaskStageCategory.Done)
                    .Select(stage => stage.Id)
                    .SingleAsync();
                var parentAndChildren = await write.TaskItems
                    .Where(task =>
                        task.Id == graph.ParentTaskId ||
                        task.ParentTaskItemId == graph.ParentTaskId)
                    .ToListAsync();
                foreach (var task in parentAndChildren)
                {
                    task.WorkflowStageId = doneStageId;
                    task.Status = TaskItemStatus.Completed;
                    task.ProgressPercent = 100;
                }
                await write.SaveChangesAsync();
            }

            await using var read = CreateTenantContext(database, graph.Tenant);
            var result = await new PlanningRepository(read).GetGanttAsync(
                graph.Project.Id,
                graph.User.Id,
                canManageProject: true,
                canContributeToOwnedTasks: true,
                workspaceTimeZone: "Asia/Tokyo",
                maximumItems: 500);

            var parent = result.Snapshot!.ScheduledItems.Single(item =>
                item.TaskId == graph.ParentTaskId);
            Assert.Equal(TaskStageCategory.Done, parent.StageCategory);
            Assert.True(parent.ProgressIsDerived);
            Assert.Equal(100, parent.ProgressPercent);
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR06")]
    public async Task SnapshotRejectsOverflowBeforeLoadingRowsOrDependencyGraph()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            var graph = await SeedOverflowProjectAsync(database, 501);
            var interceptor = new CommandCaptureInterceptor();
            await using var context = CreateTenantContext(database, graph.Tenant, interceptor);
            var repository = new PlanningRepository(context);

            interceptor.Clear();
            var result = await repository.GetGanttAsync(
                graph.Project.Id,
                graph.User.Id,
                true,
                true,
                "UTC",
                500);

            Assert.Null(result.Snapshot);
            Assert.True(result.ItemLimitExceeded);
            Assert.Equal(501, result.TotalItems);
            Assert.Equal(3, interceptor.Commands.Count);
            Assert.DoesNotContain(interceptor.Commands, sql => sql.Contains("task_dependencies", StringComparison.OrdinalIgnoreCase));
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR06")]
    public async Task SnapshotRechecksCombinedBoundWhenRowsAppearAfterCountGate()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            var graph = await SeedOverflowProjectAsync(database, 500);
            var interceptor = new AfterMilestoneCountInterceptor(async cancellationToken =>
            {
                await using var insert = CreateTenantContext(database, graph.Tenant);
                insert.Milestones.Add(new Milestone
                {
                    ProjectId = graph.Project.Id,
                    Name = "Concurrent milestone",
                    DueDate = new DateOnly(2026, 9, 1),
                    SortOrder = 1,
                    VersionNo = 1
                });
                await insert.SaveChangesAsync(cancellationToken);
            });
            await using var context = CreateTenantContext(database, graph.Tenant, interceptor);

            var result = await new PlanningRepository(context).GetGanttAsync(
                graph.Project.Id,
                graph.User.Id,
                true,
                true,
                "UTC",
                500);

            Assert.True(interceptor.Inserted);
            Assert.Null(result.Snapshot);
            Assert.True(result.ItemLimitExceeded);
            Assert.Equal(501, result.TotalItems);
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR06")]
    public async Task GanttAggregateVersionsAreOptimisticConcurrencyTokens()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            var graph = await SeedGanttAsync(database);
            await using var first = CreateTenantContext(database, graph.Tenant);
            await using var second = CreateTenantContext(database, graph.Tenant);
            var milestoneId = graph.DatedMilestoneId;
            var firstMilestone = await first.Milestones.SingleAsync(item => item.Id == milestoneId);
            var secondMilestone = await second.Milestones.SingleAsync(item => item.Id == milestoneId);

            firstMilestone.DueDate = new DateOnly(2026, 9, 1);
            firstMilestone.VersionNo++;
            await first.SaveChangesAsync();
            secondMilestone.DueDate = new DateOnly(2026, 9, 2);
            secondMilestone.VersionNo++;

            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
            await using var verification = CreateTenantContext(database, graph.Tenant);
            var persisted = await verification.Milestones.AsNoTracking().SingleAsync(item => item.Id == milestoneId);
            Assert.Equal(new DateOnly(2026, 9, 1), persisted.DueDate);
            Assert.Equal(2, persisted.VersionNo);

            await using var projectFirst = CreateTenantContext(database, graph.Tenant);
            await using var projectSecond = CreateTenantContext(database, graph.Tenant);
            var firstProject = await projectFirst.Projects.SingleAsync(item => item.Id == graph.Project.Id);
            var secondProject = await projectSecond.Projects.SingleAsync(item => item.Id == graph.Project.Id);
            firstProject.Name = "First project revision";
            firstProject.VersionNo++;
            await projectFirst.SaveChangesAsync();
            secondProject.Name = "Stale project revision";
            secondProject.VersionNo++;
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => projectSecond.SaveChangesAsync());
            await using var projectVerification = CreateTenantContext(database, graph.Tenant);
            var persistedProject = await projectVerification.Projects
                .AsNoTracking()
                .SingleAsync(item => item.Id == graph.Project.Id);
            Assert.Equal("First project revision", persistedProject.Name);
            Assert.Equal(2, persistedProject.VersionNo);
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR06")]
    public async Task ProjectGraphRevisionRejectsDependencyCommitAgainstConcurrentlyDeletedNeighbor()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            var graph = await SeedGanttAsync(database);
            await using var dependencyWriter = CreateTenantContext(database, graph.Tenant);
            await using var lifecycleWriter = CreateTenantContext(database, graph.Tenant);
            var dependencyProject = await dependencyWriter.Projects.SingleAsync(project =>
                project.Id == graph.Project.Id);
            var lifecycleProject = await lifecycleWriter.Projects.SingleAsync(project =>
                project.Id == graph.Project.Id);
            var predecessor = await dependencyWriter.TaskItems.SingleAsync(task =>
                task.Id == graph.DoneTaskId);
            var successor = await dependencyWriter.TaskItems.SingleAsync(task =>
                task.Title == "Active");
            var lifecyclePredecessor = await lifecycleWriter.TaskItems.SingleAsync(task =>
                task.Id == graph.DoneTaskId);

            lifecyclePredecessor.MarkDeleted(DateTimeOffset.UtcNow, graph.User.Id, "test");
            lifecyclePredecessor.VersionNo++;
            lifecycleProject.VersionNo++;
            await lifecycleWriter.SaveChangesAsync();

            dependencyWriter.TaskDependencies.Add(new TaskDependency
            {
                ProjectId = graph.Project.Id,
                PredecessorTaskItemId = predecessor.Id,
                SuccessorTaskItemId = successor.Id,
                DependencyType = TaskDependencyType.FinishToStart
            });
            successor.VersionNo++;
            dependencyProject.VersionNo++;

            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
                () => dependencyWriter.SaveChangesAsync());
            await using var verification = CreateTenantContext(database, graph.Tenant);
            Assert.False(await verification.TaskDependencies.AnyAsync(dependency =>
                dependency.PredecessorTaskItemId == graph.DoneTaskId &&
                dependency.SuccessorTaskItemId == successor.Id));
        });
    }

    private static async Task<bool> MilestoneVersionColumnExistsAsync(string connectionString) =>
        await PostgreSqlMigrationTestDatabase.ScalarAsync<bool>(
            connectionString,
            """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name = 'milestones'
                  AND column_name = 'VersionNo');
            """);

    private static async Task<bool> ProjectVersionColumnExistsAsync(string connectionString) =>
        await PostgreSqlMigrationTestDatabase.ScalarAsync<bool>(
            connectionString,
            """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name = 'projects'
                  AND column_name = 'VersionNo');
            """);

    private static async Task<LegacyGanttIds> SeedLegacyMilestoneAsync(string connectionString)
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var milestoneId = Guid.NewGuid();
        await PostgreSqlMigrationTestDatabase.ExecuteAsync(
            connectionString,
            """
            INSERT INTO tenants ("Id", "Name", "DisplayName", "Slug", "Status", "CreatedAt")
            VALUES (@tenant, 'PR06', 'PR06', @tenantSlug, 'Active', NOW());
            INSERT INTO users ("Id", "DisplayName", "Email", "NormalizedEmail", "PasswordHash", "Status", "CreatedAt")
            VALUES (@user, 'PR06', @email, @normalized, 'hash', 'Active', NOW());
            INSERT INTO workspaces ("Id", "TenantId", "Name", "Slug", "Status", "CreatedByUserId", "CreatedAt")
            VALUES (@workspace, @tenant, 'PR06 workspace', @workspaceSlug, 'Active', @user, NOW());
            INSERT INTO projects ("Id", "TenantId", "WorkspaceId", "OwnerUserId", "CreatedByUserId", "Name", "Slug", "Status", "CreatedAt")
            VALUES (@project, @tenant, @workspace, @user, @user, 'PR06 project', @projectSlug, 'Active', NOW());
            INSERT INTO milestones ("Id", "TenantId", "ProjectId", "Name", "Status", "SortOrder", "CreatedAt")
            VALUES (@milestone, @tenant, @project, 'Legacy', 'NotStarted', 1, NOW());
            """,
            ("tenant", tenantId),
            ("tenantSlug", $"pr06-{tenantId:N}"),
            ("user", userId),
            ("email", $"pr06-{userId:N}@example.test"),
            ("normalized", $"PR06-{userId:N}@EXAMPLE.TEST"),
            ("workspace", workspaceId),
            ("workspaceSlug", $"workspace-{workspaceId:N}"),
            ("project", projectId),
            ("projectSlug", $"project-{projectId:N}"),
            ("milestone", milestoneId));
        return new LegacyGanttIds(projectId, milestoneId);
    }

    private static async Task<GanttGraph> SeedGanttAsync(string connectionString)
    {
        var core = await SeedProjectAsync(connectionString);
        await using var context = CreateTenantContext(connectionString, core.Tenant);
        var todo = await context.TaskWorkflowStages.SingleAsync(stage =>
            stage.ProjectId == core.Project.Id &&
            stage.InternalCategory == TaskStageCategory.Todo);
        var inProgress = await context.TaskWorkflowStages.SingleAsync(stage =>
            stage.ProjectId == core.Project.Id &&
            stage.InternalCategory == TaskStageCategory.InProgress);
        var done = await context.TaskWorkflowStages.SingleAsync(stage =>
            stage.ProjectId == core.Project.Id &&
            stage.InternalCategory == TaskStageCategory.Done);
        var cancelled = await context.TaskWorkflowStages.SingleAsync(stage =>
            stage.ProjectId == core.Project.Id &&
            stage.InternalCategory == TaskStageCategory.Cancelled);
        var datedMilestone = new Milestone
        {
            ProjectId = core.Project.Id,
            Name = "Release",
            DueDate = new DateOnly(2026, 8, 20),
            SortOrder = 1,
            VersionNo = 1
        };
        var undatedMilestone = new Milestone
        {
            ProjectId = core.Project.Id,
            Name = "Legacy",
            SortOrder = 2,
            VersionNo = 1
        };
        var parent = Task(core, todo, "Parent", 1000);
        var scheduledChild = Task(core, todo, "Scheduled child", 1100);
        scheduledChild.ParentTaskItemId = parent.Id;
        scheduledChild.PlannedStartDate = scheduledChild.StartDate = new DateOnly(2026, 8, 1);
        scheduledChild.PlannedEndDate = scheduledChild.DueDate = new DateOnly(2026, 8, 10);
        scheduledChild.ProgressPercent = 40;
        var unscheduledChild = Task(core, todo, "Unscheduled child", 1200);
        unscheduledChild.ParentTaskItemId = parent.Id;
        unscheduledChild.ProgressPercent = 20;
        var active = Task(core, inProgress, "Active", 2000);
        active.Status = TaskItemStatus.InProgress;
        active.PlannedStartDate = active.StartDate = new DateOnly(2026, 8, 5);
        var completed = Task(core, done, "Completed", 3000);
        completed.Status = TaskItemStatus.Completed;
        completed.ProgressPercent = 100;
        completed.PlannedStartDate = completed.StartDate = new DateOnly(2026, 8, 11);
        completed.PlannedEndDate = completed.DueDate = new DateOnly(2026, 8, 12);
        var cancelledTask = Task(core, cancelled, "Cancelled", 4000);
        cancelledTask.Status = TaskItemStatus.Cancelled;
        cancelledTask.ProgressPercent = 20;
        cancelledTask.PlannedStartDate = cancelledTask.StartDate = new DateOnly(2026, 8, 13);
        cancelledTask.PlannedEndDate = cancelledTask.DueDate = new DateOnly(2026, 8, 14);
        context.AddRange(
            datedMilestone,
            undatedMilestone,
            parent,
            scheduledChild,
            unscheduledChild,
            active,
            completed,
            cancelledTask);
        await context.SaveChangesAsync();

        var violating = new TaskDependency
        {
            ProjectId = core.Project.Id,
            PredecessorTaskItemId = scheduledChild.Id,
            SuccessorTaskItemId = active.Id,
            DependencyType = TaskDependencyType.FinishToStart
        };
        var legacy = new TaskDependency
        {
            ProjectId = core.Project.Id,
            PredecessorTaskItemId = unscheduledChild.Id,
            SuccessorTaskItemId = active.Id,
            DependencyType = TaskDependencyType.StartToStart
        };
        context.AddRange(violating, legacy);
        await context.SaveChangesAsync();
        return new GanttGraph(
            core.Tenant,
            core.Project,
            core.User,
            parent.Id,
            unscheduledChild.Id,
            datedMilestone.Id,
            completed.Id,
            cancelledTask.Id);
    }

    private static async Task<ProjectCore> SeedOverflowProjectAsync(string connectionString, int taskCount)
    {
        var core = await SeedProjectAsync(connectionString);
        await using var context = CreateTenantContext(connectionString, core.Tenant);
        var todo = await context.TaskWorkflowStages.SingleAsync(stage =>
            stage.ProjectId == core.Project.Id &&
            stage.InternalCategory == TaskStageCategory.Todo);
        context.TaskItems.AddRange(Enumerable.Range(0, taskCount)
            .Select(index => Task(core, todo, $"Task {index:D3}", index + 1)));
        await context.SaveChangesAsync();
        return core;
    }

    private static async Task<ProjectCore> SeedProjectAsync(string connectionString)
    {
        var tenantScope = new CurrentTenantService();
        tenantScope.SetPlatformScope();
        await using var platform = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options,
            tenantScope);
        var tenant = new Tenant
        {
            Name = "PR06 Gantt",
            DisplayName = "PR06 Gantt",
            Slug = $"pr06-gantt-{Guid.NewGuid():N}"
        };
        var user = new User
        {
            DisplayName = "PR06 actor",
            Email = $"pr06-{Guid.NewGuid():N}@example.test",
            NormalizedEmail = $"PR06-{Guid.NewGuid():N}@EXAMPLE.TEST",
            PasswordHash = "hash",
            Status = UserStatus.Active
        };
        platform.AddRange(tenant, user);
        await platform.SaveChangesAsync();

        tenantScope.SetTenant(tenant.Id, tenant.Slug);
        platform.TenantUsers.Add(new TenantUser
        {
            TenantId = tenant.Id,
            UserId = user.Id,
            Role = TenantUserRole.Member,
            Status = TenantUserStatus.Active,
            JoinedAt = DateTimeOffset.UtcNow
        });
        var workspace = new Workspace
        {
            Name = "PR06 workspace",
            Slug = $"pr06-{Guid.NewGuid():N}",
            TimeZone = "Asia/Tokyo",
            CreatedByUserId = user.Id,
            Status = WorkspaceStatus.Active
        };
        platform.Workspaces.Add(workspace);
        await platform.SaveChangesAsync();
        platform.WorkspaceMembers.Add(new WorkspaceMember
        {
            WorkspaceId = workspace.Id,
            UserId = user.Id,
            Role = WorkspaceRole.Owner,
            Status = MembershipStatus.Active,
            JoinedAt = DateTimeOffset.UtcNow
        });
        var project = new Project
        {
            WorkspaceId = workspace.Id,
            OwnerUserId = user.Id,
            CreatedByUserId = user.Id,
            Name = "PR06 schedule",
            Slug = $"pr06-{Guid.NewGuid():N}",
            Status = ProjectStatus.Active
        };
        platform.Projects.Add(project);
        platform.ProjectMembers.Add(new ProjectMember
        {
            ProjectId = project.Id,
            UserId = user.Id,
            Role = ProjectRole.Owner,
            JoinedAt = DateTimeOffset.UtcNow
        });
        await platform.SaveChangesAsync();
        return new ProjectCore(tenant, workspace, project, user);
    }

    private static TaskItem Task(ProjectCore core, TaskWorkflowStage stage, string title, long sortKey) => new()
    {
        WorkspaceId = core.Workspace.Id,
        ProjectId = core.Project.Id,
        WorkflowStageId = stage.Id,
        Title = title,
        SortKey = sortKey,
        CreatedByUserId = core.User.Id,
        PrimaryAssigneeUserId = core.User.Id,
        Priority = TaskPriority.Medium,
        VersionNo = 1
    };

    private static AppDbContext CreateTenantContext(
        string connectionString,
        Tenant tenant,
        params IInterceptor[] interceptors)
    {
        var scope = new CurrentTenantService();
        scope.SetTenant(tenant.Id, tenant.Slug);
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString);
        if (interceptors.Length > 0)
            options.AddInterceptors(interceptors);
        return new AppDbContext(options.Options, scope);
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

    private sealed class AfterMilestoneCountInterceptor(
        Func<CancellationToken, Task> insert) : DbCommandInterceptor
    {
        private int _inserted;
        public bool Inserted => Volatile.Read(ref _inserted) != 0;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("COUNT", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("FROM milestones", StringComparison.OrdinalIgnoreCase) &&
                Interlocked.Exchange(ref _inserted, 1) == 0)
            {
                await insert(cancellationToken);
            }

            return result;
        }
    }

    private static string NormalizeSql(string sql) =>
        string.Join(
            ' ',
            sql.Split(
                [' ', '\t', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private sealed record LegacyGanttIds(Guid ProjectId, Guid MilestoneId);
    private sealed record ProjectCore(Tenant Tenant, Workspace Workspace, Project Project, User User);
    private sealed record GanttGraph(
        Tenant Tenant,
        Project Project,
        User User,
        Guid ParentTaskId,
        Guid UnscheduledTaskId,
        Guid DatedMilestoneId,
        Guid DoneTaskId,
        Guid CancelledTaskId);
}
