using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Projects;
using Coglatas.Application.Realtime;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Tests.Projects;

[Trait("Scope", "TaskV1PR05")]
public sealed class ProjectKanbanServiceTests
{
    [Fact]
    public async Task SnapshotIsBoundedOrderedAndExcludesOldDoneDeletedAndArchivedData()
    {
        await using var fixture = await Fixture.CreateAsync();
        var todo = fixture.Stage(TaskStageCategory.Todo);
        var done = fixture.Stage(TaskStageCategory.Done);
        var parent = await fixture.AddTaskAsync("Parent", todo, 1000);
        await fixture.AddTaskAsync("Child", done, 1000, parent.Id, completedAt: fixture.Clock.UtcNow.AddDays(-2));
        await fixture.AddTaskAsync("Old done", done, 2000, completedAt: fixture.Clock.UtcNow.AddDays(-31));
        await fixture.AddTaskAsync("Deleted", todo, 2000, deleted: true);
        var deletedParent = await fixture.AddTaskAsync("Restricted deleted parent", todo, 3000, deleted: true);
        var visibleOrphan = await fixture.AddTaskAsync("Visible child of deleted parent", todo, 4000, deletedParent.Id);

        var result = await fixture.Service.GetAsync(fixture.Project.Id, new(MaxCards: 1));

        Assert.True(result.IsSuccess);
        var snapshot = result.Value!;
        Assert.Equal(fixture.Project.Id, snapshot.Board.ProjectId);
        Assert.True(snapshot.Board.IsTruncated);
        Assert.Single(snapshot.Cards);
        Assert.Equal(parent.Id, snapshot.Cards[0].TaskId);
        Assert.True(snapshot.Cards[0].IsParentSummary);
        Assert.Equal(1, snapshot.Cards[0].ChildCount);
        Assert.DoesNotContain(snapshot.Cards, card => card.Summary is "Old done" or "Deleted");
        Assert.Equal(snapshot.Columns.OrderBy(column => column.DisplayOrder).Select(column => column.WorkflowStageId), snapshot.Columns.Select(column => column.WorkflowStageId));

        var older = await fixture.Service.GetAsync(fixture.Project.Id, new(IncludeOlderCompleted: true));
        Assert.Contains(older.Value!.Cards, card => card.Summary == "Old done");
        var orphanCard = Assert.Single(older.Value.Cards, card => card.TaskId == visibleOrphan.Id);
        Assert.Null(orphanCard.ParentTaskId);
        Assert.Null(orphanCard.ParentSummary);
        Assert.DoesNotContain(older.Value.Cards, card => card.Summary == "Restricted deleted parent");

        fixture.Project.Status = ProjectStatus.Archived;
        await fixture.Context.SaveChangesAsync();
        var archived = await fixture.Service.GetAsync(fixture.Project.Id, new());
        Assert.StartsWith("KANBAN_NOT_FOUND|", archived.Error);
    }

    [Fact]
    public async Task SnapshotDenialAndRevokedAccessReturnSameNonLeakingNotFound()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddTaskAsync("Private", fixture.Stage(TaskStageCategory.Todo), 1000);
        fixture.Authorization.ViewAllowed = false;

        var denied = await fixture.Service.GetAsync(fixture.Project.Id, new());
        var unknown = await fixture.Service.GetAsync(Guid.NewGuid(), new());

        Assert.Equal("KANBAN_NOT_FOUND|Project board not found.", denied.Error);
        Assert.Equal(denied.Error, unknown.Error);
    }

    [Fact]
    public async Task SnapshotRepositoryCannotCrossTheCurrentTenantBoundary()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.TenantScope.SetPlatformScope();
        var otherTenant = new Tenant { Name = "Other", DisplayName = "Other", Slug = $"other-{Guid.NewGuid():N}" };
        fixture.Context.Tenants.Add(otherTenant);
        await fixture.Context.SaveChangesAsync();
        var otherWorkspace = new Workspace { TenantId = otherTenant.Id, Name = "Other", Slug = $"other-{Guid.NewGuid():N}", CreatedByUserId = fixture.Actor };
        fixture.Context.Workspaces.Add(otherWorkspace);
        await fixture.Context.SaveChangesAsync();
        var otherProject = new Project { TenantId = otherTenant.Id, WorkspaceId = otherWorkspace.Id, OwnerUserId = fixture.Actor, CreatedByUserId = fixture.Actor, Name = "Other", Slug = $"other-{Guid.NewGuid():N}" };
        fixture.Context.Projects.Add(otherProject);
        await fixture.Context.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();
        fixture.TenantScope.SetTenant(fixture.Tenant.Id, fixture.Tenant.Slug);

        var read = await new ProjectKanbanRepository(fixture.Context).ReadAsync(
            otherProject.Id,
            fixture.Clock.UtcNow.AddDays(-30),
            false,
            null,
            null,
            null,
            null,
            100);

        Assert.Null(read);
    }

    [Fact]
    public async Task SnapshotProjectsHierarchySwimlanesPermissionsAndWarningOnlyWip()
    {
        await using var fixture = await Fixture.CreateAsync();
        var todo = fixture.Stage(TaskStageCategory.Todo);
        todo.WipWarningLimit = 1;
        await fixture.Context.SaveChangesAsync();
        var parent = await fixture.AddTaskAsync("Parent", todo, 1000);
        parent.ProgressPercent = 99;
        parent.PlannedStartDate = new(2025, 1, 1);
        parent.PlannedEndDate = new(2025, 1, 2);
        await fixture.Context.SaveChangesAsync();
        var childA = await fixture.AddTaskAsync("Child A", todo, 2000, parent.Id, assignee: fixture.Actor);
        childA.ProgressPercent = 20;
        childA.EstimatedEffortMinutes = 10;
        childA.PlannedStartDate = new(2026, 7, 1);
        childA.PlannedEndDate = new(2026, 7, 10);
        var childB = await fixture.AddTaskAsync("Child B", todo, 3000, parent.Id);
        childB.ProgressPercent = 60;
        childB.EstimatedEffortMinutes = 30;
        childB.PlannedStartDate = new(2026, 7, 3);
        childB.PlannedEndDate = new(2026, 7, 31);
        var cancelledChild = await fixture.AddTaskAsync(
            "Cancelled child",
            fixture.Stage(TaskStageCategory.Cancelled),
            1000,
            parent.Id);
        cancelledChild.ProgressPercent = 100;
        cancelledChild.EstimatedEffortMinutes = 100;
        cancelledChild.PlannedStartDate = new(2026, 6, 15);
        cancelledChild.PlannedEndDate = new(2026, 8, 15);
        await fixture.Context.SaveChangesAsync();

        var result = await fixture.Service.GetAsync(fixture.Project.Id, new(Swimlane: ProjectKanbanSwimlane.ParentTask));

        Assert.True(result.IsSuccess);
        var snapshot = result.Value!;
        var column = Assert.Single(snapshot.Columns, item => item.WorkflowStageId == todo.Id);
        Assert.True(column.HasWipWarning);
        Assert.Equal(3, column.CurrentAuthorizedCardCount);
        Assert.Contains(snapshot.Board.Warnings, warning => warning.Code == "KANBAN_WIP_LIMIT_EXCEEDED");
        Assert.All(snapshot.Cards.Where(card => card.ParentTaskId == parent.Id), card =>
        {
            Assert.Equal(parent.Id.ToString("D"), card.SwimlaneKey);
            Assert.Equal("Parent", card.SwimlaneLabel);
        });
        var parentCard = Assert.Single(snapshot.Cards, card => card.TaskId == parent.Id);
        Assert.True(parentCard.IsParentSummary);
        Assert.False(parentCard.IsLeaf);
        Assert.Equal(3, parentCard.ChildCount);
        Assert.Equal(0, parentCard.CompletedChildCount);
        Assert.Equal(50, parentCard.ProgressPercent);
        Assert.Equal(new DateOnly(2026, 6, 15), parentCard.PlannedStartDate);
        Assert.Equal(new DateOnly(2026, 8, 15), parentCard.PlannedEndDate);
        Assert.Contains(snapshot.Cards, card => card.Summary == "Child A" && card.UiPermissions.CanMove);
    }

    [Fact]
    public async Task SnapshotFiltersDoNotChangeAuthoritativeWipCount()
    {
        await using var fixture = await Fixture.CreateAsync();
        var todo = fixture.Stage(TaskStageCategory.Todo);
        todo.WipWarningLimit = 1;
        await fixture.Context.SaveChangesAsync();
        await fixture.AddTaskAsync("Mine", todo, 1000, assignee: fixture.Actor);
        await fixture.AddTaskAsync("Other", todo, 2000, assignee: Guid.NewGuid());

        var result = await fixture.Service.GetAsync(fixture.Project.Id, new(PrimaryAssigneeUserId: fixture.Actor));

        Assert.Single(result.Value!.Cards);
        var column = Assert.Single(result.Value.Columns, item => item.WorkflowStageId == todo.Id);
        Assert.Equal(2, column.CurrentAuthorizedCardCount);
        Assert.True(column.HasWipWarning);
    }

    [Fact]
    public async Task ManagerCanConfigureCompleteColumnsDefaultSwimlaneAndWipWithAuditAndInvalidation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var definition = await fixture.DefinitionAsync();
        var stages = fixture.Stages().AsEnumerable().Reverse().ToList();
        var request = new UpdateProjectKanbanConfigRequest(
            definition.VersionNo,
            ProjectKanbanSwimlane.Priority,
            stages.Select((stage, index) => new ProjectKanbanStageConfig(stage.Id, index, index == 0 ? 2 : null)).ToList());

        var result = await fixture.Service.UpdateConfigAsync(fixture.Project.Id, request);

        Assert.True(result.IsSuccess);
        Assert.Equal(ProjectKanbanSwimlane.Priority, result.Value!.Snapshot.Board.DefaultSwimlane);
        Assert.Equal(stages.Select(stage => stage.Id), result.Value.Snapshot.Columns.Select(column => column.WorkflowStageId));
        Assert.Equal(2, result.Value.Snapshot.Columns[0].WipWarningLimit);
        Assert.Equal(definition.VersionNo, result.Value.Snapshot.Board.Version);
        Assert.Contains(fixture.Audit.Entries, entry => entry.Action == "ProjectKanbanConfigured");
        Assert.Contains(fixture.Invalidations.ProjectChanges, change => change.Change == "kanbanConfigurationChanged");
    }

    [Fact]
    public async Task ConfigRequiresManagerCompleteStageSetAndFreshBoardVersionWithoutPartialPersistence()
    {
        await using var fixture = await Fixture.CreateAsync();
        var definition = await fixture.DefinitionAsync();
        var original = fixture.Stages().Select(stage => (stage.Id, stage.SortKey, stage.WipWarningLimit)).ToArray();
        fixture.Authorization.ManageAllowed = false;
        var denied = await fixture.Service.UpdateConfigAsync(fixture.Project.Id, new(
            definition.VersionNo,
            ProjectKanbanSwimlane.None,
            fixture.Stages().Select((stage, index) => new ProjectKanbanStageConfig(stage.Id, index, null)).ToList()));
        Assert.StartsWith("KANBAN_FORBIDDEN|", denied.Error);

        fixture.Authorization.ManageAllowed = true;
        var incomplete = await fixture.Service.UpdateConfigAsync(fixture.Project.Id, new(
            definition.VersionNo,
            ProjectKanbanSwimlane.None,
            [new(fixture.Stages()[0].Id, 0, 3)]));
        Assert.StartsWith("KANBAN_INVALID_COLUMNS|", incomplete.Error);

        var stale = await fixture.Service.UpdateConfigAsync(fixture.Project.Id, new(
            definition.VersionNo + 1,
            ProjectKanbanSwimlane.None,
            fixture.Stages().Select((stage, index) => new ProjectKanbanStageConfig(stage.Id, index, null)).ToList()));
        Assert.StartsWith("KANBAN_STALE_BOARD|", stale.Error);
        Assert.Equal(original, fixture.Stages().Select(stage => (stage.Id, stage.SortKey, stage.WipWarningLimit)).ToArray());
        Assert.Empty(fixture.Audit.Entries);
    }

    [Fact]
    public async Task MoveWithinStagePersistsStableBeforeOrderAndAdvancesBoardAndTaskVersions()
    {
        await using var fixture = await Fixture.CreateAsync();
        var todo = fixture.Stage(TaskStageCategory.Todo);
        var first = await fixture.AddTaskAsync("First", todo, 1000, assignee: fixture.Actor);
        var second = await fixture.AddTaskAsync("Second", todo, 2000, assignee: fixture.Actor);
        var third = await fixture.AddTaskAsync("Third", todo, 3000, assignee: fixture.Actor);
        var boardVersion = (await fixture.DefinitionAsync()).VersionNo;

        var result = await fixture.Service.MoveAsync(third.Id, new(todo.Id, first.Id, null, third.VersionNo, boardVersion));

        Assert.True(result.IsSuccess);
        var ordered = result.Value!.Snapshot.Cards.Where(card => card.WorkflowStageId == todo.Id).OrderBy(card => card.BoardOrder).Select(card => card.TaskId).ToArray();
        Assert.Equal([third.Id, first.Id, second.Id], ordered);
        Assert.Equal(2, result.Value.Snapshot.Cards.Single(card => card.TaskId == third.Id).Version);
        Assert.Equal(boardVersion + 1, result.Value.Snapshot.Board.Version);
        Assert.Contains(fixture.Audit.Entries, entry => entry.Action == "TaskKanbanMoved" && entry.EntityId == third.Id);
        Assert.Contains(fixture.Invalidations.TaskChanges, change => change.TaskId == third.Id && change.Change == "kanbanMoved");
    }

    [Fact]
    public async Task RepeatedMoveIsDeterministicAndGapExhaustionUsesBoundedRebalance()
    {
        await using var fixture = await Fixture.CreateAsync();
        var todo = fixture.Stage(TaskStageCategory.Todo);
        var first = await fixture.AddTaskAsync("First", todo, 1, assignee: fixture.Actor);
        var second = await fixture.AddTaskAsync("Second", todo, 2, assignee: fixture.Actor);
        var moving = await fixture.AddTaskAsync("Moving", todo, 3, assignee: fixture.Actor);
        var board = await fixture.DefinitionAsync();

        var moved = await fixture.Service.MoveAsync(moving.Id, new(todo.Id, second.Id, first.Id, moving.VersionNo, board.VersionNo));
        Assert.True(moved.IsSuccess);
        var firstOrder = moved.Value!.Snapshot.Cards.OrderBy(card => card.BoardOrder).Select(card => card.TaskId).ToArray();
        Assert.Equal([first.Id, moving.Id, second.Id], firstOrder);
        Assert.Contains(fixture.Audit.Entries, entry => entry.Action == "ProjectKanbanOrderRebalanced");
        Assert.Equal(firstOrder.Length, firstOrder.Distinct().Count());

        var movedCard = moved.Value.Snapshot.Cards.Single(card => card.TaskId == moving.Id);
        var noOp = await fixture.Service.MoveAsync(moving.Id, new(todo.Id, second.Id, first.Id, movedCard.Version, moved.Value.Snapshot.Board.Version));
        Assert.True(noOp.IsSuccess);
        Assert.Equal(moved.Value.Snapshot.Board.Version, noOp.Value!.Snapshot.Board.Version);
        Assert.Equal(firstOrder, noOp.Value.Snapshot.Cards.OrderBy(card => card.BoardOrder).Select(card => card.TaskId));
    }

    [Fact]
    public async Task CrossStageMoveUsesCanonicalAssigneeReviewDoneAndReopenGuards()
    {
        await using var fixture = await Fixture.CreateAsync();
        var todo = fixture.Stage(TaskStageCategory.Todo);
        var active = fixture.Stage(TaskStageCategory.InProgress);
        var review = fixture.Stage(TaskStageCategory.Review);
        var done = fixture.Stage(TaskStageCategory.Done);
        var task = await fixture.AddTaskAsync("Guarded", todo, 1000);
        var board = await fixture.DefinitionAsync();

        var noAssignee = await fixture.Service.MoveAsync(task.Id, new(active.Id, null, null, task.VersionNo, board.VersionNo));
        Assert.StartsWith("TASK_ASSIGNEE_REQUIRED|", noAssignee.Error);

        task.PrimaryAssigneeUserId = fixture.Actor;
        task.ReviewerUserId = Guid.NewGuid();
        task.ReviewStatus = TaskReviewStatus.Submitted;
        await fixture.Context.SaveChangesAsync();
        board = await fixture.DefinitionAsync();
        var needsReview = await fixture.Service.MoveAsync(task.Id, new(done.Id, null, null, task.VersionNo, board.VersionNo));
        Assert.StartsWith("TASK_REVIEW_REQUIRED|", needsReview.Error);

        task.ReviewStatus = TaskReviewStatus.Accepted;
        await fixture.Context.SaveChangesAsync();
        board = await fixture.DefinitionAsync();
        var completed = await fixture.Service.MoveAsync(task.Id, new(done.Id, null, null, task.VersionNo, board.VersionNo));
        Assert.True(completed.IsSuccess);
        Assert.Equal(100, task.ProgressPercent);
        Assert.NotNull(task.CompletedAt);

        var completedCard = completed.Value!.Snapshot.Cards.Single(card => card.TaskId == task.Id);
        var directActive = await fixture.Service.MoveAsync(task.Id, new(review.Id, null, null, completedCard.Version, completed.Value.Snapshot.Board.Version));
        Assert.StartsWith("TASK_TRANSITION_GUARD_FAILED|", directActive.Error);

        var reopened = await fixture.Service.MoveAsync(task.Id, new(todo.Id, null, null, completedCard.Version, completed.Value.Snapshot.Board.Version));
        Assert.True(reopened.IsSuccess);
        Assert.Null(task.CompletedAt);
        Assert.Equal(0, task.ProgressPercent);
    }

    [Fact]
    public async Task ParentMoveToDoneReusesCanonicalIncompleteChildGuardWithoutMovingTheChild()
    {
        await using var fixture = await Fixture.CreateAsync();
        var todo = fixture.Stage(TaskStageCategory.Todo);
        var done = fixture.Stage(TaskStageCategory.Done);
        var parent = await fixture.AddTaskAsync("Parent", todo, 1000, assignee: fixture.Actor);
        var child = await fixture.AddTaskAsync("Incomplete child", todo, 2000, parent.Id, assignee: fixture.Actor);
        var board = await fixture.DefinitionAsync();

        var result = await fixture.Service.MoveAsync(parent.Id, new(done.Id, null, null, parent.VersionNo, board.VersionNo));

        Assert.StartsWith("TASK_TRANSITION_GUARD_FAILED|", result.Error);
        Assert.Equal(todo.Id, parent.WorkflowStageId);
        Assert.Equal(todo.Id, child.WorkflowStageId);
        Assert.Equal(1, parent.VersionNo);
        Assert.Equal(1, child.VersionNo);
        Assert.Empty(fixture.Audit.Entries);
        Assert.Empty(fixture.Invalidations.TaskChanges);
    }

    [Fact]
    public async Task MoveRejectsStaleVersionsInvalidStageAndCrossProjectNeighborWithoutExistenceLeak()
    {
        await using var fixture = await Fixture.CreateAsync();
        var todo = fixture.Stage(TaskStageCategory.Todo);
        var task = await fixture.AddTaskAsync("Move me", todo, 1000, assignee: fixture.Actor);
        var otherProject = await fixture.AddProjectAsync("other");
        var otherStage = (await fixture.Context.TaskWorkflowStages.FirstAsync(stage => stage.ProjectId == otherProject.Id && stage.InternalCategory == TaskStageCategory.Todo));
        var otherTask = await fixture.AddTaskAsync("Other", otherStage, 1000, project: otherProject, assignee: fixture.Actor);
        var board = await fixture.DefinitionAsync();

        Assert.StartsWith("TASK_STALE_VERSION|", (await fixture.Service.MoveAsync(task.Id, new(todo.Id, null, null, task.VersionNo + 1, board.VersionNo))).Error);
        Assert.StartsWith("KANBAN_STALE_BOARD|", (await fixture.Service.MoveAsync(task.Id, new(todo.Id, null, null, task.VersionNo, board.VersionNo + 1))).Error);
        Assert.StartsWith("TASK_INVALID_STAGE|", (await fixture.Service.MoveAsync(task.Id, new(Guid.NewGuid(), null, null, task.VersionNo, board.VersionNo))).Error);
        var crossProject = await fixture.Service.MoveAsync(task.Id, new(todo.Id, otherTask.Id, null, task.VersionNo, board.VersionNo));
        var unknown = await fixture.Service.MoveAsync(task.Id, new(todo.Id, Guid.NewGuid(), null, task.VersionNo, board.VersionNo));
        Assert.Equal("KANBAN_INVALID_POSITION|The requested card position is invalid.", crossProject.Error);
        Assert.Equal(crossProject.Error, unknown.Error);
    }

    [Fact]
    public async Task MoveMutatesOnlyStageOrderAndCanonicalTransitionFieldsAndWipNeverRejects()
    {
        await using var fixture = await Fixture.CreateAsync();
        var todo = fixture.Stage(TaskStageCategory.Todo);
        var active = fixture.Stage(TaskStageCategory.InProgress);
        active.WipWarningLimit = 1;
        await fixture.Context.SaveChangesAsync();
        await fixture.AddTaskAsync("Existing active", active, 1000, assignee: fixture.Actor);
        var task = await fixture.AddTaskAsync("Move me", todo, 1000, assignee: fixture.Actor);
        task.Description = "private";
        task.Priority = TaskPriority.Critical;
        task.TargetGroupId = Guid.NewGuid();
        task.IsBlocked = true;
        task.BlockedReason = "blocked";
        task.PlannedStartDate = new(2026, 7, 1);
        task.PlannedEndDate = new(2026, 7, 9);
        await fixture.Context.SaveChangesAsync();
        var expectedVersion = task.VersionNo;
        var board = await fixture.DefinitionAsync();

        var result = await fixture.Service.MoveAsync(task.Id, new(active.Id, null, null, expectedVersion, board.VersionNo));

        Assert.True(result.IsSuccess);
        Assert.Equal("private", task.Description);
        Assert.Equal(TaskPriority.Critical, task.Priority);
        Assert.NotNull(task.TargetGroupId);
        Assert.True(task.IsBlocked);
        Assert.Equal("blocked", task.BlockedReason);
        Assert.Equal(new DateOnly(2026, 7, 1), task.PlannedStartDate);
        Assert.Equal(new DateOnly(2026, 7, 9), task.PlannedEndDate);
        Assert.Contains(result.Value!.Warnings, warning => warning.Code == "KANBAN_WIP_LIMIT_EXCEEDED");
    }

    [Fact]
    public async Task DeniedMoveCreatesNoAuditInvalidationOrPersistence()
    {
        await using var fixture = await Fixture.CreateAsync();
        var todo = fixture.Stage(TaskStageCategory.Todo);
        var task = await fixture.AddTaskAsync("Not mine", todo, 1000, assignee: Guid.NewGuid(), creator: Guid.NewGuid());
        fixture.Authorization.ManageAllowed = false;
        var board = await fixture.DefinitionAsync();

        var result = await fixture.Service.MoveAsync(task.Id, new(todo.Id, null, null, task.VersionNo, board.VersionNo));

        Assert.StartsWith("KANBAN_FORBIDDEN|", result.Error);
        Assert.Empty(fixture.Audit.Entries);
        Assert.Empty(fixture.Invalidations.TaskChanges);
        Assert.Empty(fixture.Invalidations.ProjectChanges);
    }

    [Theory]
    [InlineData(TaskCommandSaveResult.ConcurrencyConflict)]
    [InlineData(TaskCommandSaveResult.UniqueConflict)]
    public async Task SaveConflictClearsAttemptedStageOrderAndBoardVersionWithoutPartialPersistence(
        TaskCommandSaveResult saveResult)
    {
        await using var fixture = await Fixture.CreateAsync();
        var todo = fixture.Stage(TaskStageCategory.Todo);
        var active = fixture.Stage(TaskStageCategory.InProgress);
        var task = await fixture.AddTaskAsync("Concurrent", todo, 1000, assignee: fixture.Actor);
        var board = await fixture.DefinitionAsync();
        var service = fixture.ServiceWith(new FailingUnitOfWork(fixture.Context, saveResult));

        var result = await service.MoveAsync(task.Id, new(active.Id, null, null, task.VersionNo, board.VersionNo));

        Assert.StartsWith("KANBAN_CONFLICT|", result.Error);
        var persistedTask = await fixture.Context.TaskItems.SingleAsync(item => item.Id == task.Id);
        var persistedBoard = await fixture.Context.TaskWorkflowDefinitions.SingleAsync(item => item.ProjectId == fixture.Project.Id);
        Assert.Equal(todo.Id, persistedTask.WorkflowStageId);
        Assert.Equal(1000, persistedTask.SortKey);
        Assert.Equal(1, persistedTask.VersionNo);
        Assert.Equal(1, persistedBoard.VersionNo);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            AppDbContext context,
            CurrentTenantService tenantScope,
            Tenant tenant,
            Project project,
            Guid actor,
            FixedClock clock,
            FakeAuthorization authorization,
            FakeAudit audit,
            FakeInvalidations invalidations,
            ProjectKanbanService service)
        {
            Context = context;
            TenantScope = tenantScope;
            Tenant = tenant;
            Project = project;
            Actor = actor;
            Clock = clock;
            Authorization = authorization;
            Audit = audit;
            Invalidations = invalidations;
            Service = service;
        }

        public AppDbContext Context { get; }
        public CurrentTenantService TenantScope { get; }
        public Tenant Tenant { get; }
        public Project Project { get; }
        public Guid Actor { get; }
        public FixedClock Clock { get; }
        public FakeAuthorization Authorization { get; }
        public FakeAudit Audit { get; }
        public FakeInvalidations Invalidations { get; }
        public ProjectKanbanService Service { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var tenantScope = new CurrentTenantService();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options;
            var context = new AppDbContext(options, tenantScope);
            tenantScope.SetPlatformScope();
            var tenant = new Tenant { Name = "Kanban tenant", DisplayName = "Kanban tenant", Slug = $"kanban-{Guid.NewGuid():N}" };
            var actor = new User { DisplayName = "Kanban actor", Email = $"kanban-{Guid.NewGuid():N}@example.test", NormalizedEmail = $"KANBAN-{Guid.NewGuid():N}@EXAMPLE.TEST", PasswordHash = "hash" };
            context.AddRange(tenant, actor);
            await context.SaveChangesAsync();
            tenantScope.SetTenant(tenant.Id, tenant.Slug);
            var workspace = new Workspace { Name = "Kanban workspace", Slug = $"kanban-{Guid.NewGuid():N}", CreatedByUserId = actor.Id };
            context.Workspaces.Add(workspace);
            await context.SaveChangesAsync();
            var project = new Project { WorkspaceId = workspace.Id, OwnerUserId = actor.Id, CreatedByUserId = actor.Id, Name = "Kanban", Slug = $"kanban-{Guid.NewGuid():N}", Status = ProjectStatus.Active };
            context.Projects.Add(project);
            await context.SaveChangesAsync();

            var authorization = new FakeAuthorization();
            var audit = new FakeAudit();
            var invalidations = new FakeInvalidations();
            var clock = new FixedClock();
            var projectRepository = new ProjectRepository(context);
            var kanbanRepository = new ProjectKanbanRepository(context);
            var service = new ProjectKanbanService(
                projectRepository,
                kanbanRepository,
                authorization,
                new FakeCurrentUser(actor.Id),
                clock,
                new UtcTimeZoneResolver(),
                audit,
                invalidations,
                new EfUnitOfWork(context));
            return new(context, tenantScope, tenant, project, actor.Id, clock, authorization, audit, invalidations, service);
        }

        public TaskWorkflowStage Stage(TaskStageCategory category) =>
            Context.TaskWorkflowStages.Local.Single(stage => stage.ProjectId == Project.Id && stage.InternalCategory == category);
        public List<TaskWorkflowStage> Stages() => Context.TaskWorkflowStages.Local.Where(stage => stage.ProjectId == Project.Id).OrderBy(stage => stage.SortKey).ToList();
        public Task<TaskWorkflowDefinition> DefinitionAsync() => Context.TaskWorkflowDefinitions.SingleAsync(definition => definition.ProjectId == Project.Id);
        public ProjectKanbanService ServiceWith(ITaskCommandUnitOfWork unitOfWork) => new(
            new ProjectRepository(Context),
            new ProjectKanbanRepository(Context),
            Authorization,
            new FakeCurrentUser(Actor),
            Clock,
            new UtcTimeZoneResolver(),
            Audit,
            Invalidations,
            unitOfWork);

        public async Task<Project> AddProjectAsync(string name)
        {
            var project = new Project { WorkspaceId = Project.WorkspaceId, OwnerUserId = Actor, CreatedByUserId = Actor, Name = name, Slug = $"{name}-{Guid.NewGuid():N}", Status = ProjectStatus.Active };
            Context.Projects.Add(project);
            await Context.SaveChangesAsync();
            return project;
        }

        public async Task<TaskItem> AddTaskAsync(
            string title,
            TaskWorkflowStage stage,
            long sortKey,
            Guid? parentId = null,
            DateTimeOffset? completedAt = null,
            bool deleted = false,
            Guid? assignee = null,
            Guid? creator = null,
            Project? project = null)
        {
            project ??= Project;
            var task = new TaskItem
            {
                WorkspaceId = project.WorkspaceId,
                ProjectId = project.Id,
                ParentTaskItemId = parentId,
                WorkflowStageId = stage.Id,
                Title = title,
                SortKey = sortKey,
                CreatedByUserId = creator ?? Actor,
                PrimaryAssigneeUserId = assignee,
                CompletedAt = completedAt,
                Status = stage.InternalCategory == TaskStageCategory.Done ? TaskItemStatus.Completed : TaskItemStatus.NotStarted,
                ProgressPercent = stage.InternalCategory == TaskStageCategory.Done ? 100 : 0
            };
            if (deleted) task.MarkDeleted(Clock.UtcNow);
            Context.TaskItems.Add(task);
            await Context.SaveChangesAsync();
            return task;
        }

        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }

    private sealed class FakeAuthorization : IProjectAuthorizationService
    {
        public bool ViewAllowed { get; set; } = true;
        public bool ManageAllowed { get; set; } = true;
        public Task<bool> CanViewProject(Guid userId, Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(ViewAllowed);
        public Task<bool> CanManageProject(Guid userId, Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(ManageAllowed);
        public Task<bool> CanCreateProject(Guid userId, Guid workspaceId, Guid groupId, CancellationToken cancellationToken = default) => Task.FromResult(ManageAllowed);
    }
    private sealed class FakeCurrentUser(Guid id) : ICurrentUser
    {
        public Guid? UserId => id;
        public Guid? SessionId => null;
        public string? Email => null;
        public SystemRole? SystemRole => null;
        public bool IsAuthenticated => true;
    }
    private sealed class FixedClock : IClock { public DateTimeOffset UtcNow => new(2026, 7, 29, 0, 0, 0, TimeSpan.Zero); }
    private sealed class UtcTimeZoneResolver : ITaskWorkspaceTimeZoneResolver
    {
        public Task<TimeZoneInfo> ResolveAsync(Guid tenantId, Guid workspaceId, CancellationToken cancellationToken = default) => Task.FromResult(TimeZoneInfo.Utc);
    }
    private sealed class FakeAudit : IAuditLogger
    {
        public List<AuditLogEntry> Entries { get; } = [];
        public Task LogAsync(AuditLogEntry entry, CancellationToken cancellationToken = default) { Entries.Add(entry); return Task.CompletedTask; }
    }
    private sealed class FakeInvalidations : IBusinessInvalidationPublisher
    {
        public List<(Guid TaskId, string Change)> TaskChanges { get; } = [];
        public List<(Guid ProjectId, string Change)> ProjectChanges { get; } = [];
        public Task TaskChangedAsync(TaskItem task, Guid actorUserId, string change, IEnumerable<string>? changedFields = null, IEnumerable<Guid>? affectedUserIds = null, CancellationToken cancellationToken = default) { TaskChanges.Add((task.Id, change)); return Task.CompletedTask; }
        public Task ProjectChangedAsync(Project project, Guid actorUserId, string change, CancellationToken cancellationToken = default) { ProjectChanges.Add((project.Id, change)); return Task.CompletedTask; }
        public Task AnnouncementChangedAsync(Announcement announcement, Guid actorUserId, string change, IEnumerable<Guid> audienceUserIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FileChangedAsync(FileObject fileObject, Attachment attachment, Guid actorUserId, string change, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class FailingUnitOfWork(
        AppDbContext context,
        TaskCommandSaveResult saveResult) : ITaskCommandUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<TaskCommandSaveOutcome> SaveTaskCommandAsync(CancellationToken cancellationToken = default)
        {
            context.ChangeTracker.Clear();
            return Task.FromResult(new TaskCommandSaveOutcome(saveResult));
        }
        public void ClearTaskCommandTracking() => context.ChangeTracker.Clear();
    }
}
