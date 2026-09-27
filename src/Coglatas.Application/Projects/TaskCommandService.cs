using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Planning;
using Coglatas.Application.Realtime;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;

namespace Coglatas.Application.Projects;

/// <summary>Authoritative, versioned Task command boundary. Controllers and legacy adapters must not mutate TaskItem fields directly.</summary>
public sealed class TaskCommandService(
    IProjectRepository projects,
    IGroupRepository groups,
    IUserRepository users,
    IProjectAuthorizationService projectAuthorization,
    ITaskAuthorizationService taskAuthorization,
    ICurrentUser currentUser,
    IClock clock,
    IAuditLogger audit,
    IBusinessInvalidationPublisher invalidations,
    ITaskCommandUnitOfWork unitOfWork,
    ITaskWorkspaceTimeZoneResolver timeZones,
    IWorkspaceRepository? workspaceRepository = null,
    ITaskNotificationProducer? taskNotifications = null,
    ITaskRelationshipTargetPolicy? relationshipTargets = null) : ITaskCommandService
{
    private const int MaximumGanttItems = 500;
    private const int MaximumGanttDependencies = 2_000;

    public async Task<Result<CanonicalTaskResponse>> GetAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var task = await projects.GetTaskAsync(taskId, cancellationToken);
        if (!TryActor(out var actor) || task is null || task.DeletedAt.HasValue || !await projectAuthorization.CanViewProject(actor, task.ProjectId, cancellationToken))
            return Fail<CanonicalTaskResponse>("TASK_NOT_FOUND", "Task not found.");
        return Result<CanonicalTaskResponse>.Success(await ToResponseAsync(task, actor, cancellationToken));
    }

    public async Task<Result<CanonicalTaskResponse>> UpdateDetailsAsync(Guid taskId, TaskUpdateDetailsRequest request, CancellationToken cancellationToken = default)
    {
        var taskResult = await AuthorizedTaskAsync(taskId, true, cancellationToken: cancellationToken);
        if (taskResult.Error is not null) return Fail<CanonicalTaskResponse>(taskResult.Error.Value.Code, taskResult.Error.Value.Message);
        var task = taskResult.Value!;
        if (request.ExpectedVersion <= 0) return Fail<CanonicalTaskResponse>("TASK_INVALID_EXPECTED_VERSION", "Expected version must be a positive integer.");
        var stale = EnsureVersion(task, request.ExpectedVersion); if (stale is not null) return Fail<CanonicalTaskResponse>(stale.Value.Code, stale.Value.Message);

        var title = request.Title?.Trim();
        if (string.IsNullOrWhiteSpace(title) || title.Length > 240) return Fail<CanonicalTaskResponse>("TASK_INVALID_TITLE", "Task title must be between 1 and 240 characters.");
        var description = request.Description?.Trim();
        if (description?.Length > 8000) return Fail<CanonicalTaskResponse>("TASK_INVALID_DESCRIPTION", "Task description must be 8000 characters or fewer.");
        if (!request.Priority.HasValue || !Enum.IsDefined(request.Priority.Value)) return Fail<CanonicalTaskResponse>("TASK_INVALID_PRIORITY", "Task priority is invalid.");
        if (!request.ProgressPercent.HasValue || request.ProgressPercent is < 0 or > 100) return Fail<CanonicalTaskResponse>("TASK_INVALID_PROGRESS", "Task progress must be between 0 and 100.");
        if (request is { PlannedStartDate: { } plannedStartDate, PlannedEndDate: { } plannedEndDate } &&
            plannedStartDate > plannedEndDate)
        {
            return Fail<CanonicalTaskResponse>("TASK_INVALID_DATE_RANGE", "Planned start date must not be after planned end date.");
        }
        var briefValidation = TaskBriefText.Validate(
            request.Goal.IsSpecified ? request.Goal.Value : null,
            request.Deliverable.IsSpecified ? request.Deliverable.Value : null,
            request.Constraints.IsSpecified ? request.Constraints.Value : null);
        if (briefValidation is not null)
            return Result<CanonicalTaskResponse>.Failure(briefValidation);

        var category = CategoryOf(task);
        var projectTasks = await projects.ListTasksAsync(task.ProjectId, cancellationToken);
        var derived = ParentTaskDerivedValuesCalculator.Calculate(task, projectTasks, CategoryOf);
        if (derived.IsDerived && (request.ProgressPercent != derived.ProgressPercent || request.PlannedStartDate != derived.PlannedStartDate || request.PlannedEndDate != derived.PlannedEndDate))
            return Fail<CanonicalTaskResponse>("TASK_PROGRESS_DERIVED", "Parent task progress and planned dates are derived from its children.");
        if (!derived.IsDerived && category == TaskStageCategory.Done && request.ProgressPercent.Value != 100)
            return Fail<CanonicalTaskResponse>("TASK_INVALID_PROGRESS", "Completed tasks must remain at 100 percent progress.");
        if (!derived.IsDerived && category == TaskStageCategory.Cancelled && request.ProgressPercent.Value != task.ProgressPercent)
            return Fail<CanonicalTaskResponse>("TASK_INVALID_PROGRESS", "Cancelled task progress cannot be changed.");
        var deadlineClassification = TaskDeadlineChangeClassification.None;
        if (request.DeadlineAt.IsSpecified)
        {
            // PostgreSQL timestamptz/Npgsql accepts DateTimeOffset writes only
            // with a zero offset. Preserve the requested instant while making
            // UTC the canonical persisted representation before classifying it.
            var normalizedDeadlineAt = request.DeadlineAt.Value?.ToUniversalTime();
            var workspaceTimeZone = await timeZones.ResolveAsync(task.TenantId, task.WorkspaceId, cancellationToken);
            deadlineClassification = TaskDeadlineChangeClassifier.Classify(
                task.DeadlineAt,
                normalizedDeadlineAt,
                workspaceTimeZone,
                clock.UtcNow);
            task.DeadlineAt = normalizedDeadlineAt;
        }

        task.Title = title;
        task.Description = string.IsNullOrWhiteSpace(description) ? null : description;
        task.Priority = request.Priority.Value;
        if (request.Goal.IsSpecified)
            task.BriefGoal = TaskBriefText.Normalize(request.Goal.Value);
        if (request.Deliverable.IsSpecified)
            task.BriefDeliverable = TaskBriefText.Normalize(request.Deliverable.Value);
        if (request.Constraints.IsSpecified)
            task.BriefConstraints = TaskBriefText.Normalize(request.Constraints.Value);
        // Parent planning fields are derived, but ordinary body fields remain editable.
        if (!derived.IsDerived)
        {
            // Keep the legacy date columns synchronized while this compatibility model remains in use.
            task.PlannedStartDate = task.StartDate = request.PlannedStartDate;
            task.PlannedEndDate = task.DueDate = request.PlannedEndDate;
            task.ProgressPercent = request.ProgressPercent.Value;
        }

        var changedFields = new List<string>
        {
            "title",
            "description",
            "priority",
            "plannedStartDate",
            "plannedEndDate",
            "progressPercent"
        };
        if (request.DeadlineAt.IsSpecified)
        {
            changedFields.Add("deadlineAt");
        }
        if (request.Goal.IsSpecified)
            changedFields.Add("briefGoal");
        if (request.Deliverable.IsSpecified)
            changedFields.Add("briefDeliverable");
        if (request.Constraints.IsSpecified)
            changedFields.Add("briefConstraints");

        var deadlineMetadata = request.DeadlineAt.IsSpecified
            ? new Dictionary<string, object?>
            {
                ["deadlineChangeClassification"] = deadlineClassification.ToString()
            }
            : null;
        var deadlineNotification = deadlineClassification == TaskDeadlineChangeClassification.None
            ? null
            : new TaskNotificationRecipientRequest(
                task,
                TaskNotificationEventKind.MajorDeadlineChanged,
                ActorUserId: Actor(),
                DeadlineChangeClassification: deadlineClassification);
        var committed = await CommitAsync(
            task,
            "TaskDetailsUpdated",
            request.DeadlineAt.IsSpecified ? "deadlineChanged" : "updated",
            cancellationToken,
            options: new TaskCommitOptions(
                Notification: deadlineNotification,
                AuditMetadata: deadlineMetadata,
                ChangedFields: changedFields));
        return committed.IsSuccess
            ? Result<CanonicalTaskResponse>.Success(committed.Value!.Task)
            : Result<CanonicalTaskResponse>.Failure(committed.Error!);
    }

    public async Task<Result<GanttEditCommandResponse>> UpdateScheduleAsync(
        Guid taskId,
        TaskScheduleUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TryActor(out var actor))
            return Fail<GanttEditCommandResponse>("GANTT_AUTHENTICATION_REQUIRED", "Authentication is required.");

        var task = await projects.GetTaskAsync(taskId, cancellationToken);
        if (task is not null)
        {
            var authorization = await AuthorizeGanttTaskVersionAsync(
                task,
                actor,
                request.ExpectedVersion,
                cancellationToken);
            if (authorization.Error is not null)
                return Fail<GanttEditCommandResponse>(authorization.Error.Value.Code, authorization.Error.Value.Message);
            if (request.MilestoneDate.HasValue)
                return Fail<GanttEditCommandResponse>("GANTT_INVALID_SCHEDULE_TARGET", "Milestone date is not applicable to a Task.");
            if (request is { PlannedStartDate: { } plannedStartDate, PlannedEndDate: { } plannedEndDate } &&
                plannedEndDate < plannedStartDate)
            {
                return Fail<GanttEditCommandResponse>("GANTT_INVALID_DATE_RANGE", "Planned end date must not precede planned start date.");
            }

            if (await GanttItemLimitExceededAsync(task.ProjectId, cancellationToken))
                return GanttItemLimitFailure();
            var projectTasks = await projects.ListTasksBoundedAsync(task.ProjectId, MaximumGanttItems, cancellationToken);
            var derived = ParentTaskDerivedValuesCalculator.Calculate(task, projectTasks, CategoryOf);
            if (derived.IsDerived)
                return Fail<GanttEditCommandResponse>("GANTT_PARENT_DERIVED", "Parent schedule is derived from direct children.");

            // StartDate/DueDate are the maintained compatibility columns used by
            // the flag-disabled read-only projection. DeadlineAt is intentionally
            // outside this command and is never rewritten.
            task.PlannedStartDate = task.StartDate = request.PlannedStartDate;
            task.PlannedEndDate = task.DueDate = request.PlannedEndDate;
            var warnings = await BuildGanttWarningsAsync(task, projectTasks, cancellationToken);
            if (warnings.DependencyLimitExceeded)
                return GanttDependencyLimitFailure();
            return await CommitGanttTaskAsync(
                task,
                authorization.Project!,
                "TaskScheduleUpdated",
                "scheduleChanged",
                ["plannedStartDate", "plannedEndDate"],
                warnings.Warnings,
                cancellationToken);
        }

        var milestone = await projects.GetMilestoneAsync(taskId, cancellationToken);
        if (milestone is null || milestone.DeletedAt.HasValue)
            return Fail<GanttEditCommandResponse>("GANTT_WORK_ITEM_NOT_FOUND", "Work item not found.");
        var milestoneAuthorization = await AuthorizeGanttMilestoneMutationAsync(milestone, actor, cancellationToken);
        if (milestoneAuthorization.Error is not null)
            return Fail<GanttEditCommandResponse>(milestoneAuthorization.Error.Value.Code, milestoneAuthorization.Error.Value.Message);
        if (request.ExpectedVersion <= 0)
            return Fail<GanttEditCommandResponse>("GANTT_INVALID_EXPECTED_VERSION", "Expected version must be a positive integer.");
        if (milestone.VersionNo != request.ExpectedVersion)
            return Fail<GanttEditCommandResponse>("GANTT_STALE_VERSION", "Work item has changed. Refetch and retry.");
        if (await GanttItemLimitExceededAsync(milestone.ProjectId, cancellationToken))
            return GanttItemLimitFailure();
        if (request.PlannedStartDate.HasValue || request.PlannedEndDate.HasValue)
            return Fail<GanttEditCommandResponse>("GANTT_INVALID_SCHEDULE_TARGET", "Planned Task dates are not applicable to a Milestone.");
        if (!request.MilestoneDate.HasValue)
            return Fail<GanttEditCommandResponse>("MILESTONE_DATE_REQUIRED", "Milestone date is required.");

        milestone.DueDate = request.MilestoneDate;
        return await CommitGanttMilestoneAsync(
            milestone,
            milestoneAuthorization.Project!,
            "MilestoneScheduleUpdated",
            "milestoneScheduleChanged",
            [],
            cancellationToken);
    }

    public async Task<Result<GanttEditCommandResponse>> UpdateProgressAsync(
        Guid taskId,
        TaskProgressUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TryActor(out var actor))
            return Fail<GanttEditCommandResponse>("GANTT_AUTHENTICATION_REQUIRED", "Authentication is required.");
        if (request.ProgressPercent is not { } progressPercent ||
            progressPercent is < 0 or > 100)
            return Fail<GanttEditCommandResponse>("GANTT_INVALID_PROGRESS", "Progress must be an integer between 0 and 100.");

        var task = await projects.GetTaskAsync(taskId, cancellationToken);
        if (task is not null)
        {
            var authorization = await AuthorizeGanttTaskVersionAsync(
                task,
                actor,
                request.ExpectedVersion,
                cancellationToken);
            if (authorization.Error is not null)
                return Fail<GanttEditCommandResponse>(authorization.Error.Value.Code, authorization.Error.Value.Message);

            if (await GanttItemLimitExceededAsync(task.ProjectId, cancellationToken))
                return GanttItemLimitFailure();
            var projectTasks = await projects.ListTasksBoundedAsync(task.ProjectId, MaximumGanttItems, cancellationToken);
            var derived = ParentTaskDerivedValuesCalculator.Calculate(task, projectTasks, CategoryOf);
            if (derived.IsDerived)
                return Fail<GanttEditCommandResponse>("GANTT_PARENT_DERIVED", "Parent progress is derived from direct children.");
            var category = CategoryOf(task);
            if (category == TaskStageCategory.Done && progressPercent != 100)
                return Fail<GanttEditCommandResponse>("GANTT_INVALID_PROGRESS", "Completed Tasks must remain at 100 percent progress.");
            if (category == TaskStageCategory.Cancelled && progressPercent != task.ProgressPercent)
                return Fail<GanttEditCommandResponse>("GANTT_INVALID_PROGRESS", "Cancelled Task progress cannot be changed.");

            var warnings = await BuildGanttWarningsAsync(task, projectTasks, cancellationToken);
            if (warnings.DependencyLimitExceeded)
                return GanttDependencyLimitFailure();
            if (task.ProgressPercent == progressPercent)
            {
                return Result<GanttEditCommandResponse>.Success(new GanttEditCommandResponse(
                    task.Id,
                    WorkItemKind.Task,
                    task.PlannedStartDate,
                    task.PlannedEndDate,
                    null,
                    task.ProgressPercent,
                    task.VersionNo,
                    warnings.Warnings));
            }

            task.ProgressPercent = progressPercent;
            return await CommitGanttTaskAsync(
                task,
                authorization.Project!,
                "TaskProgressUpdated",
                "progressChanged",
                ["progressPercent"],
                warnings.Warnings,
                cancellationToken);
        }

        var milestone = await projects.GetMilestoneAsync(taskId, cancellationToken);
        if (milestone is null || milestone.DeletedAt.HasValue)
            return Fail<GanttEditCommandResponse>("GANTT_WORK_ITEM_NOT_FOUND", "Work item not found.");
        var milestoneAuthorization = await AuthorizeGanttMilestoneMutationAsync(milestone, actor, cancellationToken);
        if (milestoneAuthorization.Error is not null)
            return Fail<GanttEditCommandResponse>(milestoneAuthorization.Error.Value.Code, milestoneAuthorization.Error.Value.Message);
        if (request.ExpectedVersion <= 0)
            return Fail<GanttEditCommandResponse>("GANTT_INVALID_EXPECTED_VERSION", "Expected version must be a positive integer.");
        if (milestone.VersionNo != request.ExpectedVersion)
            return Fail<GanttEditCommandResponse>("GANTT_STALE_VERSION", "Work item has changed. Refetch and retry.");
        if (await GanttItemLimitExceededAsync(milestone.ProjectId, cancellationToken))
            return GanttItemLimitFailure();
        if (progressPercent is not (0 or 100))
            return Fail<GanttEditCommandResponse>("GANTT_INVALID_PROGRESS", "Milestone progress must be 0 or 100.");
        if (!milestone.DueDate.HasValue)
            return Fail<GanttEditCommandResponse>("MILESTONE_DATE_REQUIRED", "Milestone date is required before progress can be updated.");
        if (milestone.Status == MilestoneStatus.Cancelled)
            return Fail<GanttEditCommandResponse>("GANTT_INVALID_PROGRESS", "Cancelled Milestone progress cannot be changed.");

        milestone.Status = progressPercent == 100
            ? MilestoneStatus.Completed
            : milestone.Status == MilestoneStatus.Completed
                ? MilestoneStatus.NotStarted
                : milestone.Status;
        return await CommitGanttMilestoneAsync(
            milestone,
            milestoneAuthorization.Project!,
            "MilestoneProgressUpdated",
            "milestoneProgressChanged",
            [],
            cancellationToken);
    }

    public async Task<Result<TaskRelationshipsResponse>> GetRelationshipsAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var task = await AuthorizedTaskAsync(taskId, false, cancellationToken: cancellationToken);
        if (task.Error is not null) return Fail<TaskRelationshipsResponse>(task.Error.Value.Code, task.Error.Value.Message);
        var value = task.Value!;
        return Result<TaskRelationshipsResponse>.Success(await RelationshipsAsync(value, cancellationToken));
    }

    public async Task<Result<TaskCommandResponse>> TransitionAsync(Guid taskId, TaskTransitionRequest request, CancellationToken cancellationToken = default)
    {
        var taskResult = await AuthorizedTaskAsync(taskId, true, cancellationToken: cancellationToken);
        if (taskResult.Error is not null) return Fail<TaskCommandResponse>(taskResult.Error.Value.Code, taskResult.Error.Value.Message);
        var task = taskResult.Value!;
        var stale = EnsureVersion(task, request.ExpectedVersion); if (stale is not null) return Fail<TaskCommandResponse>(stale.Value.Code, stale.Value.Message);
        var transition = await TaskTransitionEngine.ApplyAsync(projects, clock, task, request.WorkflowStageId, request.Reason, cancellationToken);
        if (!transition.IsSuccess)
            return Result<TaskCommandResponse>.Failure(transition.Error!);
        return await CommitAsync(task, "TaskTransitioned", transition.Value!.Reopened ? "reopened" : "stageChanged", cancellationToken);
    }

    public async Task<Result<TaskCommandResponse>> SetBlockedStateAsync(Guid taskId, TaskBlockedStateRequest request, CancellationToken cancellationToken = default)
    {
        var taskResult = await AuthorizedTaskAsync(taskId, true, cancellationToken: cancellationToken);
        if (taskResult.Error is not null) return Fail<TaskCommandResponse>(taskResult.Error.Value.Code, taskResult.Error.Value.Message);
        var task = taskResult.Value!;
        var stale = EnsureVersion(task, request.ExpectedVersion); if (stale is not null) return Fail<TaskCommandResponse>(stale.Value.Code, stale.Value.Message);
        if (request.IsBlocked && !IsBounded(request.Reason)) return Fail<TaskCommandResponse>("TASK_BLOCK_REASON_REQUIRED", "A blocked reason is required.");
        var blockedReason = request.IsBlocked ? request.Reason!.Trim() : null;
        if (task.IsBlocked == request.IsBlocked && string.Equals(task.BlockedReason, blockedReason, StringComparison.Ordinal))
            return await CurrentCommandResponseAsync(task, cancellationToken);
        var becameBlocked = !task.IsBlocked && request.IsBlocked;
        task.IsBlocked = request.IsBlocked; task.BlockedReason = blockedReason;
        return await CommitAsync(
            task,
            "TaskBlockedStateChanged",
            "blockedStateChanged",
            cancellationToken,
            reason: request.Reason,
            options: new TaskCommitOptions(
                Notification: becameBlocked
                    ? new TaskNotificationRecipientRequest(task, TaskNotificationEventKind.BecameBlocked, ActorUserId: Actor())
                    : null,
                ChangedFields: ["isBlocked"]));
    }

    public async Task<Result<TaskCommandResponse>> CancelAsync(Guid taskId, TaskReviewRequest request, CancellationToken cancellationToken = default)
    {
        var task = await projects.GetTaskAsync(taskId, cancellationToken);
        if (task is null) return Fail<TaskCommandResponse>("TASK_NOT_FOUND", "Task not found.");
        var stage = (await projects.ListWorkflowStagesAsync(task.ProjectId, cancellationToken)).FirstOrDefault(item => item.InternalCategory == TaskStageCategory.Cancelled);
        return stage is null ? Fail<TaskCommandResponse>("TASK_INVALID_STAGE", "Cancelled stage is unavailable.") : await TransitionAsync(taskId, new TaskTransitionRequest(stage.Id, request.ExpectedVersion, request.Reason), cancellationToken);
    }

    public async Task<Result<TaskCommandResponse>> ReopenAsync(Guid taskId, TaskReviewRequest request, CancellationToken cancellationToken = default)
    {
        var task = await projects.GetTaskAsync(taskId, cancellationToken);
        if (task is null) return Fail<TaskCommandResponse>("TASK_NOT_FOUND", "Task not found.");
        var stage = (await projects.ListWorkflowStagesAsync(task.ProjectId, cancellationToken)).FirstOrDefault(item => item.InternalCategory == TaskStageCategory.Todo)
            ?? (await projects.ListWorkflowStagesAsync(task.ProjectId, cancellationToken)).FirstOrDefault(item => item.InternalCategory == TaskStageCategory.Backlog);
        return stage is null ? Fail<TaskCommandResponse>("TASK_INVALID_STAGE", "A reopen stage is unavailable.") : await TransitionAsync(taskId, new TaskTransitionRequest(stage.Id, request.ExpectedVersion), cancellationToken);
    }

    public async Task<Result<TaskCommandResponse>> SetAssigneeAsync(Guid taskId, TaskRelationshipUserRequest request, CancellationToken cancellationToken = default)
    {
        var result = await AuthorizedTaskAsync(taskId, true, true, cancellationToken: cancellationToken);
        if (result.Error is not null) return Fail<TaskCommandResponse>(result.Error.Value.Code, result.Error.Value.Message);
        var task = result.Value!;
        var stale = EnsureVersion(task, request.ExpectedVersion); if (stale is not null) return Fail<TaskCommandResponse>(stale.Value.Code, stale.Value.Message);
        if (!request.UserId.HasValue && CategoryOf(task) is TaskStageCategory.InProgress or TaskStageCategory.Review) return Fail<TaskCommandResponse>("TASK_ASSIGNEE_REQUIRED", "Active work cannot be unassigned.");
        if (request.UserId.HasValue && !await RelationshipTargets.IsEligibleAsync(task.ProjectId, request.UserId.Value, cancellationToken)) return Fail<TaskCommandResponse>("TASK_FORBIDDEN", "The assignee is not available for this Task.");
        if (request.UserId.HasValue && request.UserId == task.ReviewerUserId)
            return Fail<TaskCommandResponse>("TASK_REVIEWER_MUST_DIFFER", "Reviewer and primary assignee must differ.");
        var previousAssigneeUserId = task.PrimaryAssigneeUserId;
        if (previousAssigneeUserId == request.UserId)
            return await CurrentCommandResponseAsync(task, cancellationToken);
        task.PrimaryAssigneeUserId = request.UserId;
        var affectedUsers = new[] { previousAssigneeUserId, request.UserId, task.ReviewerUserId }
            .Where(userId => userId.HasValue)
            .Select(userId => userId!.Value)
            .Distinct()
            .ToArray();
        return await CommitAsync(
            task,
            "TaskAssigneeChanged",
            "assignmentChanged",
            cancellationToken,
            options: new TaskCommitOptions(
                Notification: new TaskNotificationRecipientRequest(
                    task,
                    TaskNotificationEventKind.PrimaryAssigneeChanged,
                    ActorUserId: Actor(),
                    PreviousPrimaryAssigneeUserId: previousAssigneeUserId,
                    NewPrimaryAssigneeUserId: request.UserId),
                AssignmentChange: "assigneeChanged",
                AssignmentAffectedUserIds: affectedUsers,
                ChangedFields: ["primaryAssigneeUserId"]));
    }

    public async Task<Result<TaskCommandResponse>> SetTargetGroupAsync(Guid taskId, TaskTargetGroupRequest request, CancellationToken cancellationToken = default)
    {
        var result = await AuthorizedTaskAsync(taskId, true, true, cancellationToken: cancellationToken);
        if (result.Error is not null) return Fail<TaskCommandResponse>(result.Error.Value.Code, result.Error.Value.Message);
        var task = result.Value!;
        var stale = EnsureVersion(task, request.ExpectedVersion); if (stale is not null) return Fail<TaskCommandResponse>(stale.Value.Code, stale.Value.Message);
        if (request.GroupId.HasValue)
        {
            var group = await groups.GetByIdAsync(request.GroupId.Value, cancellationToken);
            if (group is null || group.WorkspaceId != task.WorkspaceId || group.Status != GroupStatus.Active) return Fail<TaskCommandResponse>("TASK_TARGET_GROUP_REQUIRED", "Target group is not valid for the task workspace.");
        }
        if (!request.GroupId.HasValue && !task.PrimaryAssigneeUserId.HasValue) return Fail<TaskCommandResponse>("TASK_TARGET_GROUP_REQUIRED", "An unassigned task requires a target group.");
        if (task.TargetGroupId == request.GroupId)
            return await CurrentCommandResponseAsync(task, cancellationToken);
        task.TargetGroupId = request.GroupId;
        return await CommitAsync(
            task,
            "TaskTargetGroupChanged",
            "assignmentChanged",
            cancellationToken,
            options: new TaskCommitOptions(
                AssignmentChange: "groupChanged",
                AssignmentAffectedUserIds: RelatedUsers(task).ToArray(),
                ChangedFields: ["targetGroupId"]));
    }

    public async Task<Result<TaskCommandResponse>> AddCollaboratorAsync(Guid taskId, TaskCollaboratorRequest request, CancellationToken cancellationToken = default)
    {
        var result = await AuthorizedTaskAsync(taskId, true, true, cancellationToken: cancellationToken);
        if (result.Error is not null) return Fail<TaskCommandResponse>(result.Error.Value.Code, result.Error.Value.Message);
        var task = result.Value!;
        var stale = EnsureVersion(task, request.ExpectedVersion); if (stale is not null) return Fail<TaskCommandResponse>(stale.Value.Code, stale.Value.Message);
        if (!await RelationshipTargets.IsEligibleAsync(task.ProjectId, request.UserId, cancellationToken)) return Fail<TaskCommandResponse>("TASK_FORBIDDEN", "The collaborator is not available for this Task.");
        var collaborators = await projects.ListCollaboratorsAsync(task.Id, cancellationToken);
        if (collaborators.Any(item => item.UserId == request.UserId))
            return await CurrentCommandResponseAsync(task, cancellationToken);
        await projects.AddCollaboratorAsync(new WorkItemCollaborator { TaskItemId = task.Id, UserId = request.UserId, AddedByUserId = Actor(), AddedAt = clock.UtcNow }, cancellationToken);
        var effectiveCollaborators = collaborators.Select(item => item.UserId).Append(request.UserId).Distinct().ToArray();
        return await CommitAsync(task, "TaskCollaboratorAdded", "assignmentChanged", cancellationToken,
            effectiveCollaboratorUserIds: effectiveCollaborators,
            options: new TaskCommitOptions(
                AssignmentChange: "collaboratorChanged",
                AssignmentAffectedUserIds: effectiveCollaborators,
                ChangedFields: ["collaborators"]));
    }

    public async Task<Result<TaskCommandResponse>> RemoveCollaboratorAsync(Guid taskId, Guid collaboratorUserId, long expectedVersion, CancellationToken cancellationToken = default)
    {
        var result = await AuthorizedTaskAsync(taskId, true, true, cancellationToken: cancellationToken);
        if (result.Error is not null) return Fail<TaskCommandResponse>(result.Error.Value.Code, result.Error.Value.Message);
        var task = result.Value!;
        var stale = EnsureVersion(task, expectedVersion); if (stale is not null) return Fail<TaskCommandResponse>(stale.Value.Code, stale.Value.Message);
        var collaborators = await projects.ListCollaboratorsAsync(task.Id, cancellationToken);
        var collaborator = collaborators.FirstOrDefault(item => item.UserId == collaboratorUserId);
        if (collaborator is null)
            return await CurrentCommandResponseAsync(task, cancellationToken);
        projects.RemoveCollaborator(collaborator);
        var effectiveCollaborators = collaborators.Where(item => item.UserId != collaboratorUserId).Select(item => item.UserId).Distinct().ToArray();
        return await CommitAsync(task, "TaskCollaboratorRemoved", "assignmentChanged", cancellationToken,
            effectiveCollaboratorUserIds: effectiveCollaborators,
            options: new TaskCommitOptions(
                AssignmentChange: "collaboratorChanged",
                AssignmentAffectedUserIds: [.. effectiveCollaborators, collaboratorUserId],
                ChangedFields: ["collaborators"]));
    }

    public async Task<Result<TaskCommandResponse>> SetReviewerAsync(Guid taskId, TaskRelationshipUserRequest request, CancellationToken cancellationToken = default)
    {
        var result = await AuthorizedTaskAsync(taskId, true, true, cancellationToken: cancellationToken);
        if (result.Error is not null) return Fail<TaskCommandResponse>(result.Error.Value.Code, result.Error.Value.Message);
        var task = result.Value!;
        var stale = EnsureVersion(task, request.ExpectedVersion); if (stale is not null) return Fail<TaskCommandResponse>(stale.Value.Code, stale.Value.Message);
        // Clearing a reviewer is valid when the task has no assignee.  Only a
        // concrete requested reviewer may violate the distinct-user invariant.
        if (request.UserId.HasValue && request.UserId == task.PrimaryAssigneeUserId) return Fail<TaskCommandResponse>("TASK_REVIEWER_MUST_DIFFER", "Reviewer and primary assignee must differ.");
        if (request.UserId.HasValue && !await RelationshipTargets.IsEligibleAsync(task.ProjectId, request.UserId.Value, cancellationToken)) return Fail<TaskCommandResponse>("TASK_FORBIDDEN", "The reviewer is not available for this Task.");
        var previousReviewerUserId = task.ReviewerUserId;
        if (previousReviewerUserId == request.UserId)
            return await CurrentCommandResponseAsync(task, cancellationToken);
        task.ReviewerUserId = request.UserId;
        if (!request.UserId.HasValue) task.ReviewStatus = TaskReviewStatus.None;
        var reviewerAffectedUsers = new[] { previousReviewerUserId, request.UserId, task.PrimaryAssigneeUserId }
            .Where(userId => userId.HasValue)
            .Select(userId => userId!.Value)
            .Distinct()
            .ToArray();
        return await CommitAsync(
            task,
            "TaskReviewerChanged",
            "assignmentChanged",
            cancellationToken,
            options: new TaskCommitOptions(
                Notification: request.UserId.HasValue
                    ? new TaskNotificationRecipientRequest(
                        task,
                        TaskNotificationEventKind.ReviewerAssigned,
                        ActorUserId: Actor(),
                        NewReviewerUserId: request.UserId)
                    : null,
                AssignmentChange: "reviewerChanged",
                AssignmentAffectedUserIds: reviewerAffectedUsers,
                ChangedFields: ["reviewerUserId"]));
    }

    public async Task<Result<TaskCommandResponse>> SubmitReviewAsync(Guid taskId, TaskReviewRequest request, CancellationToken cancellationToken = default)
    {
        var result = await AuthorizedTaskAsync(taskId, true, cancellationToken: cancellationToken);
        if (result.Error is not null) return Fail<TaskCommandResponse>(result.Error.Value.Code, result.Error.Value.Message);
        var task = result.Value!; var stale = EnsureVersion(task, request.ExpectedVersion); if (stale is not null) return Fail<TaskCommandResponse>(stale.Value.Code, stale.Value.Message);
        if (!task.PrimaryAssigneeUserId.HasValue || task.PrimaryAssigneeUserId != Actor()) return Fail<TaskCommandResponse>("TASK_FORBIDDEN", "Only the primary assignee may submit review.");
        if (!task.ReviewerUserId.HasValue) return Fail<TaskCommandResponse>("TASK_TRANSITION_GUARD_FAILED", "A reviewer is required to submit review.");
        task.ReviewStatus = TaskReviewStatus.Submitted; task.ReviewSubmittedAt = clock.UtcNow; task.ReviewReturnReason = null;
        return await CommitAsync(
            task,
            "TaskReviewSubmitted",
            "reviewSubmitted",
            cancellationToken,
            options: new TaskCommitOptions(
                Notification: new TaskNotificationRecipientRequest(task, TaskNotificationEventKind.ReviewSubmitted, ActorUserId: Actor()),
                ChangedFields: ["reviewStatus"]));
    }

    public async Task<Result<TaskCommandResponse>> AcceptReviewAsync(Guid taskId, TaskReviewRequest request, CancellationToken cancellationToken = default) => await ResolveReviewAsync(taskId, request, TaskReviewStatus.Accepted, cancellationToken);

    public async Task<Result<TaskCommandResponse>> ReturnReviewAsync(Guid taskId, TaskReviewRequest request, CancellationToken cancellationToken = default) => await ResolveReviewAsync(taskId, request, TaskReviewStatus.Returned, cancellationToken);

    public async Task<Result<TaskCommandResponse>> OverrideCompleteAsync(Guid taskId, TaskReviewRequest request, CancellationToken cancellationToken = default)
    {
        var result = await AuthorizedTaskAsync(taskId, true, false, true, cancellationToken: cancellationToken);
        if (result.Error is not null) return Fail<TaskCommandResponse>(result.Error.Value.Code, result.Error.Value.Message);
        var task = result.Value!; var stale = EnsureVersion(task, request.ExpectedVersion); if (stale is not null) return Fail<TaskCommandResponse>(stale.Value.Code, stale.Value.Message);
        if (!IsBounded(request.Reason)) return Fail<TaskCommandResponse>("TASK_TRANSITION_GUARD_FAILED", "An override reason is required.");
        var done = (await projects.ListWorkflowStagesAsync(task.ProjectId, cancellationToken)).FirstOrDefault(stage => stage.InternalCategory == TaskStageCategory.Done);
        if (done is null) return Fail<TaskCommandResponse>("TASK_INVALID_STAGE", "Done stage is unavailable.");
        var transition = await TaskTransitionEngine.ApplyAsync(
            projects,
            clock,
            task,
            done.Id,
            request.Reason,
            cancellationToken,
            allowReviewOverride: true);
        if (!transition.IsSuccess)
            return Result<TaskCommandResponse>.Failure(transition.Error!);
        return await CommitAsync(task, "TaskReviewOverrideCompleted", "reviewOverridden", cancellationToken, true, request.Reason);
    }

    public async Task<Result<TaskCommandResponse>> ClaimAsync(Guid taskId, TaskClaimRequest request, CancellationToken cancellationToken = default)
    {
        var result = await AuthorizedTaskAsync(taskId, false, cancellationToken: cancellationToken);
        if (result.Error is not null) return Fail<TaskCommandResponse>(result.Error.Value.Code, result.Error.Value.Message);
        var task = result.Value!; var stale = EnsureVersion(task, request.ExpectedVersion); if (stale is not null) return Fail<TaskCommandResponse>(stale.Value.Code, stale.Value.Message);
        if (task.PrimaryAssigneeUserId.HasValue) return Fail<TaskCommandResponse>("TASK_ALREADY_ASSIGNED", "Task already has a primary assignee.");
        if (!task.TargetGroupId.HasValue || CategoryOf(task) is not (TaskStageCategory.Backlog or TaskStageCategory.Todo) || task.DeletedAt.HasValue) return Fail<TaskCommandResponse>("TASK_CLAIM_NOT_ELIGIBLE", "Task is not eligible for claim.");
        if (await groups.GetMemberAsync(task.TargetGroupId.Value, Actor(), cancellationToken) is null || !await IsProjectMemberAsync(task.ProjectId, Actor(), cancellationToken)) return Fail<TaskCommandResponse>("TASK_CLAIM_GROUP_MEMBERSHIP_REQUIRED", "Current group and project membership are required.");
        task.PrimaryAssigneeUserId = Actor();
        return await CommitAsync(
            task,
            "TaskClaimed",
            "claimed",
            cancellationToken,
            options: new TaskCommitOptions(
                Notification: new TaskNotificationRecipientRequest(
                    task,
                    TaskNotificationEventKind.PrimaryAssigneeChanged,
                    ActorUserId: Actor(),
                    NewPrimaryAssigneeUserId: Actor()),
                AssignmentChange: "assigneeChanged",
                AssignmentAffectedUserIds: RelatedUsers(task).ToArray(),
                ChangedFields: ["primaryAssigneeUserId"]));
    }

    public async Task<Result<TaskCommandResponse>> RestoreAsync(Guid taskId, TaskRestoreRequest request, CancellationToken cancellationToken = default)
    {
        var result = await AuthorizedTaskAsync(taskId, true, true, cancellationToken: cancellationToken, includeDeleted: true);
        if (result.Error is not null) return Fail<TaskCommandResponse>(result.Error.Value.Code, result.Error.Value.Message);
        var task = result.Value!; var stale = EnsureVersion(task, request.ExpectedVersion); if (stale is not null) return Fail<TaskCommandResponse>(stale.Value.Code, stale.Value.Message);
        var parentGuard = await RequireReopenedParentForHierarchyMutationAsync(task, "restore", cancellationToken);
        if (parentGuard is not null)
            return Fail<TaskCommandResponse>(parentGuard.Value.Code, parentGuard.Value.Message);
        task.Restore();
        return await CommitAsync(
            task,
            "TaskRestored",
            "restored",
            cancellationToken,
            projectChanged: true);
    }

    public async Task<Result<TaskCommandResponse>> DeleteAsync(Guid taskId, TaskDeleteRequest request, CancellationToken cancellationToken = default)
    {
        var result = await AuthorizedTaskAsync(taskId, true, true, cancellationToken: cancellationToken);
        if (result.Error is not null) return Fail<TaskCommandResponse>(result.Error.Value.Code, result.Error.Value.Message);
        var task = result.Value!;
        var stale = EnsureVersion(task, request.ExpectedVersion); if (stale is not null) return Fail<TaskCommandResponse>(stale.Value.Code, stale.Value.Message);
        var parentGuard = await RequireReopenedParentForHierarchyMutationAsync(task, "delete", cancellationToken);
        if (parentGuard is not null)
            return Fail<TaskCommandResponse>(parentGuard.Value.Code, parentGuard.Value.Message);
        task.MarkDeleted(clock.UtcNow);
        return await CommitAsync(
            task,
            "TaskDeleted",
            "deleted",
            cancellationToken,
            projectChanged: true);
    }

    public async Task<Result<TaskWatchStateResponse>> GetWatchStateAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var result = await AuthorizedTaskAsync(taskId, false, cancellationToken: cancellationToken);
        if (result.Error is not null) return Fail<TaskWatchStateResponse>(result.Error.Value.Code, result.Error.Value.Message);
        var state = await projects.GetWatchStateAsync(taskId, Actor(), cancellationToken);
        return Result<TaskWatchStateResponse>.Success(ToWatchResponse(state));
    }

    public Task<Result<TaskWatchStateResponse>> WatchAsync(Guid taskId, TaskWatchRequest request, CancellationToken cancellationToken = default) => SetWatchAsync(taskId, request, true, cancellationToken);
    public Task<Result<TaskWatchStateResponse>> UnwatchAsync(Guid taskId, TaskWatchRequest request, CancellationToken cancellationToken = default) => SetWatchAsync(taskId, request, false, cancellationToken);

    private async Task<Result<TaskWatchStateResponse>> SetWatchAsync(Guid taskId, TaskWatchRequest request, bool watch, CancellationToken cancellationToken)
    {
        var result = await AuthorizedTaskAsync(taskId, false, cancellationToken: cancellationToken);
        if (result.Error is not null) return Fail<TaskWatchStateResponse>(result.Error.Value.Code, result.Error.Value.Message);
        var task = result.Value!;
        var state = await projects.GetWatchStateAsync(taskId, Actor(), cancellationToken);
        var created = false;
        if (state is null)
        {
            if (request.ExpectedVersion != 0) return Fail<TaskWatchStateResponse>("TASK_STALE_VERSION", "Watch state has changed. Refetch and retry.");
            state = new WorkItemWatchState
            {
                TaskItemId = taskId,
                UserId = Actor(),
                AutomaticSources = await AutomaticSourcesForAsync(task, Actor(), null, cancellationToken),
                UpdatedAt = clock.UtcNow,
                VersionNo = 1
            };
            await projects.AddWatchStateAsync(state, cancellationToken);
            created = true;
        }
        else if (state.VersionNo != request.ExpectedVersion) return Fail<TaskWatchStateResponse>("TASK_STALE_VERSION", "Watch state has changed. Refetch and retry.");
        var previousManualWatch = state.IsManualWatch;
        var previousOptOut = state.IsExplicitOptOut;
        var previousWatching = state.IsWatching;
        state.IsManualWatch = watch;
        state.IsExplicitOptOut = !watch;
        TaskWatchStateRules.Normalize(state);
        var changed = created || previousManualWatch != state.IsManualWatch || previousOptOut != state.IsExplicitOptOut || previousWatching != state.IsWatching;
        if (!changed)
            return Result<TaskWatchStateResponse>.Success(ToWatchResponse(state));

        state.UpdatedAt = clock.UtcNow;
        if (!created) state.VersionNo++;
        // A watch preference changes the canonical Task projection. Advance the
        // aggregate token before queuing its durable invalidation.
        task.VersionNo++;
        await audit.LogAsync(new AuditLogEntry(Actor(), watch ? "TaskWatchEnabled" : "TaskWatchOptOut", "TaskItem", taskId, "Task watch preference changed.", WorkspaceId: task.WorkspaceId, ProjectId: task.ProjectId), cancellationToken);
        await invalidations.TaskChangedAsync(task, Actor(), "watchChanged", affectedUserIds: [Actor()], cancellationToken: cancellationToken);
        var save = await unitOfWork.SaveTaskCommandAsync(cancellationToken);
        if (!save.IsSaved)
        {
            try
            {
                if (save.Result == TaskCommandSaveResult.UniqueConflict &&
                    string.Equals(save.ConstraintName, TaskCommandConstraintNames.WorkItemWatchStateIdentity, StringComparison.Ordinal))
                {
                    // SaveTaskCommandAsync cleared the attempted mutation.  Recheck
                    // both visibility and the canonical row before classifying the
                    // watch-identity race, then clear these recovery reads below.
                    var current = await AuthorizedTaskAsync(taskId, false, cancellationToken: cancellationToken);
                    if (current.Error is null && await projects.GetWatchStateAsync(taskId, Actor(), cancellationToken) is not null)
                        return Fail<TaskWatchStateResponse>("TASK_STALE_VERSION", "Watch state has changed. Refetch and retry.");
                }
                return Fail<TaskWatchStateResponse>(save.Result == TaskCommandSaveResult.UniqueConflict ? "TASK_CONFLICT" : "TASK_STALE_VERSION", "Task has changed. Refetch and retry.");
            }
            finally
            {
                unitOfWork.ClearTaskCommandTracking();
            }
        }
        return Result<TaskWatchStateResponse>.Success(ToWatchResponse(state));
    }

    private async Task<Result<TaskCommandResponse>> ResolveReviewAsync(Guid taskId, TaskReviewRequest request, TaskReviewStatus outcome, CancellationToken cancellationToken)
    {
        var result = await AuthorizedTaskAsync(taskId, true, cancellationToken: cancellationToken, requireReview: true);
        if (result.Error is not null) return Fail<TaskCommandResponse>(result.Error.Value.Code, result.Error.Value.Message);
        var task = result.Value!; var stale = EnsureVersion(task, request.ExpectedVersion); if (stale is not null) return Fail<TaskCommandResponse>(stale.Value.Code, stale.Value.Message);
        if (task.ReviewStatus != TaskReviewStatus.Submitted) return Fail<TaskCommandResponse>("TASK_TRANSITION_GUARD_FAILED", "Task has not been submitted for review.");
        if (outcome == TaskReviewStatus.Returned && !IsBounded(request.Reason)) return Fail<TaskCommandResponse>("TASK_TRANSITION_GUARD_FAILED", "A review return reason is required.");
        task.ReviewStatus = outcome; task.ReviewResolvedAt = clock.UtcNow; task.ReviewResolvedByUserId = Actor(); task.ReviewReturnReason = outcome == TaskReviewStatus.Returned ? request.Reason!.Trim() : null;
        if (outcome == TaskReviewStatus.Returned && request.ReturnWorkflowStageId.HasValue)
        {
            var stage = await projects.GetWorkflowStageAsync(request.ReturnWorkflowStageId.Value, cancellationToken);
            if (stage is null || stage.ProjectId != task.ProjectId || stage.InternalCategory is TaskStageCategory.Done or TaskStageCategory.Cancelled)
                return Fail<TaskCommandResponse>("TASK_INVALID_STAGE", "Review return target is not available.");
            task.WorkflowStageId = stage.Id;
            task.Status = LegacyStatus(stage.InternalCategory);
        }
        return await CommitAsync(
            task,
            outcome == TaskReviewStatus.Accepted ? "TaskReviewAccepted" : "TaskReviewReturned",
            "reviewResolved",
            cancellationToken,
            reason: request.Reason,
            options: new TaskCommitOptions(
                Notification: outcome == TaskReviewStatus.Returned
                    ? new TaskNotificationRecipientRequest(task, TaskNotificationEventKind.ReviewReturned, ActorUserId: Actor())
                    : null,
                ChangedFields: ["reviewStatus"]));
    }

    private async Task<(Project? Project, (string Code, string Message)? Error)> AuthorizeGanttTaskVersionAsync(
        TaskItem task,
        Guid actor,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        if (task.DeletedAt.HasValue || task.Kind != WorkItemKind.Task)
        {
            return (null, ("GANTT_WORK_ITEM_NOT_FOUND", "Work item not found."));
        }

        var authorization = await AuthorizeGanttTaskMutationAsync(task, actor, cancellationToken);
        if (authorization.Error is not null)
        {
            return authorization;
        }

        if (expectedVersion <= 0)
        {
            return (null, ("GANTT_INVALID_EXPECTED_VERSION", "Expected version must be a positive integer."));
        }

        if (task.VersionNo != expectedVersion)
        {
            return (null, ("GANTT_STALE_VERSION", "Work item has changed. Refetch and retry."));
        }

        return authorization;
    }

    private async Task<(Project? Project, (string Code, string Message)? Error)> AuthorizeGanttTaskMutationAsync(
        TaskItem task,
        Guid actor,
        CancellationToken cancellationToken)
    {
        var project = await projects.GetProjectAsync(task.ProjectId, cancellationToken);
        if (project is null ||
            project.DeletedAt.HasValue ||
            project.Status is ProjectStatus.Archived or ProjectStatus.Deleted ||
            !await projectAuthorization.CanViewProject(actor, project.Id, cancellationToken))
        {
            return (null, ("GANTT_WORK_ITEM_NOT_FOUND", "Work item not found."));
        }

        var workspaceMember = workspaceRepository is null
            ? null
            : await workspaceRepository.GetMemberAsync(project.WorkspaceId, actor, cancellationToken);
        if (workspaceMember is { Status: MembershipStatus.Active, Role: WorkspaceRole.ReadOnly })
            return (null, ("GANTT_FORBIDDEN", "Schedule editing is not authorized."));

        if (await projectAuthorization.CanManageProject(actor, project.Id, cancellationToken))
            return (project, null);

        var projectMember = await projects.GetMemberAsync(project.Id, actor, cancellationToken);
        var mayEditOwnTask =
            workspaceMember is { Status: MembershipStatus.Active } &&
            workspaceMember.Role.CanContribute() &&
            projectMember?.Role == ProjectRole.Contributor &&
            (task.CreatedByUserId == actor || task.PrimaryAssigneeUserId == actor);
        return mayEditOwnTask
            ? (project, null)
            : (null, ("GANTT_FORBIDDEN", "Schedule editing is not authorized."));
    }

    private async Task<(string Code, string Message)?> RequireReopenedParentForHierarchyMutationAsync(
        TaskItem child,
        string operation,
        CancellationToken cancellationToken)
    {
        if (!child.ParentTaskItemId.HasValue)
            return null;

        var parent = await projects.GetTaskAsync(child.ParentTaskItemId.Value, cancellationToken);
        if (parent is null ||
            parent.DeletedAt.HasValue ||
            CategoryOf(parent) is not (TaskStageCategory.Done or TaskStageCategory.Cancelled))
        {
            return null;
        }

        return (
            "TASK_TRANSITION_GUARD_FAILED",
            $"Reopen the parent Task before attempting to {operation} its child.");
    }

    private async Task<(Project? Project, (string Code, string Message)? Error)> AuthorizeGanttMilestoneMutationAsync(
        Milestone milestone,
        Guid actor,
        CancellationToken cancellationToken)
    {
        var project = await projects.GetProjectAsync(milestone.ProjectId, cancellationToken);
        if (project is null ||
            project.DeletedAt.HasValue ||
            project.Status is ProjectStatus.Archived or ProjectStatus.Deleted ||
            !await projectAuthorization.CanViewProject(actor, project.Id, cancellationToken))
        {
            return (null, ("GANTT_WORK_ITEM_NOT_FOUND", "Work item not found."));
        }

        var workspaceMember = workspaceRepository is null
            ? null
            : await workspaceRepository.GetMemberAsync(project.WorkspaceId, actor, cancellationToken);
        if (workspaceMember is { Status: MembershipStatus.Active, Role: WorkspaceRole.ReadOnly })
            return (null, ("GANTT_FORBIDDEN", "Milestone editing is not authorized."));

        return await projectAuthorization.CanManageProject(actor, project.Id, cancellationToken)
            ? (project, null)
            : (null, ("GANTT_FORBIDDEN", "Milestone editing is not authorized."));
    }

    private async Task<GanttWarningBuildResult> BuildGanttWarningsAsync(
        TaskItem changedTask,
        IReadOnlyList<TaskItem> projectTasks,
        CancellationToken cancellationToken)
    {
        var effectiveTasks = projectTasks
            .Where(task => task.Id != changedTask.Id && !task.DeletedAt.HasValue && task.Kind == WorkItemKind.Task)
            .Append(changedTask)
            .ToList();
        var dependencies = await projects.ListProjectDependenciesBoundedAsync(
            changedTask.ProjectId,
            MaximumGanttDependencies + 1,
            cancellationToken);
        if (dependencies.Count > MaximumGanttDependencies)
            return new GanttWarningBuildResult([], true);

        var warnings = new List<GanttWarningResponse>();
        var category = CategoryOf(changedTask);
        if (!changedTask.PlannedStartDate.HasValue && !changedTask.PlannedEndDate.HasValue)
        {
            warnings.Add(GanttWarning(
                "UNSCHEDULED",
                "This Task has no planned dates and is listed as unscheduled.",
                GanttWarningSeverity.Info,
                "Task",
                changedTask.Id,
                "plannedStartDate"));
        }
        if (!changedTask.PlannedEndDate.HasValue &&
            category is TaskStageCategory.InProgress or TaskStageCategory.Review)
        {
            warnings.Add(GanttWarning(
                "MISSING_ACTIVE_PLANNED_END",
                "Active work has no planned end date.",
                GanttWarningSeverity.Warning,
                "Task",
                changedTask.Id,
                "plannedEndDate"));
        }

        var tasksById = effectiveTasks.ToDictionary(task => task.Id);
        var derivedById = effectiveTasks.ToDictionary(
            task => task.Id,
            task => ParentTaskDerivedValuesCalculator.Calculate(task, effectiveTasks, CategoryOf));
        var affectedTaskIds = changedTask.ParentTaskItemId.HasValue
            ? new HashSet<Guid> { changedTask.Id, changedTask.ParentTaskItemId.Value }
            : new HashSet<Guid> { changedTask.Id };
        foreach (var dependency in dependencies.Where(dependency =>
                     affectedTaskIds.Contains(dependency.PredecessorTaskItemId) ||
                     affectedTaskIds.Contains(dependency.SuccessorTaskItemId)))
        {
            if (dependency.DependencyType != TaskDependencyType.FinishToStart)
            {
                warnings.Add(GanttWarning(
                    "LEGACY_DEPENDENCY_TYPE",
                    "This legacy dependency type is read-only; new authoring supports Finish-to-Start only.",
                    GanttWarningSeverity.Warning,
                    "Dependency",
                    dependency.Id,
                    "type"));
                continue;
            }

            if (!tasksById.ContainsKey(dependency.PredecessorTaskItemId) ||
                !tasksById.ContainsKey(dependency.SuccessorTaskItemId))
            {
                continue;
            }

            var predecessor = derivedById[dependency.PredecessorTaskItemId];
            var successor = derivedById[dependency.SuccessorTaskItemId];
            if (predecessor.PlannedEndDate.HasValue &&
                successor.PlannedStartDate.HasValue &&
                predecessor.PlannedEndDate.Value > successor.PlannedStartDate.Value)
            {
                warnings.Add(GanttWarning(
                    "DEPENDENCY_VIOLATION",
                    "The predecessor is planned to finish after the successor starts. No dates were changed automatically.",
                    GanttWarningSeverity.Warning,
                    "Dependency",
                    dependency.Id,
                    "plannedStartDate"));
            }
        }

        return new GanttWarningBuildResult(OrderedGanttWarnings(warnings), false);
    }

    private async Task<Result<GanttEditCommandResponse>> CommitGanttTaskAsync(
        TaskItem task,
        Project project,
        string action,
        string change,
        IReadOnlyCollection<string> changedFields,
        IReadOnlyList<GanttWarningResponse> warnings,
        CancellationToken cancellationToken)
    {
        var actor = Actor();
        await ReconcileAutomaticWatchAsync(task, null, cancellationToken);
        task.VersionNo++;
        await audit.LogAsync(new AuditLogEntry(
            actor,
            action,
            "TaskItem",
            task.Id,
            action,
            WorkspaceId: task.WorkspaceId,
            ProjectId: task.ProjectId,
            Metadata: new Dictionary<string, object?>
            {
                ["versionBefore"] = task.VersionNo - 1,
                ["changedFields"] = changedFields.ToArray()
            }), cancellationToken);
        await invalidations.TaskChangedAsync(
            task,
            actor,
            change,
            changedFields,
            RelatedUsers(task),
            cancellationToken);
        await invalidations.ProjectChangedAsync(project, actor, change, cancellationToken);
        await AdvanceParentForChildMutationAsync(task, actor, action, cancellationToken);
        var save = await unitOfWork.SaveTaskCommandAsync(cancellationToken);
        if (!save.IsSaved)
        {
            try
            {
                return Fail<GanttEditCommandResponse>(
                    save.Result == TaskCommandSaveResult.ConcurrencyConflict
                        ? "GANTT_STALE_VERSION"
                        : "GANTT_CONFLICT",
                    "Work item has changed. Refetch and retry.");
            }
            finally
            {
                unitOfWork.ClearTaskCommandTracking();
            }
        }

        return Result<GanttEditCommandResponse>.Success(new GanttEditCommandResponse(
            task.Id,
            WorkItemKind.Task,
            task.PlannedStartDate,
            task.PlannedEndDate,
            null,
            task.ProgressPercent,
            task.VersionNo,
            OrderedGanttWarnings(warnings)));
    }

    private async Task<Result<GanttEditCommandResponse>> CommitGanttMilestoneAsync(
        Milestone milestone,
        Project project,
        string action,
        string change,
        IReadOnlyList<GanttWarningResponse> warnings,
        CancellationToken cancellationToken)
    {
        var actor = Actor();
        milestone.VersionNo++;
        await audit.LogAsync(new AuditLogEntry(
            actor,
            action,
            "Milestone",
            milestone.Id,
            action,
            WorkspaceId: project.WorkspaceId,
            ProjectId: project.Id,
            Metadata: new Dictionary<string, object?>
            {
                ["versionBefore"] = milestone.VersionNo - 1
            }), cancellationToken);
        await invalidations.ProjectChangedAsync(project, actor, change, cancellationToken);
        var save = await unitOfWork.SaveTaskCommandAsync(cancellationToken);
        if (!save.IsSaved)
        {
            try
            {
                return Fail<GanttEditCommandResponse>(
                    save.Result == TaskCommandSaveResult.ConcurrencyConflict
                        ? "GANTT_STALE_VERSION"
                        : "GANTT_CONFLICT",
                    "Work item has changed. Refetch and retry.");
            }
            finally
            {
                unitOfWork.ClearTaskCommandTracking();
            }
        }

        return Result<GanttEditCommandResponse>.Success(new GanttEditCommandResponse(
            milestone.Id,
            WorkItemKind.Milestone,
            null,
            null,
            milestone.DueDate,
            milestone.Status == MilestoneStatus.Completed ? 100 : 0,
            milestone.VersionNo,
            OrderedGanttWarnings(warnings)));
    }

    private static GanttWarningResponse GanttWarning(
        string code,
        string message,
        GanttWarningSeverity severity,
        string targetType,
        Guid targetId,
        string? field) =>
        new(code, message, severity, targetType, targetId, field);

    private static IReadOnlyList<GanttWarningResponse> OrderedGanttWarnings(IEnumerable<GanttWarningResponse> warnings) =>
        warnings
            .Distinct()
            .OrderBy(warning => warning.Code, StringComparer.Ordinal)
            .ThenBy(warning => warning.TargetId)
            .ToList();

    private static Result<GanttEditCommandResponse> GanttItemLimitFailure() =>
        Fail<GanttEditCommandResponse>(
            "GANTT_ITEM_LIMIT_EXCEEDED",
            $"The Project schedule exceeds the supported limit of {MaximumGanttItems} work items.");

    private async Task<bool> GanttItemLimitExceededAsync(
        Guid projectId,
        CancellationToken cancellationToken) =>
        await projects.CountGanttItemsBoundedAsync(
            projectId,
            MaximumGanttItems + 1,
            cancellationToken) > MaximumGanttItems;

    private static Result<GanttEditCommandResponse> GanttDependencyLimitFailure() =>
        Fail<GanttEditCommandResponse>(
            "GANTT_DEPENDENCY_LIMIT_EXCEEDED",
            "The Project dependency graph exceeds the supported schedule limit.");

    private sealed record GanttWarningBuildResult(
        IReadOnlyList<GanttWarningResponse> Warnings,
        bool DependencyLimitExceeded);

    private sealed record TaskCommitOptions(
        TaskNotificationRecipientRequest? Notification = null,
        string? AssignmentChange = null,
        IReadOnlyCollection<Guid>? AssignmentAffectedUserIds = null,
        IReadOnlyDictionary<string, object?>? AuditMetadata = null,
        IReadOnlyCollection<string>? ChangedFields = null);

    private async Task<(TaskItem? Value, (string Code, string Message)? Error)> AuthorizedTaskAsync(Guid taskId, bool mutate, bool requireAssign = false, bool requireOverride = false, CancellationToken cancellationToken = default, bool includeDeleted = false, bool requireReview = false)
    {
        var task = await projects.GetTaskAsync(taskId, cancellationToken);
        if (!TryActor(out var actor) || task is null || (!includeDeleted && task.DeletedAt.HasValue) || !await projectAuthorization.CanViewProject(actor, task.ProjectId, cancellationToken)) return (null, ("TASK_NOT_FOUND", "Task not found."));
        var project = await projects.GetProjectAsync(task.ProjectId, cancellationToken);
        if (mutate && (project is null || project.DeletedAt.HasValue || project.Status is ProjectStatus.Archived or ProjectStatus.Deleted)) return (null, ("TASK_TRANSITION_GUARD_FAILED", "Project is read-only."));
        // Restore is the only command that intentionally authorizes a soft-deleted
        // Task. Normal assignment/deletion authorization remains fail-closed for
        // deleted rows; Restore requires both current operational contribution
        // authority and current Project management authority.
        var allowed = requireOverride
            ? await taskAuthorization.CanOverrideTaskReview(actor, task.Id, cancellationToken)
            : requireReview
                ? await taskAuthorization.CanReviewTask(actor, task.Id, cancellationToken)
                : requireAssign
                    ? includeDeleted
                        ? await projectAuthorization.CanContributeProject(actor, task.ProjectId, cancellationToken) &&
                          await projectAuthorization.CanManageProject(actor, task.ProjectId, cancellationToken)
                        : await taskAuthorization.CanAssignTask(actor, task.Id, cancellationToken)
                    : !mutate || await taskAuthorization.CanUpdateTask(actor, task.Id, cancellationToken);
        return allowed ? (task, null) : (null, ("TASK_FORBIDDEN", "Task operation is not authorized."));
    }

    private async Task<Result<TaskCommandResponse>> CommitAsync(
        TaskItem task,
        string action,
        string change,
        CancellationToken cancellationToken,
        bool overrideApplied = false,
        string? reason = null,
        IReadOnlyCollection<Guid>? effectiveCollaboratorUserIds = null,
        bool projectChanged = false,
        TaskCommitOptions? options = null)
    {
        var actor = Actor();
        await ReconcileAutomaticWatchAsync(task, effectiveCollaboratorUserIds, cancellationToken);
        // Relationship-only commands also advance the aggregate token.  Set it before
        // queuing the transactional invalidation so its version matches the committed row.
        task.VersionNo++;
        var auditMetadata = new Dictionary<string, object?>
        {
            ["versionBefore"] = task.VersionNo - 1,
            ["reasonProvided"] = !string.IsNullOrWhiteSpace(reason)
        };
        if (options?.AuditMetadata is not null)
        {
            foreach (var pair in options.AuditMetadata)
            {
                auditMetadata[pair.Key] = pair.Value;
            }
        }
        await audit.LogAsync(new AuditLogEntry(actor, action, "TaskItem", task.Id, action, WorkspaceId: task.WorkspaceId, ProjectId: task.ProjectId, Metadata: auditMetadata), cancellationToken);

        var affectedUsers = RelatedUsers(task)
            .Concat(options?.AssignmentAffectedUserIds ?? [])
            .Where(userId => userId != Guid.Empty)
            .Distinct()
            .ToArray();
        await invalidations.TaskChangedAsync(task, actor, change, options?.ChangedFields, affectedUsers, cancellationToken);
        if (!string.IsNullOrWhiteSpace(options?.AssignmentChange))
        {
            await invalidations.TaskAssignmentChangedAsync(
                task,
                actor,
                options.AssignmentChange,
                options.AssignmentAffectedUserIds,
                cancellationToken);
        }
        if (options?.Notification is not null && taskNotifications is not null)
        {
            await taskNotifications.ProduceAsync(options.Notification, cancellationToken);
        }
        await AdvanceParentForChildMutationAsync(task, actor, action, cancellationToken);
        if (projectChanged)
        {
            var project = await projects.GetProjectAsync(task.ProjectId, cancellationToken);
            if (project is null)
                return Fail<TaskCommandResponse>("TASK_NOT_FOUND", "Task not found.");
            await invalidations.ProjectChangedAsync(project, actor, change, cancellationToken);
        }
        var save = await unitOfWork.SaveTaskCommandAsync(cancellationToken);
        if (!save.IsSaved)
        {
            try
            {
                if (save.Result == TaskCommandSaveResult.ConcurrencyConflict)
                    return Fail<TaskCommandResponse>("TASK_STALE_VERSION", "Task has changed. Refetch and retry.");

                if (string.Equals(save.ConstraintName, TaskCommandConstraintNames.WorkItemWatchStateIdentity, StringComparison.Ordinal))
                {
                    // Automatic-source reconciliation shares this save boundary.
                    // Only its own identity constraint has a Task-specific
                    // classification, and only after fresh authorization.
                    var current = await AuthorizedTaskAsync(task.Id, true, cancellationToken: cancellationToken);
                    if (current.Error is not null)
                        return Fail<TaskCommandResponse>(current.Error.Value.Code, current.Error.Value.Message);
                    return Fail<TaskCommandResponse>("TASK_STALE_VERSION", "Task has changed. Refetch and retry.");
                }

                return Fail<TaskCommandResponse>("TASK_CONFLICT", "Task command conflicts with current data.");
            }
            finally
            {
                unitOfWork.ClearTaskCommandTracking();
            }
        }
        return Result<TaskCommandResponse>.Success(new TaskCommandResponse(await ToResponseAsync(task, actor, cancellationToken), [], overrideApplied));
    }

    private async Task<Result<TaskCommandResponse>> CurrentCommandResponseAsync(
        TaskItem task,
        CancellationToken cancellationToken) =>
        Result<TaskCommandResponse>.Success(new TaskCommandResponse(
            await ToResponseAsync(task, Actor(), cancellationToken),
            []));

    /// <summary>
    /// A direct child is part of its parent's canonical detail response (derived
    /// fields and the subtask summary/page).  Keep the parent aggregate token,
    /// audit trail, and invalidation in the same save boundary as the child.
    /// </summary>
    private async Task AdvanceParentForChildMutationAsync(TaskItem child, Guid actor, string childAction, CancellationToken cancellationToken)
    {
        if (!child.ParentTaskItemId.HasValue)
            return;

        var parent = await projects.GetTaskAsync(child.ParentTaskItemId.Value, cancellationToken);
        if (parent is null || parent.DeletedAt.HasValue)
            return;

        parent.VersionNo++;
        await audit.LogAsync(new AuditLogEntry(
            actor,
            "TaskSubtasksChanged",
            "TaskItem",
            parent.Id,
            WorkspaceId: parent.WorkspaceId,
            ProjectId: parent.ProjectId,
            Metadata: new Dictionary<string, object?>
            {
                ["childTaskId"] = child.Id,
                ["childAction"] = childAction,
                ["versionBefore"] = parent.VersionNo - 1
            }), cancellationToken);
        await invalidations.TaskChangedAsync(parent, actor, "subtasksChanged", cancellationToken: cancellationToken);
    }

    private async Task ReconcileAutomaticWatchAsync(TaskItem task, IReadOnlyCollection<Guid>? effectiveCollaboratorUserIds, CancellationToken cancellationToken)
    {
        var collaboratorIds = effectiveCollaboratorUserIds ?? (await projects.ListCollaboratorsAsync(task.Id, cancellationToken)).Select(item => item.UserId).ToArray();
        var sources = AutomaticSourcesFor(task, collaboratorIds);
        var states = (await projects.ListWatchStatesAsync(task.Id, cancellationToken)).ToDictionary(x => x.UserId);
        foreach (var userId in states.Keys.Union(sources.Keys).ToList())
        {
            if (!states.TryGetValue(userId, out var state))
            {
                var initialSources = sources.GetValueOrDefault(userId);
                state = new WorkItemWatchState { TaskItemId = task.Id, UserId = userId, AutomaticSources = initialSources, IsWatching = TaskWatchStateRules.IsWatching(false, false, initialSources), UpdatedAt = clock.UtcNow, VersionNo = 1 };
                await projects.AddWatchStateAsync(state, cancellationToken);
                continue;
            }
            var automaticSources = sources.GetValueOrDefault(userId);
            if (state.AutomaticSources == automaticSources && TaskWatchStateRules.IsWatching(state.IsManualWatch, state.IsExplicitOptOut, automaticSources) == state.IsWatching) continue;
            state.AutomaticSources = automaticSources;
            TaskWatchStateRules.Normalize(state);
            state.UpdatedAt = clock.UtcNow; state.VersionNo++;
        }
    }

    private async Task<WorkItemWatchAutomaticSource> AutomaticSourcesForAsync(TaskItem task, Guid userId, IReadOnlyCollection<Guid>? effectiveCollaboratorUserIds, CancellationToken cancellationToken)
    {
        var collaborators = effectiveCollaboratorUserIds ?? (await projects.ListCollaboratorsAsync(task.Id, cancellationToken)).Select(item => item.UserId).ToArray();
        return AutomaticSourcesFor(task, collaborators).GetValueOrDefault(userId);
    }

    private static Dictionary<Guid, WorkItemWatchAutomaticSource> AutomaticSourcesFor(TaskItem task, IEnumerable<Guid> collaboratorIds)
    {
        var sources = new Dictionary<Guid, WorkItemWatchAutomaticSource> { [task.CreatedByUserId] = WorkItemWatchAutomaticSource.Creator };
        void Add(Guid? userId, WorkItemWatchAutomaticSource source)
        {
            if (userId.HasValue)
                sources[userId.Value] = sources.GetValueOrDefault(userId.Value) | source;
        }
        Add(task.PrimaryAssigneeUserId, WorkItemWatchAutomaticSource.PrimaryAssignee);
        Add(task.ReviewerUserId, WorkItemWatchAutomaticSource.Reviewer);
        foreach (var collaboratorId in collaboratorIds)
            Add(collaboratorId, WorkItemWatchAutomaticSource.Collaborator);
        return sources;
    }

    private async Task<CanonicalTaskResponse> ToResponseAsync(TaskItem task, Guid actor, CancellationToken cancellationToken)
    {
        var stage = task.WorkflowStage ?? (task.WorkflowStageId.HasValue ? await projects.GetWorkflowStageAsync(task.WorkflowStageId.Value, cancellationToken) : null);
        var relationships = await RelationshipsAsync(task, cancellationToken);
        var canUpdate = await taskAuthorization.CanUpdateTask(actor, task.Id, cancellationToken);
        var canAssign = await taskAuthorization.CanAssignTask(actor, task.Id, cancellationToken);
        var canReview = await taskAuthorization.CanReviewTask(actor, task.Id, cancellationToken);
        var categories = (await projects.ListWorkflowStagesAsync(task.ProjectId, cancellationToken)).Select(item => item.InternalCategory).Distinct().ToList();
        var projectTasks = await projects.ListTasksAsync(task.ProjectId, cancellationToken);
        var derived = ParentTaskDerivedValuesCalculator.Calculate(task, projectTasks, CategoryOf);
        var start = derived.PlannedStartDate;
        var end = derived.PlannedEndDate;
        var progress = derived.ProgressPercent;
        var timeZone = await timeZones.ResolveAsync(task.TenantId, task.WorkspaceId, cancellationToken);
        var checklist = await projects.ListChecklistAsync(task.Id, cancellationToken);
        var labels = await projects.ListWorkItemLabelsAsync(task.Id, cancellationToken);
        var subresources = new TaskSubresourceSummary(checklist.Count(x => x.IsCompleted), checklist.Count, await projects.CountTaskCommentsAsync(task.Id, cancellationToken), labels.Count, projectTasks.Count(child => child.ParentTaskItemId == task.Id && !child.DeletedAt.HasValue));
        return new CanonicalTaskResponse(task.Id, task.TenantId, task.WorkspaceId, task.ProjectId, task.Kind, task.ParentTaskItemId, task.MilestoneId, task.Title, task.Description, task.WorkflowStageId, stage?.Name ?? CategoryOf(task).ToString(), stage?.InternalCategory ?? CategoryOf(task), task.Priority.ToString(), task.IsBlocked, canUpdate || canAssign ? task.BlockedReason : null, start, end, task.DeadlineAt, task.ActualStartAt, task.CompletedAt, progress, derived.IsDerived, task.EstimatedEffortMinutes, relationships.PrimaryAssignee, task.TargetGroupId, relationships.Collaborators.Count, relationships.Reviewer, TaskDeadlineCalculator.IsOverdue(task, CategoryOf(task), timeZone, clock.UtcNow, end), [], task.VersionNo, new TaskCommandPermissions(canUpdate, canAssign, await taskAuthorization.CanDeleteTask(actor, task.Id, cancellationToken), canReview, await taskAuthorization.CanOverrideTaskReview(actor, task.Id, cancellationToken), !task.PrimaryAssigneeUserId.HasValue && task.TargetGroupId.HasValue), categories, task.ReviewStatus, subresources, ToBrief(task));
    }

    private static TaskBriefResponse ToBrief(TaskItem task) => new(
        ToBriefField(task.BriefGoal),
        ToBriefField(task.BriefDeliverable),
        ToBriefField(task.BriefConstraints));

    private static TaskBriefFieldResponse ToBriefField(string? value)
    {
        var normalized = TaskBriefText.Normalize(value);
        return new(
            normalized,
            normalized is null ? TaskBriefValueSource.NotSet : TaskBriefValueSource.TaskSpecific);
    }

    private async Task<TaskRelationshipsResponse> RelationshipsAsync(TaskItem task, CancellationToken cancellationToken)
    {
        TaskPersonSummary? assignee = task.PrimaryAssigneeUserId.HasValue ? await PersonAsync(task.PrimaryAssigneeUserId.Value, cancellationToken) : null;
        TaskPersonSummary? reviewer = task.ReviewerUserId.HasValue ? await PersonAsync(task.ReviewerUserId.Value, cancellationToken) : null;
        var collaborators = new List<TaskPersonSummary>(); foreach (var item in await projects.ListCollaboratorsAsync(task.Id, cancellationToken)) { var person = await PersonAsync(item.UserId, cancellationToken); if (person is not null) collaborators.Add(person); }
        return new TaskRelationshipsResponse(assignee, task.TargetGroupId, collaborators, reviewer, task.VersionNo);
    }

    private async Task<TaskPersonSummary?> PersonAsync(Guid userId, CancellationToken cancellationToken) { var user = await users.GetByIdAsync(userId, cancellationToken); return user is null ? null : new TaskPersonSummary(user.Id, user.DisplayName); }
    private ITaskRelationshipTargetPolicy RelationshipTargets => relationshipTargets ?? new TaskRelationshipTargetPolicy(projects, users, projectAuthorization);
    private async Task<bool> IsProjectMemberAsync(Guid projectId, Guid userId, CancellationToken cancellationToken) => await projects.GetMemberAsync(projectId, userId, cancellationToken) is not null;
    private static TaskStageCategory CategoryOf(TaskItem task) => task.WorkflowStage?.InternalCategory ?? task.Status switch { TaskItemStatus.InProgress => TaskStageCategory.InProgress, TaskItemStatus.WaitingReview => TaskStageCategory.Review, TaskItemStatus.Completed => TaskStageCategory.Done, TaskItemStatus.Cancelled => TaskStageCategory.Cancelled, _ => TaskStageCategory.Todo };
    private static TaskItemStatus LegacyStatus(TaskStageCategory category) => category switch { TaskStageCategory.InProgress => TaskItemStatus.InProgress, TaskStageCategory.Review => TaskItemStatus.WaitingReview, TaskStageCategory.Done => TaskItemStatus.Completed, TaskStageCategory.Cancelled => TaskItemStatus.Cancelled, _ => TaskItemStatus.NotStarted };
    private static (string Code, string Message)? EnsureVersion(TaskItem task, long expected) => expected == task.VersionNo ? null : ("TASK_STALE_VERSION", "Task has changed. Refetch and retry.");
    private static bool IsBounded(string? value) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 1000;
    private bool TryActor(out Guid actor) { actor = currentUser.UserId ?? Guid.Empty; return currentUser.IsAuthenticated && actor != Guid.Empty; }
    private Guid Actor() => TryActor(out var actor) ? actor : Guid.Empty;
    private static IEnumerable<Guid> RelatedUsers(TaskItem task) => new[] { task.CreatedByUserId, task.PrimaryAssigneeUserId ?? Guid.Empty, task.ReviewerUserId ?? Guid.Empty }.Where(id => id != Guid.Empty);
    private static Result<T> Fail<T>(string code, string message) => Result<T>.Failure($"{code}|{message}");
    private static TaskWatchStateResponse ToWatchResponse(WorkItemWatchState? state) => new(state?.IsWatching ?? false, state?.IsExplicitOptOut ?? false, state is null ? [] : Enum.GetValues<WorkItemWatchAutomaticSource>().Where(x => x != WorkItemWatchAutomaticSource.None && state.AutomaticSources.HasFlag(x)).Select(x => x.ToString()).ToArray(), state?.VersionNo ?? 0);
}
