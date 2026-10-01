using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Realtime;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;

namespace Coglatas.Application.Projects;

public sealed class ProjectKanbanService(
    IProjectRepository projects,
    IProjectKanbanRepository kanban,
    IProjectAuthorizationService projectAuthorization,
    ICurrentUser currentUser,
    IClock clock,
    ITaskWorkspaceTimeZoneResolver timeZones,
    IAuditLogger audit,
    IBusinessInvalidationPublisher invalidations,
    ITaskCommandUnitOfWork unitOfWork) : IProjectKanbanService
{
    private const int DoneWindowDays = 30;
    private const int RankGap = 1000;
    private const int MaximumRebalanceCards = 1000;
    private static readonly ProjectKanbanSwimlane[] SupportedSwimlanes = Enum.GetValues<ProjectKanbanSwimlane>();
    private static readonly string[] SupportedFilters = ["primaryAssigneeUserId", "targetGroupId", "priority", "parentTaskId", "includeOlderCompleted"];

    public async Task<Result<ProjectKanbanSnapshot>> GetAsync(Guid projectId, ProjectKanbanQuery query, CancellationToken cancellationToken = default)
    {
        var access = await GetVisibleProjectAsync(projectId, cancellationToken);
        if (access.Error is not null)
            return Fail<ProjectKanbanSnapshot>(access.Error.Value.Code, access.Error.Value.Message);

        return await BuildSnapshotAsync(access.Project!, access.Actor, query, cancellationToken);
    }

    public async Task<Result<ProjectKanbanCommandResponse>> UpdateConfigAsync(
        Guid projectId,
        UpdateProjectKanbanConfigRequest request,
        CancellationToken cancellationToken = default)
    {
        var access = await GetVisibleProjectAsync(projectId, cancellationToken);
        if (access.Error is not null)
            return Fail<ProjectKanbanCommandResponse>(access.Error.Value.Code, access.Error.Value.Message);
        if (!await projectAuthorization.CanManageProject(access.Actor, projectId, cancellationToken))
            return Fail<ProjectKanbanCommandResponse>("KANBAN_FORBIDDEN", "Project board configuration is not authorized.");
        if (IsReadOnly(access.Project!))
            return Fail<ProjectKanbanCommandResponse>("KANBAN_PROJECT_READ_ONLY", "Archived or deleted Projects cannot be changed.");
        if (request.ExpectedBoardVersion <= 0)
            return Fail<ProjectKanbanCommandResponse>("KANBAN_INVALID_EXPECTED_VERSION", "Expected board version must be a positive integer.");
        if (!Enum.IsDefined(request.DefaultSwimlane))
            return Fail<ProjectKanbanCommandResponse>("KANBAN_INVALID_SWIMLANE", "Default swimlane is invalid.");

        var definition = await kanban.GetDefinitionForUpdateAsync(projectId, cancellationToken);
        if (definition is null)
            return Fail<ProjectKanbanCommandResponse>("KANBAN_NOT_CONFIGURED", "The Project board is not configured.");
        if (definition.VersionNo != request.ExpectedBoardVersion)
            return Fail<ProjectKanbanCommandResponse>("KANBAN_STALE_BOARD", "The Project board changed. Refetch and retry.");

        var requestedColumns = request.Columns;
        var stages = definition.Stages.OrderBy(stage => stage.SortKey).ThenBy(stage => stage.Id).ToList();
        if (stages.Count == 0)
            return Fail<ProjectKanbanCommandResponse>("KANBAN_NOT_CONFIGURED", "The Project board has no Workflow Stages.");
        if (requestedColumns.Count != stages.Count ||
            requestedColumns.Select(item => item.WorkflowStageId).Distinct().Count() != stages.Count ||
            stages.Any(stage => requestedColumns.All(item => item.WorkflowStageId != stage.Id)))
        {
            return Fail<ProjectKanbanCommandResponse>("KANBAN_INVALID_COLUMNS", "Configuration must include every existing Workflow Stage exactly once.");
        }
        if (requestedColumns.Select(item => item.DisplayOrder).Distinct().Count() != stages.Count)
            return Fail<ProjectKanbanCommandResponse>("KANBAN_INVALID_COLUMNS", "Workflow Stage display orders must be unique.");
        if (requestedColumns.Any(item => item.WipWarningLimit is <= 0 or > 9999))
            return Fail<ProjectKanbanCommandResponse>("KANBAN_INVALID_WIP_LIMIT", "WIP warning limits must be between 1 and 9999 when supplied.");

        var ordered = requestedColumns.OrderBy(item => item.DisplayOrder).ThenBy(item => item.WorkflowStageId).ToList();
        var columnOrderChanged = !ordered.Select(item => item.WorkflowStageId)
            .SequenceEqual(stages.Select(stage => stage.Id));
        var maximumCurrentSortKey = stages.Max(stage => stage.SortKey);
        if (columnOrderChanged && maximumCurrentSortKey > long.MaxValue - ((ordered.Count + 1L) * RankGap))
            return Fail<ProjectKanbanCommandResponse>("KANBAN_CONFIG_CONFLICT", "Workflow Stage order cannot be advanced safely.");
        // Assign a fresh non-overlapping key range. The existing unique
        // (DefinitionId, SortKey) index therefore remains valid even when EF
        // persists a column swap as separate UPDATE statements.
        var freshSortKeyBase = maximumCurrentSortKey + RankGap;
        var changedStageIds = new List<Guid>();
        for (var index = 0; index < ordered.Count; index++)
        {
            var requestColumn = ordered[index];
            var stage = stages.Single(item => item.Id == requestColumn.WorkflowStageId);
            var sortKey = columnOrderChanged ? freshSortKeyBase + (index * RankGap) : stage.SortKey;
            if (stage.SortKey == sortKey && stage.WipWarningLimit == requestColumn.WipWarningLimit)
                continue;
            stage.SortKey = sortKey;
            stage.WipWarningLimit = requestColumn.WipWarningLimit;
            stage.VersionNo++;
            changedStageIds.Add(stage.Id);
        }

        var defaultChanged = definition.KanbanDefaultSwimlane != request.DefaultSwimlane;
        if (changedStageIds.Count == 0 && !defaultChanged)
        {
            var unchanged = await BuildSnapshotAsync(access.Project!, access.Actor, new(), cancellationToken);
            return unchanged.IsSuccess
                ? Result<ProjectKanbanCommandResponse>.Success(new(unchanged.Value!, null, unchanged.Value!.Board.Warnings))
                : Result<ProjectKanbanCommandResponse>.Failure(unchanged.Error!);
        }

        var versionBefore = definition.VersionNo;
        definition.KanbanDefaultSwimlane = request.DefaultSwimlane;
        definition.VersionNo++;
        await audit.LogAsync(new AuditLogEntry(
            access.Actor,
            "ProjectKanbanConfigured",
            "TaskWorkflowDefinition",
            definition.Id,
            "Project Kanban configuration updated.",
            WorkspaceId: definition.WorkspaceId,
            ProjectId: projectId,
            Metadata: new Dictionary<string, object?>
            {
                ["versionBefore"] = versionBefore,
                ["versionAfter"] = definition.VersionNo,
                ["defaultSwimlane"] = definition.KanbanDefaultSwimlane.ToString(),
                ["changedStageCount"] = changedStageIds.Count
            }), cancellationToken);
        await invalidations.ProjectChangedAsync(access.Project!, access.Actor, "kanbanConfigurationChanged", cancellationToken);

        var save = await unitOfWork.SaveTaskCommandAsync(cancellationToken);
        if (!save.IsSaved)
        {
            unitOfWork.ClearTaskCommandTracking();
            return Fail<ProjectKanbanCommandResponse>(
                save.Result == TaskCommandSaveResult.ConcurrencyConflict ? "KANBAN_STALE_BOARD" : "KANBAN_CONFLICT",
                "The Project board changed. Refetch and retry.");
        }

        var snapshot = await BuildSnapshotAsync(access.Project!, access.Actor, new(), cancellationToken);
        return snapshot.IsSuccess
            ? Result<ProjectKanbanCommandResponse>.Success(new(snapshot.Value!, null, snapshot.Value!.Board.Warnings))
            : Result<ProjectKanbanCommandResponse>.Failure(snapshot.Error!);
    }

    public async Task<Result<ProjectKanbanCommandResponse>> MoveAsync(
        Guid taskId,
        MoveTaskOnKanbanRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TryActor(out var actor))
            return Fail<ProjectKanbanCommandResponse>("KANBAN_NOT_FOUND", "Project board not found.");

        var task = await projects.GetTaskAsync(taskId, cancellationToken);
        if (task is null || task.DeletedAt.HasValue || !await projectAuthorization.CanViewProject(actor, task.ProjectId, cancellationToken))
            return Fail<ProjectKanbanCommandResponse>("KANBAN_NOT_FOUND", "Project board not found.");
        var project = await projects.GetProjectAsync(task.ProjectId, cancellationToken);
        if (project is null || IsReadOnly(project))
            return Fail<ProjectKanbanCommandResponse>("KANBAN_PROJECT_READ_ONLY", "Archived or deleted Projects cannot be changed.");

        var canManage = await projectAuthorization.CanManageProject(actor, task.ProjectId, cancellationToken);
        var canMove = canManage || task.CreatedByUserId == actor || task.PrimaryAssigneeUserId == actor;
        if (!canMove)
            return Fail<ProjectKanbanCommandResponse>("KANBAN_FORBIDDEN", "Moving this Task is not authorized.");
        if (request.ExpectedTaskVersion <= 0 || request.ExpectedBoardVersion <= 0)
            return Fail<ProjectKanbanCommandResponse>("KANBAN_INVALID_EXPECTED_VERSION", "Expected Task and board versions must be positive integers.");
        if (task.VersionNo != request.ExpectedTaskVersion)
            return Fail<ProjectKanbanCommandResponse>("TASK_STALE_VERSION", "Task has changed. Refetch and retry.");

        var definition = await kanban.GetDefinitionForUpdateAsync(task.ProjectId, cancellationToken);
        if (definition is null)
            return Fail<ProjectKanbanCommandResponse>("KANBAN_NOT_CONFIGURED", "The Project board is not configured.");
        if (definition.VersionNo != request.ExpectedBoardVersion)
            return Fail<ProjectKanbanCommandResponse>("KANBAN_STALE_BOARD", "The Project board changed. Refetch and retry.");
        var targetStage = definition.Stages.SingleOrDefault(stage => stage.Id == request.TargetWorkflowStageId);
        if (targetStage is null)
            return Fail<ProjectKanbanCommandResponse>("TASK_INVALID_STAGE", "Workflow stage is not available for this Task.");

        var targetTasks = (await kanban.ListStageTasksForUpdateAsync(
            task.ProjectId,
            targetStage.Id,
            MaximumRebalanceCards + 1,
            cancellationToken)).ToList();
        var targetCardCount = targetTasks.Count + (targetTasks.Any(item => item.Id == task.Id) ? 0 : 1);
        if (targetCardCount > MaximumRebalanceCards)
            return Fail<ProjectKanbanCommandResponse>("KANBAN_BOARD_TOO_LARGE", "This Stage is too large for a bounded reorder.");
        var originalTargetRanks = targetTasks.ToDictionary(item => item.Id, item => item.SortKey);

        var position = BuildPosition(task, targetTasks, request.TargetBeforeTaskId, request.TargetAfterTaskId);
        if (position.Error is not null)
            return Fail<ProjectKanbanCommandResponse>(position.Error.Value.Code, position.Error.Value.Message);

        var sourceStageId = task.WorkflowStageId;
        var sourceSortKey = task.SortKey;
        AppliedTaskTransition? transition = null;
        if (task.WorkflowStageId != targetStage.Id)
        {
            var applied = await TaskTransitionEngine.ApplyAsync(projects, clock, task, targetStage.Id, request.Reason, cancellationToken);
            if (!applied.IsSuccess)
                return Result<ProjectKanbanCommandResponse>.Failure(applied.Error!);
            transition = applied.Value;
        }

        var ordered = position.Ordered!;
        var sourceOrder = targetTasks.OrderBy(item => item.SortKey).ThenBy(item => item.Id).Select(item => item.Id).ToArray();
        var targetOrder = ordered.Select(item => item.Id).ToArray();
        var relativeOrderChanged = task.WorkflowStageId != sourceStageId || !sourceOrder.SequenceEqual(targetOrder);
        var rebalanced = false;
        if (relativeOrderChanged)
        {
            var rank = RankAt(ordered, position.Index);
            if (rank.HasValue)
            {
                task.SortKey = rank.Value;
            }
            else
            {
                rebalanced = true;
                for (var index = 0; index < ordered.Count; index++)
                    ordered[index].SortKey = (index + 1L) * RankGap;
            }
        }

        var changedTasks = ordered
            .Where(item =>
                item.Id == task.Id && (transition is not null || task.SortKey != sourceSortKey || relativeOrderChanged) ||
                rebalanced && originalTargetRanks.TryGetValue(item.Id, out var originalRank) && item.SortKey != originalRank)
            .DistinctBy(item => item.Id)
            .ToList();
        if (changedTasks.All(item => item.Id != task.Id) && (task.WorkflowStageId != sourceStageId || task.SortKey != sourceSortKey))
            changedTasks.Add(task);

        if (changedTasks.Count == 0 && transition is null)
        {
            var unchanged = await BuildSnapshotAsync(project, actor, new(), cancellationToken);
            return unchanged.IsSuccess
                ? Result<ProjectKanbanCommandResponse>.Success(new(unchanged.Value!, task.Id, unchanged.Value!.Board.Warnings))
                : Result<ProjectKanbanCommandResponse>.Failure(unchanged.Error!);
        }

        // Every persisted rank belongs to the canonical Task aggregate. Advancing
        // each changed token makes a rebalance visible to optimistic clients.
        foreach (var changed in changedTasks)
        {
            changed.VersionNo++;
            await invalidations.TaskChangedAsync(
                changed,
                actor,
                changed.Id == task.Id ? "kanbanMoved" : "kanbanOrderRebalanced",
                changedFields: changed.Id == task.Id && sourceStageId != task.WorkflowStageId
                    ? ["workflowStageId", "sortKey"]
                    : ["sortKey"],
                affectedUserIds: RelatedUsers(changed),
                cancellationToken: cancellationToken);
        }

        var boardVersionBefore = definition.VersionNo;
        definition.VersionNo++;
        await audit.LogAsync(new AuditLogEntry(
            actor,
            "TaskKanbanMoved",
            "TaskItem",
            task.Id,
            "Task Stage or board order changed.",
            WorkspaceId: task.WorkspaceId,
            ProjectId: task.ProjectId,
            Metadata: new Dictionary<string, object?>
            {
                ["sourceWorkflowStageId"] = sourceStageId,
                ["targetWorkflowStageId"] = task.WorkflowStageId,
                ["sourceSortKey"] = sourceSortKey,
                ["targetSortKey"] = task.SortKey,
                ["boardVersionBefore"] = boardVersionBefore,
                ["boardVersionAfter"] = definition.VersionNo,
                ["rebalanceCount"] = rebalanced ? changedTasks.Count : 0
            }), cancellationToken);
        if (rebalanced)
        {
            await audit.LogAsync(new AuditLogEntry(
                actor,
                "ProjectKanbanOrderRebalanced",
                "TaskWorkflowDefinition",
                definition.Id,
                "Project Kanban ranks rebalanced.",
                WorkspaceId: definition.WorkspaceId,
                ProjectId: definition.ProjectId,
                Metadata: new Dictionary<string, object?> { ["changedTaskCount"] = changedTasks.Count }), cancellationToken);
        }

        if (transition is not null)
            await AdvanceParentForStageChangeAsync(task, actor, cancellationToken);
        await invalidations.ProjectChangedAsync(project, actor, "kanbanMoved", cancellationToken);

        var save = await unitOfWork.SaveTaskCommandAsync(cancellationToken);
        if (!save.IsSaved)
        {
            unitOfWork.ClearTaskCommandTracking();
            return Fail<ProjectKanbanCommandResponse>(
                "KANBAN_CONFLICT",
                "The Task or Project board changed. Refetch and retry.");
        }

        var snapshot = await BuildSnapshotAsync(project, actor, new(), cancellationToken);
        return snapshot.IsSuccess
            ? Result<ProjectKanbanCommandResponse>.Success(new(snapshot.Value!, task.Id, snapshot.Value!.Board.Warnings))
            : Result<ProjectKanbanCommandResponse>.Failure(snapshot.Error!);
    }

    private async Task<Result<ProjectKanbanSnapshot>> BuildSnapshotAsync(
        Project project,
        Guid actor,
        ProjectKanbanQuery query,
        CancellationToken cancellationToken)
    {
        var cutoff = clock.UtcNow.AddDays(-DoneWindowDays);
        var read = await kanban.ReadAsync(
            project.Id,
            cutoff,
            query.IncludeOlderCompleted,
            query.PrimaryAssigneeUserId,
            query.TargetGroupId,
            query.Priority,
            query.ParentTaskId,
            query.SafeMaxCards,
            cancellationToken);
        if (read is null)
            return Fail<ProjectKanbanSnapshot>("KANBAN_NOT_CONFIGURED", "The Project board is not configured.");

        var canManage = await projectAuthorization.CanManageProject(actor, project.Id, cancellationToken);
        var selectedSwimlane = query.Swimlane.HasValue && Enum.IsDefined(query.Swimlane.Value)
            ? query.Swimlane.Value
            : read.Definition.DefaultSwimlane;
        var warnings = new List<ProjectKanbanWarning>
        {
            new("KANBAN_DONE_WINDOW", $"Done shows the most recent {DoneWindowDays} days by default.")
        };
        if (read.TotalCount > read.Tasks.Count)
            warnings.Add(new("KANBAN_TRUNCATED", $"Showing the first {read.Tasks.Count} of {read.TotalCount} authorized cards."));

        var columns = read.Stages.Select(stage =>
        {
            var count = read.CountsByStage.GetValueOrDefault(stage.Id);
            var hasWarning = stage.WipWarningLimit.HasValue && count > stage.WipWarningLimit.Value;
            if (hasWarning)
            {
                warnings.Add(new(
                    "KANBAN_WIP_LIMIT_EXCEEDED",
                    $"{stage.Name} contains {count} cards; the warning limit is {stage.WipWarningLimit}.",
                    stage.Id,
                    count,
                    stage.WipWarningLimit));
            }
            return new ProjectKanbanColumn(
                stage.Id,
                stage.Name,
                stage.Category,
                stage.SortKey,
                stage.WipWarningLimit,
                count,
                hasWarning,
                new(canManage));
        }).ToList();

        var cards = read.Tasks.Select(item =>
        {
            var canMove = canManage || item.CreatedByUserId == actor || item.PrimaryAssigneeUserId == actor;
            var allowed = canMove
                ? AllowedTargetStages(item, read.Definition, read.Stages)
                : [];
            var lane = Swimlane(item, selectedSwimlane);
            return new ProjectKanbanCard(
                item.Id,
                item.Title,
                item.WorkflowStageId,
                item.SortKey,
                item.ParentTaskId,
                item.ParentTitle,
                item.ChildCount > 0,
                item.ChildCount == 0,
                item.CompletedChildCount,
                item.ChildCount,
                item.ProgressPercent,
                item.PlannedStartDate,
                item.PlannedEndDate,
                item.PrimaryAssigneeUserId,
                item.PrimaryAssigneeName ?? "Unassigned",
                item.TargetGroupId,
                item.TargetGroupName ?? "Ungrouped",
                item.Priority,
                item.IsBlocked,
                item.Version,
                lane.Key,
                lane.Label,
                new(true, canMove, allowed));
        }).ToList();

        var timeZone = await timeZones.ResolveAsync(read.Definition.TenantId, read.Definition.WorkspaceId, cancellationToken);
        var board = new ProjectKanbanBoard(
            project.Id,
            read.Definition.Version,
            timeZone.Id,
            read.Definition.DefaultSwimlane,
            selectedSwimlane,
            SupportedSwimlanes,
            SupportedFilters,
            query.IncludeOlderCompleted,
            DoneWindowDays,
            read.TotalCount,
            read.TotalCount > read.Tasks.Count,
            new(canManage),
            warnings);
        return Result<ProjectKanbanSnapshot>.Success(new(board, columns, cards));
    }

    private async Task AdvanceParentForStageChangeAsync(TaskItem child, Guid actor, CancellationToken cancellationToken)
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
            "Child Task stage changed from Project Kanban.",
            WorkspaceId: parent.WorkspaceId,
            ProjectId: parent.ProjectId,
            Metadata: new Dictionary<string, object?> { ["childTaskId"] = child.Id, ["childAction"] = "TaskKanbanMoved" }), cancellationToken);
        await invalidations.TaskChangedAsync(parent, actor, "subtasksChanged", ["subtasks", "progress", "plannedDates"], RelatedUsers(parent), cancellationToken);
    }

    private static IReadOnlyList<Guid> AllowedTargetStages(
        ProjectKanbanTaskReadModel task,
        ProjectKanbanDefinitionReadModel definition,
        IReadOnlyList<ProjectKanbanStageReadModel> stages)
    {
        var current = stages.First(stage => stage.Id == task.WorkflowStageId).Category;
        return stages
            .Where(stage =>
                stage.Id == task.WorkflowStageId ||
                (current is not (TaskStageCategory.Done or TaskStageCategory.Cancelled) ||
                 stage.Category is TaskStageCategory.Backlog or TaskStageCategory.Todo))
            .Where(stage => stage.Category != TaskStageCategory.InProgress || task.PrimaryAssigneeUserId.HasValue)
            .Where(stage => stage.Category != TaskStageCategory.Done ||
                task.IncompleteChildCount == 0 &&
                (!task.ReviewerUserId.HasValue || !definition.ReviewEnforcementEnabled || task.ReviewStatus == TaskReviewStatus.Accepted))
            .Select(stage => stage.Id)
            .ToList();
    }

    private static (string Key, string Label) Swimlane(ProjectKanbanTaskReadModel task, ProjectKanbanSwimlane swimlane) => swimlane switch
    {
        ProjectKanbanSwimlane.PrimaryAssignee => (task.PrimaryAssigneeUserId?.ToString("D") ?? "unassigned", task.PrimaryAssigneeName ?? "Unassigned"),
        ProjectKanbanSwimlane.TargetGroup => (task.TargetGroupId?.ToString("D") ?? "ungrouped", task.TargetGroupName ?? "Ungrouped"),
        ProjectKanbanSwimlane.Priority => (task.Priority.ToString(), task.Priority.ToString()),
        ProjectKanbanSwimlane.ParentTask => (task.ParentTaskId?.ToString("D") ?? "no-parent", task.ParentTitle ?? "No parent task"),
        _ => ("all", "All tasks")
    };

    private static (IReadOnlyList<TaskItem>? Ordered, int Index, (string Code, string Message)? Error) BuildPosition(
        TaskItem task,
        IReadOnlyList<TaskItem> targetTasks,
        Guid? beforeTaskId,
        Guid? afterTaskId)
    {
        if (beforeTaskId == task.Id || afterTaskId == task.Id || beforeTaskId.HasValue && beforeTaskId == afterTaskId)
            return (null, 0, ("KANBAN_INVALID_POSITION", "The requested card position is invalid."));

        var ordered = targetTasks.Where(item => item.Id != task.Id).OrderBy(item => item.SortKey).ThenBy(item => item.Id).ToList();
        var beforeIndex = beforeTaskId.HasValue ? ordered.FindIndex(item => item.Id == beforeTaskId.Value) : -1;
        var afterIndex = afterTaskId.HasValue ? ordered.FindIndex(item => item.Id == afterTaskId.Value) : -1;
        if (beforeTaskId.HasValue && beforeIndex < 0 || afterTaskId.HasValue && afterIndex < 0)
            return (null, 0, ("KANBAN_INVALID_POSITION", "The requested card position is invalid."));
        if (beforeTaskId.HasValue && afterTaskId.HasValue && afterIndex + 1 != beforeIndex)
            return (null, 0, ("KANBAN_INVALID_POSITION", "The requested neighboring cards are not adjacent."));

        var index = beforeTaskId.HasValue ? beforeIndex : afterTaskId.HasValue ? afterIndex + 1 : ordered.Count;
        ordered.Insert(index, task);
        return (ordered, index, null);
    }

    private static long? RankAt(IReadOnlyList<TaskItem> ordered, int index)
    {
        var previous = index > 0 ? ordered[index - 1].SortKey : (long?)null;
        var next = index < ordered.Count - 1 ? ordered[index + 1].SortKey : (long?)null;
        if (!previous.HasValue && !next.HasValue)
            return RankGap;
        if (!previous.HasValue)
            return next!.Value > long.MinValue + RankGap ? next.Value - RankGap : null;
        if (!next.HasValue)
            return previous.Value < long.MaxValue - RankGap ? previous.Value + RankGap : null;
        return next.Value - previous.Value > 1 ? previous.Value + ((next.Value - previous.Value) / 2) : null;
    }

    private async Task<(Project? Project, Guid Actor, (string Code, string Message)? Error)> GetVisibleProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        if (!TryActor(out var actor))
            return (null, Guid.Empty, ("KANBAN_NOT_FOUND", "Project board not found."));
        var project = await projects.GetProjectAsync(projectId, cancellationToken);
        if (project is null ||
            project.DeletedAt.HasValue ||
            project.Status is ProjectStatus.Archived or ProjectStatus.Deleted ||
            !await projectAuthorization.CanViewProject(actor, projectId, cancellationToken))
            return (null, actor, ("KANBAN_NOT_FOUND", "Project board not found."));
        return (project, actor, null);
    }

    private bool TryActor(out Guid actor)
    {
        actor = currentUser.UserId ?? Guid.Empty;
        return currentUser.IsAuthenticated && actor != Guid.Empty;
    }

    private static bool IsReadOnly(Project project) =>
        project.DeletedAt.HasValue || project.Status is ProjectStatus.Archived or ProjectStatus.Deleted;

    private static IEnumerable<Guid> RelatedUsers(TaskItem task) =>
        new[] { task.CreatedByUserId, task.PrimaryAssigneeUserId, task.ReviewerUserId }
            .Where(item => item.HasValue)
            .Select(item => item!.Value)
            .Concat(task.Collaborators.Select(item => item.UserId))
            .Distinct();

    private static Result<T> Fail<T>(string code, string message) => Result<T>.Failure($"{code}|{message}");
}
