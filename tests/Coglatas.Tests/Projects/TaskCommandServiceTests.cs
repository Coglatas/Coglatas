using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Projects;
using Coglatas.Application.Realtime;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;

namespace Coglatas.Tests.Projects;

public sealed class TaskCommandServiceTests
{
    [Fact]
    public async Task StructuredBriefPatchPreservesOmittedFieldsClearsExplicitNullAndReturnsPerFieldProvenance()
    {
        var fixture = Fixture.Create();
        var task = fixture.AddTask("brief");
        task.Description = "Legacy free-form notes";
        task.BriefGoal = "Old goal";
        task.BriefDeliverable = "Existing deliverable";
        task.BriefConstraints = "Old constraints";

        var result = await fixture.Service.UpdateDetailsAsync(
            task.Id,
            new TaskUpdateDetailsRequest(
                task.Title,
                task.Description,
                task.Priority,
                null,
                null,
                0,
                task.VersionNo,
                Goal: "  Updated goal  ",
                Deliverable: default,
                Constraints: new OptionalString(true, null)));

        Assert.True(result.IsSuccess);
        Assert.Equal("Legacy free-form notes", task.Description);
        Assert.Equal("Updated goal", task.BriefGoal);
        Assert.Equal("Existing deliverable", task.BriefDeliverable);
        Assert.Null(task.BriefConstraints);
        Assert.Equal(TaskBriefValueSource.TaskSpecific, result.Value!.Brief!.Goal.Source);
        Assert.Equal("Updated goal", result.Value.Brief.Goal.Value);
        Assert.Equal(TaskBriefValueSource.TaskSpecific, result.Value.Brief.Deliverable.Source);
        Assert.Equal("Existing deliverable", result.Value.Brief.Deliverable.Value);
        Assert.Equal(TaskBriefValueSource.NotSet, result.Value.Brief.Constraints.Source);
        Assert.Null(result.Value.Brief.Constraints.Value);

        var semantic = Assert.Single(fixture.Invalidations.TaskChanges);
        Assert.Contains("briefGoal", semantic.ChangedFields);
        Assert.Contains("briefConstraints", semantic.ChangedFields);
        Assert.DoesNotContain("briefDeliverable", semantic.ChangedFields);
        Assert.DoesNotContain(semantic.ChangedFields, field => field.Contains("Updated goal", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Audit.Entries, entry =>
            entry.Metadata?.Values.Any(value => string.Equals(value?.ToString(), "Updated goal", StringComparison.Ordinal)) == true);
    }

    [Theory]
    [InlineData("goal")]
    [InlineData("deliverable")]
    [InlineData("constraints")]
    public async Task EachStructuredBriefFieldRejectsOverLimitTextWithoutMutationOrSideEffects(string field)
    {
        var fixture = Fixture.Create();
        var task = fixture.AddTask("brief");
        task.BriefGoal = "goal";
        task.BriefDeliverable = "deliverable";
        task.BriefConstraints = "constraints";
        var oversized = new string('x', TaskBriefText.MaximumFieldLength + 1);

        var result = await fixture.Service.UpdateDetailsAsync(
            task.Id,
            new TaskUpdateDetailsRequest(
                task.Title,
                null,
                task.Priority,
                null,
                null,
                0,
                task.VersionNo,
                Goal: field == "goal" ? oversized : default(OptionalString),
                Deliverable: field == "deliverable" ? oversized : default(OptionalString),
                Constraints: field == "constraints" ? oversized : default(OptionalString)));

        Assert.False(result.IsSuccess);
        Assert.StartsWith("TASK_BRIEF_FIELD_TOO_LONG|", result.Error);
        Assert.Equal("goal", task.BriefGoal);
        Assert.Equal("deliverable", task.BriefDeliverable);
        Assert.Equal("constraints", task.BriefConstraints);
        Assert.Equal(1, task.VersionNo);
        Assert.Empty(fixture.Audit.Entries);
        Assert.Empty(fixture.Invalidations.TaskChanges);
        Assert.Equal(0, fixture.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task DescriptionOnlyUpdateRoundTripsWithAllStructuredBriefSourcesNotSet()
    {
        var fixture = Fixture.Create();
        var task = fixture.AddTask("legacy");

        var result = await fixture.Service.UpdateDetailsAsync(
            task.Id,
            new TaskUpdateDetailsRequest(task.Title, "  free-form notes  ", task.Priority, null, null, 0, task.VersionNo));

        Assert.True(result.IsSuccess);
        Assert.Equal("free-form notes", task.Description);
        Assert.Null(task.BriefGoal);
        Assert.Null(task.BriefDeliverable);
        Assert.Null(task.BriefConstraints);
        Assert.All(
            new[] { result.Value!.Brief!.Goal, result.Value.Brief.Deliverable, result.Value.Brief.Constraints },
            field =>
            {
                Assert.Null(field.Value);
                Assert.Equal(TaskBriefValueSource.NotSet, field.Source);
            });
    }

    [Fact]
    public async Task UnauthorizedCanonicalDetailDoesNotRevealStructuredBriefValues()
    {
        var fixture = Fixture.Create();
        var task = fixture.AddTask("restricted");
        task.BriefGoal = "Sensitive task-specific goal";
        var service = new TaskCommandService(
            fixture.Projects,
            new FakeGroups(),
            fixture.Users,
            new DeniedProjectAuthorization(),
            new AllowedTaskAuthorization(),
            new FakeCurrentUser(fixture.Actor),
            new FixedClock(),
            fixture.Audit,
            fixture.Invalidations,
            fixture.UnitOfWork,
            new UtcTimeZoneResolver());

        var result = await service.GetAsync(task.Id);

        Assert.False(result.IsSuccess);
        Assert.Equal("TASK_NOT_FOUND|Task not found.", result.Error);
        Assert.DoesNotContain("Sensitive", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CanonicalDetailTreatsLegacyBlankBriefStorageAsNotSet()
    {
        var fixture = Fixture.Create();
        var task = fixture.AddTask("legacy blank");
        task.BriefGoal = "   ";

        var result = await fixture.Service.GetAsync(task.Id);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value!.Brief!.Goal.Value);
        Assert.Equal(TaskBriefValueSource.NotSet, result.Value.Brief.Goal.Source);
    }

    [Fact]
    public async Task CollaboratorCommandsReconcileAgainstTheirEffectiveRelationshipSet()
    {
        var fixture = Fixture.Create();
        var task = fixture.AddTask("relationships");
        var collaborator = Guid.NewGuid();

        Assert.True((await fixture.Service.AddCollaboratorAsync(task.Id, new TaskCollaboratorRequest(collaborator, task.VersionNo))).IsSuccess);
        var added = fixture.Projects.Watches.Single(state => state.UserId == collaborator);
        Assert.Equal(WorkItemWatchAutomaticSource.Collaborator, added.AutomaticSources);
        Assert.True(added.IsWatching);

        Assert.True((await fixture.Service.RemoveCollaboratorAsync(task.Id, collaborator, task.VersionNo)).IsSuccess);
        var removed = fixture.Projects.Watches.Single(state => state.UserId == collaborator);
        Assert.Equal(WorkItemWatchAutomaticSource.None, removed.AutomaticSources);
        Assert.False(removed.IsWatching);
    }

    [Fact]
    public async Task ChildProgressMutationAdvancesParentAndQueuesBothObservableChanges()
    {
        var fixture = Fixture.Create();
        var parent = fixture.AddTask("parent");
        var child = fixture.AddTask("child", parent.Id, progress: 20);

        var result = await fixture.Service.UpdateDetailsAsync(child.Id, new TaskUpdateDetailsRequest("child", null, TaskPriority.Medium, null, null, 45, child.VersionNo));

        Assert.True(result.IsSuccess);
        Assert.Equal(2, parent.VersionNo);
        Assert.Equal(2, child.VersionNo);
        Assert.Equal(1, fixture.UnitOfWork.SaveCount);
        Assert.Contains(fixture.Audit.Entries, entry => entry.EntityId == child.Id && entry.Action == "TaskDetailsUpdated");
        Assert.Contains(fixture.Audit.Entries, entry => entry.EntityId == parent.Id && entry.Action == "TaskSubtasksChanged");
        Assert.Contains(fixture.Invalidations.TaskChanges, change => change.TaskId == child.Id && change.Change == "updated");
        Assert.Contains(fixture.Invalidations.TaskChanges, change => change.TaskId == parent.Id && change.Change == "subtasksChanged");
    }

    [Fact]
    public async Task ChildCancellationAdvancesParentInTheSameCommandBoundary()
    {
        var fixture = Fixture.Create();
        var parent = fixture.AddTask("parent");
        var child = fixture.AddTask("child", parent.Id, progress: 20);
        var cancelled = new TaskWorkflowStage { ProjectId = fixture.Project.Id, Name = "Cancelled", InternalCategory = TaskStageCategory.Cancelled };
        fixture.Projects.Stages[cancelled.Id] = cancelled;

        var result = await fixture.Service.TransitionAsync(child.Id, new TaskTransitionRequest(cancelled.Id, child.VersionNo, "No longer required"));

        Assert.True(result.IsSuccess);
        Assert.Equal(2, parent.VersionNo);
        Assert.Equal(TaskItemStatus.Cancelled, child.Status);
        Assert.Equal(1, fixture.UnitOfWork.SaveCount);
        Assert.Contains(fixture.Audit.Entries, entry => entry.EntityId == parent.Id && entry.Action == "TaskSubtasksChanged");
        Assert.Contains(fixture.Invalidations.TaskChanges, change => change.TaskId == parent.Id && change.Change == "subtasksChanged");
    }

    [Fact]
    public async Task ReopenFromDoneClearsCompletionAndResetsTodoProgress()
    {
        var fixture = Fixture.Create();
        var task = fixture.AddTask("reopen", progress: 0);
        var done = new TaskWorkflowStage { ProjectId = fixture.Project.Id, Name = "Done", InternalCategory = TaskStageCategory.Done };
        var todo = new TaskWorkflowStage { ProjectId = fixture.Project.Id, Name = "Todo", InternalCategory = TaskStageCategory.Todo };
        fixture.Projects.Stages[done.Id] = done;
        fixture.Projects.Stages[todo.Id] = todo;

        Assert.True((await fixture.Service.TransitionAsync(task.Id, new TaskTransitionRequest(done.Id, task.VersionNo))).IsSuccess);
        var reopened = await fixture.Service.TransitionAsync(task.Id, new TaskTransitionRequest(todo.Id, task.VersionNo));

        Assert.True(reopened.IsSuccess);
        Assert.Equal(TaskItemStatus.NotStarted, task.Status);
        Assert.Equal(0, task.ProgressPercent);
        Assert.Null(task.CompletedAt);
        Assert.Equal(3, task.VersionNo);
        Assert.Contains(fixture.Invalidations.TaskChanges, change => change.TaskId == task.Id && change.Change == "reopened");
    }

    [Fact]
    public async Task ReopenFromCancelledClearsTerminalMetadataAndResetsBacklogProgress()
    {
        var fixture = Fixture.Create();
        var task = fixture.AddTask("reopen", progress: 35);
        var cancelled = new TaskWorkflowStage { ProjectId = fixture.Project.Id, Name = "Cancelled", InternalCategory = TaskStageCategory.Cancelled };
        var backlog = new TaskWorkflowStage { ProjectId = fixture.Project.Id, Name = "Backlog", InternalCategory = TaskStageCategory.Backlog };
        fixture.Projects.Stages[cancelled.Id] = cancelled;
        fixture.Projects.Stages[backlog.Id] = backlog;

        Assert.True((await fixture.Service.TransitionAsync(task.Id, new TaskTransitionRequest(cancelled.Id, task.VersionNo, "deferred"))).IsSuccess);
        var reopened = await fixture.Service.TransitionAsync(task.Id, new TaskTransitionRequest(backlog.Id, task.VersionNo));

        Assert.True(reopened.IsSuccess);
        Assert.Equal(0, task.ProgressPercent);
        Assert.Null(task.CancelledAt);
        Assert.Null(task.CancellationReason);
        Assert.Contains(fixture.Invalidations.TaskChanges, change => change.TaskId == task.Id && change.Change == "reopened");
    }

    [Fact]
    [Trait("Scope", "TaskV1PR06")]
    public async Task TerminalChildCannotReopenUntilItsParentIsReopened()
    {
        var fixture = Fixture.Create();
        var parent = fixture.AddTask("parent", progress: 100);
        parent.Status = TaskItemStatus.Completed;
        var child = fixture.AddTask("child", parent.Id, progress: 100);
        child.Status = TaskItemStatus.Completed;
        var todo = new TaskWorkflowStage
        {
            ProjectId = fixture.Project.Id,
            Name = "Todo",
            InternalCategory = TaskStageCategory.Todo
        };
        fixture.Projects.Stages[todo.Id] = todo;

        var result = await fixture.Service.TransitionAsync(
            child.Id,
            new TaskTransitionRequest(todo.Id, child.VersionNo));

        Assert.StartsWith("TASK_TRANSITION_GUARD_FAILED|", result.Error);
        Assert.Equal(TaskItemStatus.Completed, child.Status);
        Assert.Equal(100, child.ProgressPercent);
        Assert.Equal(0, fixture.UnitOfWork.SaveCount);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR06")]
    public async Task ParentCompletionRequiresCanonicalDerivedProgressOfOneHundred()
    {
        var fixture = Fixture.Create();
        var parent = fixture.AddTask("parent");
        var cancelledChild = fixture.AddTask("cancelled", parent.Id, progress: 100);
        cancelledChild.Status = TaskItemStatus.Cancelled;
        var done = new TaskWorkflowStage
        {
            ProjectId = fixture.Project.Id,
            Name = "Done",
            InternalCategory = TaskStageCategory.Done
        };
        fixture.Projects.Stages[done.Id] = done;

        var allCancelled = await fixture.Service.TransitionAsync(
            parent.Id,
            new TaskTransitionRequest(done.Id, parent.VersionNo));
        var completedChild = fixture.AddTask("completed", parent.Id, progress: 100);
        completedChild.Status = TaskItemStatus.Completed;
        var derivedOneHundred = await fixture.Service.TransitionAsync(
            parent.Id,
            new TaskTransitionRequest(done.Id, parent.VersionNo));

        Assert.StartsWith("TASK_TRANSITION_GUARD_FAILED|", allCancelled.Error);
        Assert.True(derivedOneHundred.IsSuccess);
        Assert.Equal(TaskItemStatus.Completed, parent.Status);
        Assert.Equal(100, parent.ProgressPercent);
        Assert.Equal(1, fixture.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task TerminalTaskCannotTransitionDirectlyToActiveWork()
    {
        var fixture = Fixture.Create();
        var task = fixture.AddTask("terminal", progress: 0);
        var done = new TaskWorkflowStage { ProjectId = fixture.Project.Id, Name = "Done", InternalCategory = TaskStageCategory.Done };
        var inProgress = new TaskWorkflowStage { ProjectId = fixture.Project.Id, Name = "In progress", InternalCategory = TaskStageCategory.InProgress };
        fixture.Projects.Stages[done.Id] = done;
        fixture.Projects.Stages[inProgress.Id] = inProgress;

        Assert.True((await fixture.Service.TransitionAsync(task.Id, new TaskTransitionRequest(done.Id, task.VersionNo))).IsSuccess);
        var version = task.VersionNo;
        var result = await fixture.Service.TransitionAsync(task.Id, new TaskTransitionRequest(inProgress.Id, version));

        Assert.False(result.IsSuccess);
        Assert.StartsWith("TASK_TRANSITION_GUARD_FAILED|", result.Error);
        Assert.Equal(version, task.VersionNo);
        Assert.Equal(TaskItemStatus.Completed, task.Status);
        Assert.Equal(100, task.ProgressPercent);
        Assert.Equal(1, fixture.UnitOfWork.SaveCount);
        Assert.Single(fixture.Audit.Entries);
        Assert.Single(fixture.Invalidations.TaskChanges);
    }

    [Fact]
    public async Task AllCancelledChildrenStillRejectDirectParentDerivedFieldChangesButAllowTitleUpdate()
    {
        var fixture = Fixture.Create();
        var parent = fixture.AddTask("parent", progress: 37);
        var child = fixture.AddTask("child", parent.Id, progress: 100);
        child.Status = TaskItemStatus.Cancelled;

        var rejected = await fixture.Service.UpdateDetailsAsync(parent.Id, new TaskUpdateDetailsRequest("renamed", null, TaskPriority.High, null, null, 37, parent.VersionNo));
        var accepted = await fixture.Service.UpdateDetailsAsync(parent.Id, new TaskUpdateDetailsRequest("renamed", null, TaskPriority.High, null, null, 0, parent.VersionNo));

        Assert.False(rejected.IsSuccess);
        Assert.StartsWith("TASK_PROGRESS_DERIVED|", rejected.Error);
        Assert.True(accepted.IsSuccess);
        Assert.Equal("renamed", parent.Title);
    }

    [Fact]
    public async Task DoneDerivedParentAcceptsOrdinaryEditsUsingAuthoritativeDerivedValues()
    {
        var fixture = Fixture.Create();
        var parent = fixture.AddTask("parent", progress: 100);
        parent.Status = TaskItemStatus.Completed;
        parent.PlannedStartDate = new DateOnly(2026, 7, 1);
        parent.PlannedEndDate = new DateOnly(2026, 7, 2);
        var child = fixture.AddTask("child", parent.Id, progress: 100);
        child.Status = TaskItemStatus.Cancelled;
        child.PlannedStartDate = new DateOnly(2026, 7, 4);
        child.PlannedEndDate = new DateOnly(2026, 7, 8);

        var result = await fixture.Service.UpdateDetailsAsync(parent.Id, new TaskUpdateDetailsRequest(
            "renamed", "updated description", TaskPriority.High, child.PlannedStartDate, child.PlannedEndDate, 0, parent.VersionNo));

        Assert.True(result.IsSuccess);
        Assert.Equal("renamed", parent.Title);
        Assert.Equal("updated description", parent.Description);
        Assert.Equal(TaskPriority.High, parent.Priority);
        Assert.Equal(100, parent.ProgressPercent);
        Assert.Equal(new DateOnly(2026, 7, 1), parent.PlannedStartDate);
        Assert.Equal(new DateOnly(2026, 7, 2), parent.PlannedEndDate);
        Assert.Equal(0, result.Value!.ProgressPercent);
        Assert.True(result.Value.ProgressIsDerived);
        Assert.Equal(child.PlannedStartDate, result.Value.PlannedStartDate);
        Assert.Equal(child.PlannedEndDate, result.Value.PlannedEndDate);
        Assert.Equal(2, parent.VersionNo);
        Assert.Equal(1, fixture.UnitOfWork.SaveCount);
        Assert.Contains(fixture.Audit.Entries, entry => entry.EntityId == parent.Id && entry.Action == "TaskDetailsUpdated");
        Assert.Contains(fixture.Invalidations.TaskChanges, change => change.TaskId == parent.Id && change.Change == "updated");
    }

    [Fact]
    public async Task CancelledDerivedParentAcceptsOrdinaryEditsUsingAuthoritativeDerivedValues()
    {
        var fixture = Fixture.Create();
        var parent = fixture.AddTask("parent", progress: 73);
        parent.Status = TaskItemStatus.Cancelled;
        parent.PlannedStartDate = new DateOnly(2026, 7, 1);
        parent.PlannedEndDate = new DateOnly(2026, 7, 2);
        var child = fixture.AddTask("child", parent.Id, progress: 100);
        child.Status = TaskItemStatus.Cancelled;
        child.PlannedStartDate = new DateOnly(2026, 7, 4);
        child.PlannedEndDate = new DateOnly(2026, 7, 8);

        var result = await fixture.Service.UpdateDetailsAsync(parent.Id, new TaskUpdateDetailsRequest(
            "renamed", "updated description", TaskPriority.High, child.PlannedStartDate, child.PlannedEndDate, 0, parent.VersionNo));

        Assert.True(result.IsSuccess);
        Assert.Equal("renamed", parent.Title);
        Assert.Equal("updated description", parent.Description);
        Assert.Equal(TaskPriority.High, parent.Priority);
        Assert.Equal(73, parent.ProgressPercent);
        Assert.Equal(new DateOnly(2026, 7, 1), parent.PlannedStartDate);
        Assert.Equal(new DateOnly(2026, 7, 2), parent.PlannedEndDate);
        Assert.Equal(0, result.Value!.ProgressPercent);
        Assert.True(result.Value.ProgressIsDerived);
        Assert.Equal(child.PlannedStartDate, result.Value.PlannedStartDate);
        Assert.Equal(child.PlannedEndDate, result.Value.PlannedEndDate);
        Assert.Equal(2, parent.VersionNo);
        Assert.Equal(1, fixture.UnitOfWork.SaveCount);
    }

    [Theory]
    [InlineData(TaskItemStatus.Completed)]
    [InlineData(TaskItemStatus.Cancelled)]
    public async Task TerminalDerivedParentRejectsNonAuthoritativeDerivedFieldChangesWithoutSideEffects(TaskItemStatus parentStatus)
    {
        var fixture = Fixture.Create();
        var parent = fixture.AddTask("parent", progress: 100);
        parent.Status = parentStatus;
        var child = fixture.AddTask("child", parent.Id, progress: 100);
        child.Status = TaskItemStatus.Cancelled;
        child.PlannedStartDate = new DateOnly(2026, 7, 4);
        child.PlannedEndDate = new DateOnly(2026, 7, 8);

        var result = await fixture.Service.UpdateDetailsAsync(parent.Id, new TaskUpdateDetailsRequest(
            "renamed", "updated description", TaskPriority.High, child.PlannedStartDate, child.PlannedEndDate, 1, parent.VersionNo));

        Assert.False(result.IsSuccess);
        Assert.StartsWith("TASK_PROGRESS_DERIVED|", result.Error);
        Assert.Equal("parent", parent.Title);
        Assert.Null(parent.Description);
        Assert.Equal(TaskPriority.Medium, parent.Priority);
        Assert.Equal(1, parent.VersionNo);
        Assert.Empty(fixture.Audit.Entries);
        Assert.Empty(fixture.Invalidations.TaskChanges);
        Assert.Equal(0, fixture.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task LeafDoneRequiresOneHundredProgressButAllowsOrdinaryEditsAtOneHundred()
    {
        var fixture = Fixture.Create();
        var task = fixture.AddTask("task", progress: 100);
        task.Status = TaskItemStatus.Completed;

        var rejected = await fixture.Service.UpdateDetailsAsync(task.Id, new TaskUpdateDetailsRequest(
            "renamed", "updated description", TaskPriority.High, null, null, 99, task.VersionNo));

        Assert.False(rejected.IsSuccess);
        Assert.StartsWith("TASK_INVALID_PROGRESS|", rejected.Error);
        Assert.Equal("task", task.Title);
        Assert.Equal(1, task.VersionNo);
        Assert.Empty(fixture.Audit.Entries);
        Assert.Empty(fixture.Invalidations.TaskChanges);
        Assert.Equal(0, fixture.UnitOfWork.SaveCount);

        var accepted = await fixture.Service.UpdateDetailsAsync(task.Id, new TaskUpdateDetailsRequest(
            "renamed", "updated description", TaskPriority.High, null, null, 100, task.VersionNo));

        Assert.True(accepted.IsSuccess);
        Assert.Equal("renamed", task.Title);
        Assert.Equal(2, task.VersionNo);
    }

    [Fact]
    public async Task LeafCancelledRejectsProgressChangesButAllowsOrdinaryEditsAtSavedProgress()
    {
        var fixture = Fixture.Create();
        var task = fixture.AddTask("task", progress: 37);
        task.Status = TaskItemStatus.Cancelled;

        var rejected = await fixture.Service.UpdateDetailsAsync(task.Id, new TaskUpdateDetailsRequest(
            "renamed", "updated description", TaskPriority.High, null, null, 38, task.VersionNo));

        Assert.False(rejected.IsSuccess);
        Assert.StartsWith("TASK_INVALID_PROGRESS|", rejected.Error);
        Assert.Equal("task", task.Title);
        Assert.Equal(1, task.VersionNo);
        Assert.Empty(fixture.Audit.Entries);
        Assert.Empty(fixture.Invalidations.TaskChanges);
        Assert.Equal(0, fixture.UnitOfWork.SaveCount);

        var accepted = await fixture.Service.UpdateDetailsAsync(task.Id, new TaskUpdateDetailsRequest(
            "renamed", "updated description", TaskPriority.High, null, null, 37, task.VersionNo));

        Assert.True(accepted.IsSuccess);
        Assert.Equal("renamed", task.Title);
        Assert.Equal(2, task.VersionNo);
    }

    [Fact]
    public async Task SaveConflictDoesNotReturnSuccessAndUnrelatedRootIsUnchanged()
    {
        var fixture = Fixture.Create();
        var changed = fixture.AddTask("changed");
        var unrelated = fixture.AddTask("unrelated");
        fixture.UnitOfWork.Result = TaskCommandSaveResult.ConcurrencyConflict;

        var result = await fixture.Service.UpdateDetailsAsync(changed.Id, new TaskUpdateDetailsRequest("changed", null, TaskPriority.Medium, null, null, 10, changed.VersionNo));

        Assert.False(result.IsSuccess);
        Assert.StartsWith("TASK_STALE_VERSION|", result.Error);
        Assert.Equal(1, fixture.UnitOfWork.SaveCount);
        Assert.Equal(1, unrelated.VersionNo);
    }

    [Fact]
    public async Task CommitAsyncMapsOnlyTheWatchIdentityConstraintToStaleAndKeepsOtherUniqueConflictsGeneric()
    {
        var fixture = Fixture.Create();
        var task = fixture.AddTask("constraint mapping");
        fixture.UnitOfWork.Outcome = new TaskCommandSaveOutcome(TaskCommandSaveResult.UniqueConflict, "IX_unrelated_task_command_constraint");

        var unknown = await fixture.Service.UpdateDetailsAsync(task.Id, new TaskUpdateDetailsRequest("constraint mapping", null, TaskPriority.Medium, null, null, 0, task.VersionNo));
        Assert.False(unknown.IsSuccess);
        Assert.StartsWith("TASK_CONFLICT|", unknown.Error);

        var retry = fixture.AddTask("watch identity");
        fixture.UnitOfWork.Outcome = new TaskCommandSaveOutcome(TaskCommandSaveResult.UniqueConflict, TaskCommandConstraintNames.WorkItemWatchStateIdentity);
        var known = await fixture.Service.UpdateDetailsAsync(retry.Id, new TaskUpdateDetailsRequest("watch identity", null, TaskPriority.Medium, null, null, 0, retry.VersionNo));
        Assert.False(known.IsSuccess);
        Assert.StartsWith("TASK_STALE_VERSION|", known.Error);
    }

    [Fact]
    public async Task SubtaskCreationComposesChildCreatorWatchAndParentChangeInOneSave()
    {
        var fixture = Fixture.Create();
        var parent = fixture.AddTask("parent");

        var result = await fixture.Subresources.CreateSubtaskAsync(
            parent.Id,
            new CreateTaskSubtaskRequest(
                "child",
                "description",
                TaskPriority.High,
                "  Child goal  ",
                "  Child deliverable  ",
                "  Child constraints  "));

        Assert.True(result.IsSuccess);
        var child = Assert.Single(fixture.Projects.Tasks.Values, task => task.ParentTaskItemId == parent.Id);
        Assert.Equal(parent.Id, child.ParentTaskItemId);
        Assert.Equal("description", child.Description);
        Assert.Equal("Child goal", child.BriefGoal);
        Assert.Equal("Child deliverable", child.BriefDeliverable);
        Assert.Equal("Child constraints", child.BriefConstraints);
        Assert.Equal(fixture.Projects.Stages.Values.Single(stage => stage.IsInitialStage).Id, child.WorkflowStageId);
        Assert.Equal(1000, child.SortKey);
        var watch = Assert.Single(fixture.Projects.Watches);
        Assert.Equal(child.Id, watch.TaskItemId);
        Assert.Equal(fixture.Actor, watch.UserId);
        Assert.Equal(WorkItemWatchAutomaticSource.Creator, watch.AutomaticSources);
        Assert.True(watch.IsWatching);
        Assert.Contains(fixture.Audit.Entries, entry => entry.EntityId == child.Id && entry.Action == "TaskCreated");
        Assert.Contains(fixture.Audit.Entries, entry => entry.EntityId == parent.Id && entry.Action == "TaskSubtasksChanged");
        Assert.Contains(fixture.Invalidations.TaskChanges, change => change.TaskId == child.Id && change.Change == "created");
        Assert.Contains(fixture.Invalidations.TaskChanges, change => change.TaskId == parent.Id && change.Change == "subtasksChanged");
        Assert.Equal(2, parent.VersionNo);
        Assert.Equal(1, fixture.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task OversizedSubtaskBriefIsRejectedBeforeChildOrParentSideEffects()
    {
        var fixture = Fixture.Create();
        var parent = fixture.AddTask("parent");

        var result = await fixture.Subresources.CreateSubtaskAsync(
            parent.Id,
            new CreateTaskSubtaskRequest(
                "child",
                null,
                TaskPriority.Medium,
                Goal: new string('x', TaskBriefText.MaximumFieldLength + 1)));

        Assert.False(result.IsSuccess);
        Assert.StartsWith("TASK_BRIEF_FIELD_TOO_LONG|", result.Error);
        Assert.Single(fixture.Projects.Tasks);
        Assert.Equal(1, parent.VersionNo);
        Assert.Empty(fixture.Audit.Entries);
        Assert.Empty(fixture.Invalidations.TaskChanges);
        Assert.Equal(0, fixture.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task LabelPatchValidatesExpectedVersionBeforeQueuingSideEffectsAndPreservesPatchSemantics()
    {
        var fixture = Fixture.Create();
        var label = new ProjectTaskLabel
        {
            ProjectId = fixture.Project.Id,
            WorkspaceId = fixture.Project.WorkspaceId,
            Name = "Label",
            Description = "existing",
            VersionNo = 1
        };
        fixture.Projects.Labels[label.Id] = label;

        foreach (var invalidVersion in new long?[] { null, 0, -1 })
        {
            var invalid = await fixture.Subresources.UpdateLabelAsync(
                fixture.Project.Id,
                label.Id,
                new UpdateProjectTaskLabelRequest(default, default, default, invalidVersion));

            Assert.False(invalid.IsSuccess);
            Assert.StartsWith("TASK_INVALID_EXPECTED_VERSION|", invalid.Error);
        }

        var stale = await fixture.Subresources.UpdateLabelAsync(
            fixture.Project.Id,
            label.Id,
            new UpdateProjectTaskLabelRequest(default, default, default, 2));

        Assert.False(stale.IsSuccess);
        Assert.StartsWith("TASK_STALE_VERSION|", stale.Error);
        Assert.Equal("existing", label.Description);
        Assert.Empty(fixture.Audit.Entries);
        Assert.Empty(fixture.Invalidations.TaskChanges);
        Assert.Empty(fixture.Invalidations.ProjectChanges);
        Assert.Equal(0, fixture.UnitOfWork.SaveCount);

        var omitted = await fixture.Subresources.UpdateLabelAsync(
            fixture.Project.Id,
            label.Id,
            new UpdateProjectTaskLabelRequest(default, default, default, 1));
        Assert.True(omitted.IsSuccess);
        Assert.Equal("existing", label.Description);

        var trimmed = await fixture.Subresources.UpdateLabelAsync(
            fixture.Project.Id,
            label.Id,
            new UpdateProjectTaskLabelRequest(default, new OptionalString(true, "  updated  "), default, 2));
        Assert.True(trimmed.IsSuccess);
        Assert.Equal("updated", label.Description);

        var clearedByEmptyString = await fixture.Subresources.UpdateLabelAsync(
            fixture.Project.Id,
            label.Id,
            new UpdateProjectTaskLabelRequest(default, new OptionalString(true, string.Empty), default, 3));
        Assert.True(clearedByEmptyString.IsSuccess);
        Assert.Null(label.Description);

        var setAgain = await fixture.Subresources.UpdateLabelAsync(
            fixture.Project.Id,
            label.Id,
            new UpdateProjectTaskLabelRequest(default, new OptionalString(true, "again"), default, 4));
        Assert.True(setAgain.IsSuccess);

        var clearedByNull = await fixture.Subresources.UpdateLabelAsync(
            fixture.Project.Id,
            label.Id,
            new UpdateProjectTaskLabelRequest(default, new OptionalString(true, null), default, 5));
        Assert.True(clearedByNull.IsSuccess);
        Assert.Null(label.Description);

        var setBeforeWhitespaceClear = await fixture.Subresources.UpdateLabelAsync(
            fixture.Project.Id,
            label.Id,
            new UpdateProjectTaskLabelRequest(default, new OptionalString(true, "again"), default, 6));
        Assert.True(setBeforeWhitespaceClear.IsSuccess);

        var clearedByWhitespace = await fixture.Subresources.UpdateLabelAsync(
            fixture.Project.Id,
            label.Id,
            new UpdateProjectTaskLabelRequest(default, new OptionalString(true, " "), default, 7));
        Assert.True(clearedByWhitespace.IsSuccess);
        Assert.Null(label.Description);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR06")]
    public async Task GanttScheduleCommandOwnsOnlyPlannedDatesAndQueuesAtomicInvalidations()
    {
        var fixture = Fixture.Create();
        var task = fixture.AddTask("schedule");
        task.DeadlineAt = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        var deadline = task.DeadlineAt;

        var updated = await fixture.Service.UpdateScheduleAsync(
            task.Id,
            new TaskScheduleUpdateRequest(
                new DateOnly(2026, 8, 1),
                new DateOnly(2026, 8, 5),
                null,
                task.VersionNo));

        Assert.True(updated.IsSuccess);
        Assert.Equal(new DateOnly(2026, 8, 1), task.PlannedStartDate);
        Assert.Equal(task.PlannedStartDate, task.StartDate);
        Assert.Equal(new DateOnly(2026, 8, 5), task.PlannedEndDate);
        Assert.Equal(task.PlannedEndDate, task.DueDate);
        Assert.Equal(deadline, task.DeadlineAt);
        Assert.Equal(2, task.VersionNo);
        Assert.Contains(fixture.Audit.Entries, entry => entry.Action == "TaskScheduleUpdated");
        Assert.Contains(fixture.Invalidations.TaskChanges, change => change.TaskId == task.Id && change.Change == "scheduleChanged");
        Assert.Contains(fixture.Invalidations.ProjectChanges, change => change.ProjectId == fixture.Project.Id && change.Change == "scheduleChanged");

        var cleared = await fixture.Service.UpdateScheduleAsync(
            task.Id,
            new TaskScheduleUpdateRequest(null, null, null, task.VersionNo));

        Assert.True(cleared.IsSuccess);
        Assert.Null(task.PlannedStartDate);
        Assert.Null(task.PlannedEndDate);
        Assert.Contains(cleared.Value!.Warnings, warning => warning.Code == "UNSCHEDULED" && !warning.Blocking);
        Assert.Equal(deadline, task.DeadlineAt);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR06")]
    public async Task ChildScheduleChangeReturnsWarningsForDependenciesOnDerivedParent()
    {
        var fixture = Fixture.Create();
        var parent = fixture.AddTask("parent");
        var child = fixture.AddTask("child", parent.Id);
        child.PlannedStartDate = new DateOnly(2026, 8, 1);
        child.PlannedEndDate = new DateOnly(2026, 8, 1);
        var successor = fixture.AddTask("successor");
        successor.PlannedStartDate = new DateOnly(2026, 8, 2);
        successor.PlannedEndDate = new DateOnly(2026, 8, 4);
        fixture.Projects.Dependencies.Add(new TaskDependency
        {
            ProjectId = fixture.Project.Id,
            PredecessorTaskItemId = parent.Id,
            SuccessorTaskItemId = successor.Id,
            DependencyType = TaskDependencyType.FinishToStart
        });

        var result = await fixture.Service.UpdateScheduleAsync(
            child.Id,
            new TaskScheduleUpdateRequest(
                new DateOnly(2026, 8, 1),
                new DateOnly(2026, 8, 3),
                null,
                child.VersionNo));

        Assert.True(result.IsSuccess);
        Assert.Contains(
            result.Value!.Warnings,
            warning => warning.Code == "DEPENDENCY_VIOLATION" &&
                       warning.TargetType == "Dependency" &&
                       !warning.Blocking);
        Assert.Equal(new DateOnly(2026, 8, 2), successor.PlannedStartDate);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR06")]
    public async Task GanttCommandsRejectDerivedParentInvalidRangeAndStaleProgress()
    {
        var fixture = Fixture.Create();
        var parent = fixture.AddTask("parent", progress: 25);
        var child = fixture.AddTask("child", parent.Id, progress: 50);

        var parentSchedule = await fixture.Service.UpdateScheduleAsync(
            parent.Id,
            new TaskScheduleUpdateRequest(new DateOnly(2026, 8, 2), new DateOnly(2026, 8, 3), null, parent.VersionNo));
        var invalidRange = await fixture.Service.UpdateScheduleAsync(
            child.Id,
            new TaskScheduleUpdateRequest(new DateOnly(2026, 8, 4), new DateOnly(2026, 8, 3), null, child.VersionNo));
        var missingProgress = await fixture.Service.UpdateProgressAsync(
            child.Id,
            new TaskProgressUpdateRequest(null, child.VersionNo));
        var staleProgress = await fixture.Service.UpdateProgressAsync(
            child.Id,
            new TaskProgressUpdateRequest(75, 99));
        var parentProgress = await fixture.Service.UpdateProgressAsync(
            parent.Id,
            new TaskProgressUpdateRequest(75, parent.VersionNo));

        Assert.StartsWith("GANTT_PARENT_DERIVED|", parentSchedule.Error);
        Assert.StartsWith("GANTT_INVALID_DATE_RANGE|", invalidRange.Error);
        Assert.StartsWith("GANTT_INVALID_PROGRESS|", missingProgress.Error);
        Assert.StartsWith("GANTT_STALE_VERSION|", staleProgress.Error);
        Assert.StartsWith("GANTT_PARENT_DERIVED|", parentProgress.Error);
        Assert.Equal(0, fixture.UnitOfWork.SaveCount);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR06")]
    public async Task CompletedAndCancelledProgressNoOpsDoNotAdvanceVersionAuditOrOutbox()
    {
        var fixture = Fixture.Create();
        var completed = fixture.AddTask("completed", progress: 100);
        completed.Status = TaskItemStatus.Completed;
        var cancelled = fixture.AddTask("cancelled", progress: 35);
        cancelled.Status = TaskItemStatus.Cancelled;

        var completedResult = await fixture.Service.UpdateProgressAsync(
            completed.Id,
            new TaskProgressUpdateRequest(100, completed.VersionNo));
        var cancelledResult = await fixture.Service.UpdateProgressAsync(
            cancelled.Id,
            new TaskProgressUpdateRequest(35, cancelled.VersionNo));

        Assert.True(completedResult.IsSuccess);
        Assert.True(cancelledResult.IsSuccess);
        Assert.Equal(1, completed.VersionNo);
        Assert.Equal(1, cancelled.VersionNo);
        Assert.Equal(0, fixture.UnitOfWork.SaveCount);
        Assert.DoesNotContain(
            fixture.Audit.Entries,
            entry => entry.Action == "TaskProgressUpdated");
        Assert.Empty(fixture.Invalidations.TaskChanges);
        Assert.Empty(fixture.Invalidations.ProjectChanges);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR06")]
    public async Task GanttMilestoneCommandsRequireDateAndAllowOnlyBinaryProgress()
    {
        var fixture = Fixture.Create();
        var milestone = fixture.AddMilestone("release");

        var missingDate = await fixture.Service.UpdateScheduleAsync(
            milestone.Id,
            new TaskScheduleUpdateRequest(null, null, null, milestone.VersionNo));
        var scheduled = await fixture.Service.UpdateScheduleAsync(
            milestone.Id,
            new TaskScheduleUpdateRequest(null, null, new DateOnly(2026, 8, 20), milestone.VersionNo));
        var invalidProgress = await fixture.Service.UpdateProgressAsync(
            milestone.Id,
            new TaskProgressUpdateRequest(50, milestone.VersionNo));
        var completed = await fixture.Service.UpdateProgressAsync(
            milestone.Id,
            new TaskProgressUpdateRequest(100, milestone.VersionNo));

        Assert.StartsWith("MILESTONE_DATE_REQUIRED|", missingDate.Error);
        Assert.True(scheduled.IsSuccess);
        Assert.StartsWith("GANTT_INVALID_PROGRESS|", invalidProgress.Error);
        Assert.True(completed.IsSuccess);
        Assert.Equal(MilestoneStatus.Completed, milestone.Status);
        Assert.Equal(3, milestone.VersionNo);
        Assert.Equal(2, fixture.UnitOfWork.SaveCount);
        Assert.Equal(2, fixture.Invalidations.ProjectChanges.Count(change => change.ProjectId == fixture.Project.Id));
    }

    [Fact]
    [Trait("Scope", "TaskV1PR06")]
    public async Task FailedGanttSaveClearsCommandTracking()
    {
        var fixture = Fixture.Create();
        var task = fixture.AddTask("rollback");
        fixture.UnitOfWork.Result = TaskCommandSaveResult.ConcurrencyConflict;

        var result = await fixture.Service.UpdateProgressAsync(
            task.Id,
            new TaskProgressUpdateRequest(30, task.VersionNo));

        Assert.StartsWith("GANTT_STALE_VERSION|", result.Error);
        Assert.Equal(1, fixture.UnitOfWork.ClearCount);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR06")]
    public async Task GanttCommandsRejectCombinedTaskAndMilestoneOverflow()
    {
        var fixture = Fixture.Create();
        var target = fixture.AddTask("target");
        for (var index = 1; index < 500; index++)
            fixture.AddTask($"task-{index}");
        fixture.AddMilestone("milestone");

        var result = await fixture.Service.UpdateScheduleAsync(
            target.Id,
            new TaskScheduleUpdateRequest(
                new DateOnly(2026, 8, 1),
                new DateOnly(2026, 8, 2),
                null,
                target.VersionNo));

        Assert.StartsWith("GANTT_ITEM_LIMIT_EXCEEDED|", result.Error);
        Assert.Null(target.PlannedStartDate);
        Assert.Null(target.PlannedEndDate);
        Assert.Equal(0, fixture.UnitOfWork.SaveCount);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR06")]
    public async Task LegacyMilestoneKindTaskRowsDoNotAffectCanonicalTaskHierarchy()
    {
        var fixture = Fixture.Create();
        var task = fixture.AddTask("canonical task");
        var legacyMilestoneRow = fixture.AddTask("legacy milestone row", task.Id);
        legacyMilestoneRow.Kind = WorkItemKind.Milestone;

        var result = await fixture.Service.UpdateScheduleAsync(
            task.Id,
            new TaskScheduleUpdateRequest(
                new DateOnly(2026, 8, 3),
                new DateOnly(2026, 8, 4),
                null,
                task.VersionNo));

        Assert.True(result.IsSuccess);
        Assert.Equal(new DateOnly(2026, 8, 3), task.PlannedStartDate);
        Assert.Equal(new DateOnly(2026, 8, 4), task.PlannedEndDate);
        Assert.Equal(1, fixture.UnitOfWork.SaveCount);
    }

    [Theory]
    [InlineData(TaskItemStatus.Completed)]
    [InlineData(TaskItemStatus.Cancelled)]
    [Trait("Scope", "TaskV1PR06")]
    public async Task TerminalParentRejectsNewSubtasksUntilExplicitlyReopened(
        TaskItemStatus status)
    {
        var fixture = Fixture.Create();
        var parent = fixture.AddTask("terminal parent", progress: status == TaskItemStatus.Completed ? 100 : 0);
        parent.Status = status;

        var result = await fixture.Subresources.CreateSubtaskAsync(
            parent.Id,
            new CreateTaskSubtaskRequest("child", null, TaskPriority.Medium));

        Assert.StartsWith("TASK_TRANSITION_GUARD_FAILED|", result.Error);
        Assert.DoesNotContain(
            fixture.Projects.Tasks.Values,
            task => task.ParentTaskItemId == parent.Id);
        Assert.Equal(0, fixture.UnitOfWork.SaveCount);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR06")]
    public async Task ReviewOverrideUsesCanonicalDoneTransitionAndReturnsDoneStage()
    {
        var fixture = Fixture.Create();
        var review = new TaskWorkflowStage
        {
            ProjectId = fixture.Project.Id,
            Name = "Review",
            InternalCategory = TaskStageCategory.Review
        };
        var done = new TaskWorkflowStage
        {
            ProjectId = fixture.Project.Id,
            Name = "Done",
            InternalCategory = TaskStageCategory.Done
        };
        fixture.Projects.Stages[review.Id] = review;
        fixture.Projects.Stages[done.Id] = done;
        var task = fixture.AddTask("reviewed");
        task.Status = TaskItemStatus.WaitingReview;
        task.WorkflowStageId = review.Id;
        task.WorkflowStage = review;
        task.ReviewStatus = TaskReviewStatus.Submitted;

        var result = await fixture.Service.OverrideCompleteAsync(
            task.Id,
            new TaskReviewRequest(task.VersionNo, "Manager accepted the risk."));

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.OverrideApplied);
        Assert.Equal(TaskItemStatus.Completed, task.Status);
        Assert.Equal(done.Id, task.WorkflowStageId);
        Assert.Same(done, task.WorkflowStage);
        Assert.Equal(100, task.ProgressPercent);
        Assert.NotNull(task.CompletedAt);
        Assert.Equal(TaskStageCategory.Done, result.Value.Task.StageCategory);
        Assert.Equal("Done", result.Value.Task.WorkflowStageName);
        Assert.Equal(1, fixture.UnitOfWork.SaveCount);
        Assert.Contains(
            fixture.Audit.Entries,
            entry => entry.EntityId == task.Id &&
                     entry.Action == "TaskReviewOverrideCompleted");
    }

    [Fact]
    [Trait("Scope", "TaskV1PR06")]
    public async Task ReviewOverrideCannotCompleteParentWithIncompleteChild()
    {
        var fixture = Fixture.Create();
        var review = new TaskWorkflowStage
        {
            ProjectId = fixture.Project.Id,
            Name = "Review",
            InternalCategory = TaskStageCategory.Review
        };
        var done = new TaskWorkflowStage
        {
            ProjectId = fixture.Project.Id,
            Name = "Done",
            InternalCategory = TaskStageCategory.Done
        };
        fixture.Projects.Stages[review.Id] = review;
        fixture.Projects.Stages[done.Id] = done;
        var parent = fixture.AddTask("parent");
        parent.Status = TaskItemStatus.WaitingReview;
        parent.WorkflowStageId = review.Id;
        parent.WorkflowStage = review;
        fixture.AddTask("incomplete child", parent.Id, progress: 50);

        var result = await fixture.Service.OverrideCompleteAsync(
            parent.Id,
            new TaskReviewRequest(parent.VersionNo, "Manager override."));

        Assert.StartsWith("TASK_TRANSITION_GUARD_FAILED|", result.Error);
        Assert.Equal(TaskItemStatus.WaitingReview, parent.Status);
        Assert.Equal(review.Id, parent.WorkflowStageId);
        Assert.Equal(0, parent.ProgressPercent);
        Assert.Equal(1, parent.VersionNo);
        Assert.Equal(0, fixture.UnitOfWork.SaveCount);
        Assert.Empty(fixture.Audit.Entries);
        Assert.Empty(fixture.Invalidations.TaskChanges);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR06")]
    public async Task ReviewOverrideCannotCompleteCancelledTaskWithoutReopen()
    {
        var fixture = Fixture.Create();
        var cancelled = new TaskWorkflowStage
        {
            ProjectId = fixture.Project.Id,
            Name = "Cancelled",
            InternalCategory = TaskStageCategory.Cancelled
        };
        var done = new TaskWorkflowStage
        {
            ProjectId = fixture.Project.Id,
            Name = "Done",
            InternalCategory = TaskStageCategory.Done
        };
        fixture.Projects.Stages[cancelled.Id] = cancelled;
        fixture.Projects.Stages[done.Id] = done;
        var task = fixture.AddTask("cancelled", progress: 35);
        task.Status = TaskItemStatus.Cancelled;
        task.WorkflowStageId = cancelled.Id;
        task.WorkflowStage = cancelled;
        task.CancelledAt = new DateTimeOffset(2026, 7, 24, 0, 0, 0, TimeSpan.Zero);
        task.CancellationReason = "Superseded";

        var result = await fixture.Service.OverrideCompleteAsync(
            task.Id,
            new TaskReviewRequest(task.VersionNo, "Manager override."));

        Assert.StartsWith("TASK_TRANSITION_GUARD_FAILED|", result.Error);
        Assert.Equal(TaskItemStatus.Cancelled, task.Status);
        Assert.Equal(cancelled.Id, task.WorkflowStageId);
        Assert.Equal(35, task.ProgressPercent);
        Assert.NotNull(task.CancelledAt);
        Assert.Equal("Superseded", task.CancellationReason);
        Assert.Equal(1, task.VersionNo);
        Assert.Equal(0, fixture.UnitOfWork.SaveCount);
        Assert.Empty(fixture.Audit.Entries);
        Assert.Empty(fixture.Invalidations.TaskChanges);
    }

    [Theory]
    [InlineData(TaskItemStatus.Completed)]
    [InlineData(TaskItemStatus.Cancelled)]
    [Trait("Scope", "TaskV1PR06")]
    public async Task TerminalParentRejectsRestoringDeletedChildUntilReopened(
        TaskItemStatus parentStatus)
    {
        var fixture = Fixture.Create();
        var parent = fixture.AddTask(
            "terminal parent",
            progress: parentStatus == TaskItemStatus.Completed ? 100 : 0);
        parent.Status = parentStatus;
        var child = fixture.AddTask("deleted child", parent.Id, progress: 20);
        child.MarkDeleted(new DateTimeOffset(2026, 7, 24, 0, 0, 0, TimeSpan.Zero));

        var result = await fixture.Service.RestoreAsync(
            child.Id,
            new TaskRestoreRequest(child.VersionNo));

        Assert.StartsWith("TASK_TRANSITION_GUARD_FAILED|", result.Error);
        Assert.NotNull(child.DeletedAt);
        Assert.Equal(1, child.VersionNo);
        Assert.Equal(0, fixture.UnitOfWork.SaveCount);
        Assert.Empty(fixture.Audit.Entries);
        Assert.Empty(fixture.Invalidations.TaskChanges);
    }

    [Theory]
    [InlineData(TaskItemStatus.Completed)]
    [InlineData(TaskItemStatus.Cancelled)]
    [Trait("Scope", "TaskV1PR06")]
    public async Task TerminalParentRejectsDeletingChildUntilReopened(
        TaskItemStatus parentStatus)
    {
        var fixture = Fixture.Create();
        var parent = fixture.AddTask(
            "terminal parent",
            progress: parentStatus == TaskItemStatus.Completed ? 100 : 0);
        parent.Status = parentStatus;
        var child = fixture.AddTask("child", parent.Id, progress: 100);
        child.Status = TaskItemStatus.Completed;
        var cancelledSibling = fixture.AddTask("cancelled sibling", parent.Id, progress: 100);
        cancelledSibling.Status = TaskItemStatus.Cancelled;

        var result = await fixture.Service.DeleteAsync(
            child.Id,
            new TaskDeleteRequest(child.VersionNo));

        Assert.StartsWith("TASK_TRANSITION_GUARD_FAILED|", result.Error);
        Assert.Null(child.DeletedAt);
        Assert.Equal(1, child.VersionNo);
        Assert.Equal(0, fixture.UnitOfWork.SaveCount);
        Assert.Empty(fixture.Audit.Entries);
        Assert.Empty(fixture.Invalidations.TaskChanges);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR06")]
    public async Task DeleteAndRestoreAdvanceTheSharedProjectGraphRevision()
    {
        var fixture = Fixture.Create();
        var task = fixture.AddTask("lifecycle");

        var deleted = await fixture.Service.DeleteAsync(
            task.Id,
            new TaskDeleteRequest(task.VersionNo));
        var restored = await fixture.Service.RestoreAsync(
            task.Id,
            new TaskRestoreRequest(task.VersionNo));

        Assert.True(deleted.IsSuccess);
        Assert.True(restored.IsSuccess);
        Assert.Equal(
            ["deleted", "restored"],
            fixture.Invalidations.ProjectChanges.Select(change => change.Change).ToArray());
        Assert.Equal(2, fixture.UnitOfWork.SaveCount);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task ReplacingPrimaryAssigneePreservesCompatibilityAndAssignmentSemanticAffectedUsers()
    {
        var fixture = Fixture.Create();
        var task = fixture.AddTask("relationship notification");
        var previousAssignee = Guid.NewGuid();
        var newAssignee = Guid.NewGuid();
        var reviewer = Guid.NewGuid();
        task.PrimaryAssigneeUserId = previousAssignee;
        task.ReviewerUserId = reviewer;

        var result = await fixture.Service.SetAssigneeAsync(
            task.Id,
            new TaskRelationshipUserRequest(newAssignee, task.VersionNo));

        Assert.True(result.IsSuccess);
        var notification = Assert.Single(fixture.Notifications.Requests);
        Assert.Equal(TaskNotificationEventKind.PrimaryAssigneeChanged, notification.EventKind);
        Assert.Equal(previousAssignee, notification.PreviousPrimaryAssigneeUserId);
        Assert.Equal(newAssignee, notification.NewPrimaryAssigneeUserId);
        Assert.Equal(fixture.Actor, notification.ActorUserId);
        Assert.Equal(2, notification.Task.VersionNo);

        var semantic = Assert.Single(fixture.Invalidations.TaskAssignmentChanges);
        Assert.Equal(task.Id, semantic.TaskId);
        Assert.Equal("assigneeChanged", semantic.Change);
        Assert.True(
            semantic.AffectedUserIds.ToHashSet().SetEquals(
                [previousAssignee, newAssignee, reviewer]));

        var compatibility = Assert.Single(fixture.Invalidations.TaskChanges);
        Assert.Equal(task.Id, compatibility.TaskId);
        Assert.Equal("assignmentChanged", compatibility.Change);
        Assert.True(
            compatibility.AffectedUserIds.ToHashSet().SetEquals(
                [fixture.Actor, previousAssignee, newAssignee, reviewer]));
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task AssigningReviewerQueuesReviewerRequestAndAssignmentSemanticEvent()
    {
        var fixture = Fixture.Create();
        var task = fixture.AddTask("reviewer notification");
        task.PrimaryAssigneeUserId = Guid.NewGuid();
        var reviewer = Guid.NewGuid();

        var result = await fixture.Service.SetReviewerAsync(
            task.Id,
            new TaskRelationshipUserRequest(reviewer, task.VersionNo));

        Assert.True(result.IsSuccess);
        var notification = Assert.Single(fixture.Notifications.Requests);
        Assert.Equal(TaskNotificationEventKind.ReviewerAssigned, notification.EventKind);
        Assert.Equal(reviewer, notification.NewReviewerUserId);
        Assert.Equal(fixture.Actor, notification.ActorUserId);

        var semantic = Assert.Single(fixture.Invalidations.TaskAssignmentChanges);
        Assert.Equal(task.Id, semantic.TaskId);
        Assert.Equal("reviewerChanged", semantic.Change);
        Assert.Contains(reviewer, semantic.AffectedUserIds);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task OnlyFalseToTrueBlockedTransitionQueuesBlockedNotification()
    {
        var fixture = Fixture.Create();
        var task = fixture.AddTask("blocked notification");

        var alreadyUnblocked = await fixture.Service.SetBlockedStateAsync(
            task.Id,
            new TaskBlockedStateRequest(false, null, task.VersionNo));
        var becameBlocked = await fixture.Service.SetBlockedStateAsync(
            task.Id,
            new TaskBlockedStateRequest(true, "External dependency", task.VersionNo));
        var alreadyBlocked = await fixture.Service.SetBlockedStateAsync(
            task.Id,
            new TaskBlockedStateRequest(true, "External dependency", task.VersionNo));
        var becameUnblocked = await fixture.Service.SetBlockedStateAsync(
            task.Id,
            new TaskBlockedStateRequest(false, null, task.VersionNo));

        Assert.True(alreadyUnblocked.IsSuccess);
        Assert.True(becameBlocked.IsSuccess);
        Assert.True(alreadyBlocked.IsSuccess);
        Assert.True(becameUnblocked.IsSuccess);
        var notification = Assert.Single(fixture.Notifications.Requests);
        Assert.Equal(TaskNotificationEventKind.BecameBlocked, notification.EventKind);
        Assert.Equal(fixture.Actor, notification.ActorUserId);
        Assert.Equal(2, fixture.UnitOfWork.SaveCount);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task ReviewSubmitAndReturnQueueTheirDistinctRecipientCategories()
    {
        var submittedFixture = Fixture.Create();
        var submittedTask = submittedFixture.AddTask("submit review");
        submittedTask.PrimaryAssigneeUserId = submittedFixture.Actor;
        submittedTask.ReviewerUserId = Guid.NewGuid();

        var submitted = await submittedFixture.Service.SubmitReviewAsync(
            submittedTask.Id,
            new TaskReviewRequest(submittedTask.VersionNo));

        Assert.True(submitted.IsSuccess);
        Assert.Equal(
            TaskNotificationEventKind.ReviewSubmitted,
            Assert.Single(submittedFixture.Notifications.Requests).EventKind);

        var returnedFixture = Fixture.Create();
        var returnedTask = returnedFixture.AddTask("return review");
        returnedTask.PrimaryAssigneeUserId = Guid.NewGuid();
        returnedTask.ReviewerUserId = returnedFixture.Actor;
        returnedTask.ReviewStatus = TaskReviewStatus.Submitted;

        var returned = await returnedFixture.Service.ReturnReviewAsync(
            returnedTask.Id,
            new TaskReviewRequest(returnedTask.VersionNo, "Needs another pass"));

        Assert.True(returned.IsSuccess);
        Assert.Equal(
            TaskNotificationEventKind.ReviewReturned,
            Assert.Single(returnedFixture.Notifications.Requests).EventKind);
    }

    public static TheoryData<DateTimeOffset?, DateTimeOffset?, TaskDeadlineChangeClassification> MajorDeadlineCases =>
        new()
        {
            { null, new DateTimeOffset(2026, 7, 27, 0, 0, 0, TimeSpan.Zero), TaskDeadlineChangeClassification.Added },
            { new DateTimeOffset(2026, 7, 27, 0, 0, 0, TimeSpan.Zero), null, TaskDeadlineChangeClassification.Removed },
            {
                new DateTimeOffset(2026, 7, 27, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 28, 0, 0, 0, TimeSpan.Zero),
                TaskDeadlineChangeClassification.ShiftAtLeast24Hours
            },
            {
                new DateTimeOffset(2026, 7, 25, 23, 30, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 26, 0, 30, 0, TimeSpan.Zero),
                TaskDeadlineChangeClassification.CrossedUrgencyBoundary
            }
        };

    [Theory]
    [MemberData(nameof(MajorDeadlineCases))]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task HardDeadlineMutationQueuesServerClassificationAndSafeAuditMetadata(
        DateTimeOffset? persistedDeadline,
        DateTimeOffset? requestedDeadline,
        TaskDeadlineChangeClassification expectedClassification)
    {
        var fixture = Fixture.Create();
        var task = fixture.AddTask("restricted task title must not enter audit metadata");
        task.DeadlineAt = persistedDeadline;

        var result = await fixture.Service.UpdateDetailsAsync(
            task.Id,
            new TaskUpdateDetailsRequest(
                task.Title,
                null,
                TaskPriority.Medium,
                null,
                null,
                0,
                task.VersionNo,
                new OptionalDateTimeOffset(true, requestedDeadline)));

        Assert.True(result.IsSuccess);
        Assert.Equal(requestedDeadline, task.DeadlineAt);
        var notification = Assert.Single(fixture.Notifications.Requests);
        Assert.Equal(TaskNotificationEventKind.MajorDeadlineChanged, notification.EventKind);
        Assert.Equal(expectedClassification, notification.DeadlineChangeClassification);

        var audit = Assert.Single(fixture.Audit.Entries);
        Assert.Equal(
            expectedClassification.ToString(),
            audit.Metadata!["deadlineChangeClassification"]);
        Assert.Equal(
            ["deadlineChangeClassification", "reasonProvided", "versionBefore"],
            audit.Metadata.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(
            audit.Metadata.Values.OfType<string>(),
            value => value.Contains(task.Title, StringComparison.Ordinal) ||
                     value.Contains("2026-07", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task PlannedEndOnlyScheduleChangeDoesNotClassifyOrMutateHardDeadline()
    {
        var fixture = Fixture.Create();
        var task = fixture.AddTask("planned dates are separate");
        var deadline = new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
        task.DeadlineAt = deadline;

        var result = await fixture.Service.UpdateScheduleAsync(
            task.Id,
            new TaskScheduleUpdateRequest(
                null,
                new DateOnly(2026, 7, 29),
                null,
                task.VersionNo));

        Assert.True(result.IsSuccess);
        Assert.Equal(deadline, task.DeadlineAt);
        Assert.Empty(fixture.Notifications.Requests);
        Assert.DoesNotContain(
            fixture.Audit.Entries,
            entry => entry.Metadata?.ContainsKey("deadlineChangeClassification") == true);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task StaleVersionQueuesNeitherRecipientRequestNorSave()
    {
        var fixture = Fixture.Create();
        var task = fixture.AddTask("stale notification");

        var result = await fixture.Service.SetAssigneeAsync(
            task.Id,
            new TaskRelationshipUserRequest(Guid.NewGuid(), task.VersionNo + 1));

        Assert.StartsWith("TASK_STALE_VERSION|", result.Error);
        Assert.Empty(fixture.Notifications.Requests);
        Assert.Empty(fixture.Invalidations.TaskAssignmentChanges);
        Assert.Empty(fixture.Audit.Entries);
        Assert.Equal(0, fixture.UnitOfWork.SaveCount);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task RepeatingSameRelationshipDoesNotQueueASecondRequestOrSave()
    {
        var fixture = Fixture.Create();
        var task = fixture.AddTask("idempotent relationship");
        var assignee = Guid.NewGuid();

        var first = await fixture.Service.SetAssigneeAsync(
            task.Id,
            new TaskRelationshipUserRequest(assignee, task.VersionNo));
        var duplicate = await fixture.Service.SetAssigneeAsync(
            task.Id,
            new TaskRelationshipUserRequest(assignee, task.VersionNo));

        Assert.True(first.IsSuccess);
        Assert.True(duplicate.IsSuccess);
        Assert.Single(fixture.Notifications.Requests);
        Assert.Single(fixture.Invalidations.TaskAssignmentChanges);
        Assert.Equal(1, fixture.UnitOfWork.SaveCount);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task RevokedWorkspaceMemberCannotBecomePrimaryAssignee()
    {
        var fixture = Fixture.Create(); var task = fixture.AddTask("revoked assignee"); var target = Guid.NewGuid();
        fixture.RelationshipTargets.Ineligible.Add(target);
        var result = await fixture.Service.SetAssigneeAsync(task.Id, new TaskRelationshipUserRequest(target, task.VersionNo));
        AssertRejectedRelationshipMutation(fixture, task, result); Assert.Null(task.PrimaryAssigneeUserId);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task RevokedWorkspaceMemberCannotBecomeReviewer()
    {
        var fixture = Fixture.Create(); var task = fixture.AddTask("revoked reviewer"); var target = Guid.NewGuid();
        fixture.RelationshipTargets.Ineligible.Add(target);
        var result = await fixture.Service.SetReviewerAsync(task.Id, new TaskRelationshipUserRequest(target, task.VersionNo));
        AssertRejectedRelationshipMutation(fixture, task, result); Assert.Null(task.ReviewerUserId);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task RevokedWorkspaceMemberCannotBecomeCollaborator()
    {
        var fixture = Fixture.Create(); var task = fixture.AddTask("revoked collaborator"); var target = Guid.NewGuid();
        fixture.RelationshipTargets.Ineligible.Add(target);
        var result = await fixture.Service.AddCollaboratorAsync(task.Id, new TaskCollaboratorRequest(target, task.VersionNo));
        AssertRejectedRelationshipMutation(fixture, task, result); Assert.Empty(fixture.Projects.Collaborators);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task SuspendedUserCannotBecomePrimaryAssignee()
    {
        var fixture = Fixture.Create(); var task = fixture.AddTask("suspended user"); var target = Guid.NewGuid();
        fixture.RelationshipTargets.Ineligible.Add(target);
        var result = await fixture.Service.SetAssigneeAsync(task.Id, new TaskRelationshipUserRequest(target, task.VersionNo));
        AssertRejectedRelationshipMutation(fixture, task, result);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task DeletedUserCannotBecomeReviewer()
    {
        var fixture = Fixture.Create(); var task = fixture.AddTask("deleted user"); var target = Guid.NewGuid();
        fixture.RelationshipTargets.Ineligible.Add(target);
        var result = await fixture.Service.SetReviewerAsync(task.Id, new TaskRelationshipUserRequest(target, task.VersionNo));
        AssertRejectedRelationshipMutation(fixture, task, result);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task RevokedAssigneeCanStillBeCleared()
    {
        var fixture = Fixture.Create(); var task = fixture.AddTask("clear assignee"); var target = Guid.NewGuid();
        task.PrimaryAssigneeUserId = target; fixture.RelationshipTargets.Ineligible.Add(target);
        var result = await fixture.Service.SetAssigneeAsync(task.Id, new TaskRelationshipUserRequest(null, task.VersionNo));
        Assert.True(result.IsSuccess, result.Error); Assert.Null(task.PrimaryAssigneeUserId);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task RevokedReviewerCanStillBeCleared()
    {
        var fixture = Fixture.Create(); var task = fixture.AddTask("clear reviewer"); var target = Guid.NewGuid();
        task.ReviewerUserId = target; fixture.RelationshipTargets.Ineligible.Add(target);
        var result = await fixture.Service.SetReviewerAsync(task.Id, new TaskRelationshipUserRequest(null, task.VersionNo));
        Assert.True(result.IsSuccess, result.Error); Assert.Null(task.ReviewerUserId);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task RevokedCollaboratorCanStillBeRemoved()
    {
        var fixture = Fixture.Create(); var task = fixture.AddTask("remove collaborator"); var target = Guid.NewGuid();
        fixture.Projects.Collaborators.Add(new WorkItemCollaborator { TaskItemId = task.Id, UserId = target }); fixture.RelationshipTargets.Ineligible.Add(target);
        var result = await fixture.Service.RemoveCollaboratorAsync(task.Id, target, task.VersionNo);
        Assert.True(result.IsSuccess, result.Error); Assert.Empty(fixture.Projects.Collaborators);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task ActiveCurrentProjectMemberCanBecomePrimaryAssignee()
    {
        var fixture = Fixture.Create(); var task = fixture.AddTask("active assignee"); var target = Guid.NewGuid();
        var result = await fixture.Service.SetAssigneeAsync(task.Id, new TaskRelationshipUserRequest(target, task.VersionNo));
        Assert.True(result.IsSuccess, result.Error); Assert.Equal(target, task.PrimaryAssigneeUserId);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task ActiveCurrentProjectMemberCanBecomeReviewer()
    {
        var fixture = Fixture.Create(); var task = fixture.AddTask("active reviewer"); var target = Guid.NewGuid();
        var result = await fixture.Service.SetReviewerAsync(task.Id, new TaskRelationshipUserRequest(target, task.VersionNo));
        Assert.True(result.IsSuccess, result.Error); Assert.Equal(target, task.ReviewerUserId);
    }

    [Fact]
    [Trait("Scope", "TaskV1PR07B")]
    public async Task ActiveCurrentProjectMemberCanBecomeCollaborator()
    {
        var fixture = Fixture.Create(); var task = fixture.AddTask("active collaborator"); var target = Guid.NewGuid();
        var result = await fixture.Service.AddCollaboratorAsync(task.Id, new TaskCollaboratorRequest(target, task.VersionNo));
        Assert.True(result.IsSuccess, result.Error); Assert.Contains(fixture.Projects.Collaborators, item => item.UserId == target);
    }

    private static void AssertRejectedRelationshipMutation(Fixture fixture, TaskItem task, Result<TaskCommandResponse> result)
    {
        Assert.False(result.IsSuccess); Assert.StartsWith("TASK_FORBIDDEN|", result.Error); Assert.Equal(1, task.VersionNo);
        Assert.Empty(fixture.Projects.Watches); Assert.Empty(fixture.Notifications.Requests); Assert.Empty(fixture.Audit.Entries);
        Assert.Empty(fixture.Invalidations.TaskChanges); Assert.Empty(fixture.Invalidations.TaskAssignmentChanges); Assert.Equal(0, fixture.UnitOfWork.SaveCount);
    }

    private sealed class Fixture
    {
        private Fixture()
        {
            Actor = Guid.NewGuid();
            Project = new Project { WorkspaceId = Guid.NewGuid(), Name = "Project", Slug = "project", OwnerUserId = Actor, CreatedByUserId = Actor };
            Projects.ProjectItems[Project.Id] = Project;
            var initialStage = new TaskWorkflowStage
            {
                ProjectId = Project.Id,
                WorkspaceId = Project.WorkspaceId,
                Name = "Backlog",
                InternalCategory = TaskStageCategory.Backlog,
                IsInitialStage = true,
                SortKey = 1000
            };
            Projects.Stages[initialStage.Id] = initialStage;
            Service = new TaskCommandService(
                Projects,
                new FakeGroups(),
                Users,
                new AllowedProjectAuthorization(),
                new AllowedTaskAuthorization(),
                new FakeCurrentUser(Actor),
                new FixedClock(),
                Audit,
                Invalidations,
                UnitOfWork,
                new UtcTimeZoneResolver(),
                taskNotifications: Notifications,
                relationshipTargets: RelationshipTargets);
            Subresources = CreateSubresources();
        }

        public Guid Actor { get; }
        public Project Project { get; }
        public FakeProjects Projects { get; } = new();
        public FakeUsers Users { get; } = new();
        public FakeAudit Audit { get; } = new();
        public FakeInvalidations Invalidations { get; } = new();
        public RecordingTaskNotifications Notifications { get; } = new();
        public RecordingRelationshipTargets RelationshipTargets { get; } = new();
        public FakeTaskUnitOfWork UnitOfWork { get; } = new();
        public TaskCommandService Service { get; }
        public TaskSubresourceService Subresources { get; }
        public static Fixture Create() => new();
        public TaskItem AddTask(string title, Guid? parentId = null, int progress = 0)
        {
            var item = new TaskItem { ProjectId = Project.Id, WorkspaceId = Project.WorkspaceId, CreatedByUserId = Actor, Title = title, ParentTaskItemId = parentId, ProgressPercent = progress, VersionNo = 1 };
            Projects.Tasks[item.Id] = item;
            return item;
        }

        public Milestone AddMilestone(string title)
        {
            var milestone = new Milestone
            {
                ProjectId = Project.Id,
                Name = title,
                VersionNo = 1
            };
            Projects.Milestones[milestone.Id] = milestone;
            return milestone;
        }

        private TaskSubresourceService CreateSubresources() => new(
            Projects,
            null!,
            new AllowedProjectAuthorization(),
            new AllowedTaskAuthorization(),
            null!,
            null!,
            null!,
            null!,
            null!,
            new FakeCurrentUser(Actor),
            new FixedClock(),
            Audit,
            Invalidations,
            UnitOfWork,
            new UtcTimeZoneResolver());
    }

    private sealed class FakeProjects : IProjectRepository
    {
        public Dictionary<Guid, Project> ProjectItems { get; } = [];
        public Dictionary<Guid, TaskItem> Tasks { get; } = [];
        public Dictionary<Guid, TaskWorkflowStage> Stages { get; } = [];
        public Dictionary<Guid, ProjectTaskLabel> Labels { get; } = [];
        public Dictionary<Guid, Milestone> Milestones { get; } = [];
        public List<TaskDependency> Dependencies { get; } = [];
        public List<WorkItemWatchState> Watches { get; } = [];
        public List<WorkItemCollaborator> Collaborators { get; } = [];
        public Task<IReadOnlyList<Project>> ListVisibleAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Project>>(ProjectItems.Values.ToArray());
        public Task<Project?> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(ProjectItems.GetValueOrDefault(projectId));
        public Task<ProjectMember?> GetMemberAsync(Guid projectId, Guid userId, CancellationToken cancellationToken = default) => Task.FromResult<ProjectMember?>(new ProjectMember { ProjectId = projectId, UserId = userId });
        public Task<IReadOnlyList<ProjectMember>> ListMembersAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ProjectMember>>([]);
        public Task<IReadOnlyList<Milestone>> ListMilestonesAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Milestone>>(Milestones.Values.Where(milestone => milestone.ProjectId == projectId).ToArray());
        public Task<Milestone?> GetMilestoneAsync(Guid milestoneId, CancellationToken cancellationToken = default) => Task.FromResult(Milestones.GetValueOrDefault(milestoneId));
        public Task<IReadOnlyList<TaskItem>> ListTasksAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskItem>>(Tasks.Values.Where(task => task.ProjectId == projectId).ToArray());
        public Task<TaskItem?> GetTaskAsync(Guid taskItemId, CancellationToken cancellationToken = default) => Task.FromResult(Tasks.GetValueOrDefault(taskItemId));
        public Task<TaskWorkflowStage?> GetWorkflowStageAsync(Guid workflowStageId, CancellationToken cancellationToken = default) => Task.FromResult(Stages.GetValueOrDefault(workflowStageId));
        public Task<IReadOnlyList<TaskWorkflowStage>> ListWorkflowStagesAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskWorkflowStage>>(Stages.Values.Where(stage => stage.ProjectId == projectId).ToArray());
        public Task<IReadOnlyList<WorkItemCollaborator>> ListCollaboratorsAsync(Guid taskItemId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<WorkItemCollaborator>>(Collaborators.Where(item => item.TaskItemId == taskItemId).ToArray());
        public Task<IReadOnlyList<TaskAssignment>> ListAssignmentsAsync(Guid taskItemId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskAssignment>>([]);
        public Task<TaskAssignment?> GetAssignmentAsync(Guid assignmentId, CancellationToken cancellationToken = default) => Task.FromResult<TaskAssignment?>(null);
        public Task<IReadOnlyList<TaskDependency>> ListDependenciesAsync(Guid taskItemId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskDependency>>(Dependencies.Where(dependency => dependency.PredecessorTaskItemId == taskItemId || dependency.SuccessorTaskItemId == taskItemId).ToArray());
        public Task<IReadOnlyList<TaskDependency>> ListProjectDependenciesAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskDependency>>(Dependencies.Where(dependency => dependency.ProjectId == projectId).ToArray());
        public Task<TaskDependency?> GetDependencyAsync(Guid dependencyId, CancellationToken cancellationToken = default) => Task.FromResult(Dependencies.SingleOrDefault(dependency => dependency.Id == dependencyId));
        public Task<bool> DependencyExistsAsync(Guid predecessorTaskId, Guid successorTaskId, CancellationToken cancellationToken = default) => Task.FromResult(Dependencies.Any(dependency => dependency.PredecessorTaskItemId == predecessorTaskId && dependency.SuccessorTaskItemId == successorTaskId));
        public Task<IReadOnlyList<Comment>> ListCommentsAsync(CommentTargetType targetType, Guid targetId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Comment>>([]);
        public Task<Comment?> GetCommentAsync(Guid commentId, CancellationToken cancellationToken = default) => Task.FromResult<Comment?>(null);
        public Task<IReadOnlyList<ProjectTaskLabel>> ListTaskLabelsAsync(Guid projectId, bool includeArchived, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ProjectTaskLabel>>(Labels.Values.Where(label => label.ProjectId == projectId && (includeArchived || !label.IsArchived)).ToArray());
        public Task<ProjectTaskLabel?> GetTaskLabelAsync(Guid labelId, CancellationToken cancellationToken = default) => Task.FromResult(Labels.GetValueOrDefault(labelId));
        public Task AddTaskLabelAsync(ProjectTaskLabel label, CancellationToken cancellationToken = default) { Labels[label.Id] = label; return Task.CompletedTask; }
        public Task AddWatchStateAsync(WorkItemWatchState watchState, CancellationToken cancellationToken = default) { Watches.Add(watchState); return Task.CompletedTask; }
        public Task<IReadOnlyList<WorkItemWatchState>> ListWatchStatesAsync(Guid taskItemId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<WorkItemWatchState>>(Watches.Where(watch => watch.TaskItemId == taskItemId).ToArray());
        public Task AddProjectAsync(Project project, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AddMemberAsync(ProjectMember member, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AddMilestoneAsync(Milestone milestone, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AddTaskAsync(TaskItem task, CancellationToken cancellationToken = default) { Tasks[task.Id] = task; return Task.CompletedTask; }
        public Task AddCollaboratorAsync(WorkItemCollaborator collaborator, CancellationToken cancellationToken = default) { Collaborators.Add(collaborator); return Task.CompletedTask; }
        public Task AddAssignmentAsync(TaskAssignment assignment, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AddDependencyAsync(TaskDependency dependency, CancellationToken cancellationToken = default) { Dependencies.Add(dependency); return Task.CompletedTask; }
        public Task AddCommentAsync(Comment comment, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void RemoveMember(ProjectMember member) { }
        public void RemoveAssignment(TaskAssignment assignment) { }
        public void RemoveDependency(TaskDependency dependency) => Dependencies.Remove(dependency);
        public void RemoveCollaborator(WorkItemCollaborator collaborator) => Collaborators.Remove(collaborator);
    }

    private sealed class FakeGroups : IGroupRepository
    {
        public Task<IReadOnlyList<Group>> ListByWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Group>>([]);
        public Task<IReadOnlyList<Group>> ListManagedByUserAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Group>>([]);
        public Task<Group?> GetByIdAsync(Guid groupId, CancellationToken cancellationToken = default) => Task.FromResult<Group?>(null);
        public Task<GroupMember?> GetMemberAsync(Guid groupId, Guid userId, CancellationToken cancellationToken = default) => Task.FromResult<GroupMember?>(null);
        public Task<IReadOnlyList<GroupMember>> ListMembersAsync(Guid groupId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GroupMember>>([]);
        public Task AddAsync(Group group, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AddMemberAsync(GroupMember member, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeUsers : IUserRepository
    {
        public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<User?>(null);
        public Task<User?> GetByNormalizedEmailAsync(string normalizedEmail, CancellationToken cancellationToken = default) => Task.FromResult<User?>(null);
        public Task AddAsync(User user, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingRelationshipTargets : ITaskRelationshipTargetPolicy
    {
        public HashSet<Guid> Ineligible { get; } = [];
        public Task<bool> IsEligibleAsync(Guid projectId, Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(userId != Guid.Empty && !Ineligible.Contains(userId));
    }

    private sealed class AllowedProjectAuthorization : IProjectAuthorizationService
    {
        public Task<bool> CanViewProject(Guid userId, Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> CanManageProject(Guid userId, Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> CanCreateProject(Guid userId, Guid workspaceId, Guid groupId, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class DeniedProjectAuthorization : IProjectAuthorizationService
    {
        public Task<bool> CanViewProject(Guid userId, Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> CanManageProject(Guid userId, Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> CanCreateProject(Guid userId, Guid workspaceId, Guid groupId, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class AllowedTaskAuthorization : ITaskAuthorizationService
    {
        public Task<bool> CanCreateTask(Guid userId, Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> CanUpdateTask(Guid userId, Guid taskItemId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> CanAssignTask(Guid userId, Guid taskItemId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> CanDeleteTask(Guid userId, Guid taskItemId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> CanReviewTask(Guid userId, Guid taskItemId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> CanOverrideTaskReview(Guid userId, Guid taskItemId, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class FakeCurrentUser(Guid id) : ICurrentUser
    {
        public Guid? UserId => id; public Guid? SessionId => null; public string? Email => null; public SystemRole? SystemRole => null; public bool IsAuthenticated => true;
    }
    private sealed class FixedClock : IClock { public DateTimeOffset UtcNow => new(2026, 7, 25, 0, 0, 0, TimeSpan.Zero); }
    private sealed class UtcTimeZoneResolver : ITaskWorkspaceTimeZoneResolver { public Task<TimeZoneInfo> ResolveAsync(Guid tenantId, Guid workspaceId, CancellationToken cancellationToken = default) => Task.FromResult(TimeZoneInfo.Utc); }
    private sealed class FakeAudit : IAuditLogger { public List<AuditLogEntry> Entries { get; } = []; public Task LogAsync(AuditLogEntry entry, CancellationToken cancellationToken = default) { Entries.Add(entry); return Task.CompletedTask; } }
    private sealed class RecordingTaskNotifications : ITaskNotificationProducer
    {
        public List<TaskNotificationRecipientRequest> Requests { get; } = [];

        public Task ProduceAsync(
            TaskNotificationRecipientRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTaskUnitOfWork : ITaskCommandUnitOfWork
    {
        public int SaveCount { get; private set; }
        public int ClearCount { get; private set; }
        public TaskCommandSaveResult Result { get; set; } = TaskCommandSaveResult.Saved;
        public TaskCommandSaveOutcome? Outcome { get; set; }
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(1);
        public Task<TaskCommandSaveOutcome> SaveTaskCommandAsync(CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return Task.FromResult(Outcome ?? new TaskCommandSaveOutcome(Result));
        }
        public void ClearTaskCommandTracking() => ClearCount++;
    }
    private sealed class FakeInvalidations : IBusinessInvalidationPublisher
    {
        public List<(Guid TaskId, string Change, IReadOnlyList<string> ChangedFields, IReadOnlyList<Guid> AffectedUserIds)> TaskChanges { get; } = [];
        public List<(Guid TaskId, string Change, IReadOnlyList<Guid> AffectedUserIds)> TaskAssignmentChanges { get; } = [];
        public List<(Guid ProjectId, string Change)> ProjectChanges { get; } = [];
        public Task TaskChangedAsync(TaskItem task, Guid actorUserId, string change, IEnumerable<string>? changedFields = null, IEnumerable<Guid>? affectedUserIds = null, CancellationToken cancellationToken = default) { TaskChanges.Add((task.Id, change, (changedFields ?? []).Distinct(StringComparer.Ordinal).ToArray(), (affectedUserIds ?? []).Distinct().ToArray())); return Task.CompletedTask; }
        public Task TaskAssignmentChangedAsync(TaskItem task, Guid actorUserId, string change, IEnumerable<Guid>? affectedUserIds = null, CancellationToken cancellationToken = default)
        {
            TaskAssignmentChanges.Add((task.Id, change, (affectedUserIds ?? []).Distinct().ToArray()));
            return Task.CompletedTask;
        }
        public Task ProjectChangedAsync(Project project, Guid actorUserId, string change, CancellationToken cancellationToken = default) { ProjectChanges.Add((project.Id, change)); return Task.CompletedTask; }
        public Task AnnouncementChangedAsync(Announcement announcement, Guid actorUserId, string change, IEnumerable<Guid> audienceUserIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FileChangedAsync(FileObject fileObject, Attachment attachment, Guid actorUserId, string change, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
