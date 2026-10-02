using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Planning;
using Coglatas.Application.Projects;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Infrastructure.Persistence;

public sealed class PlanningRepository(AppDbContext dbContext) : IPlanningRepository
{
    private const int MaximumGanttDependencies = 2_000;

    public async Task<GanttSnapshotReadResult> GetGanttAsync(
        Guid projectId,
        Guid actorUserId,
        bool canManageProject,
        bool canContributeToOwnedTasks,
        string workspaceTimeZone,
        int maximumItems,
        CancellationToken cancellationToken = default)
    {
        var project = await dbContext.Projects
            .AsNoTracking()
            .Where(project =>
                project.Id == projectId &&
                !project.DeletedAt.HasValue &&
                project.Status != ProjectStatus.Archived &&
                project.Status != ProjectStatus.Deleted)
            .Select(project => new
            {
                project.Id,
                project.Name,
                project.VersionNo
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (project is null)
        {
            return new GanttSnapshotReadResult(null, 0, false, false);
        }

        var taskCount = await dbContext.TaskItems
            .AsNoTracking()
            .LongCountAsync(task =>
                task.ProjectId == projectId &&
                task.Kind == WorkItemKind.Task &&
                !task.DeletedAt.HasValue,
                cancellationToken);
        var milestoneCount = await dbContext.Milestones
            .AsNoTracking()
            .LongCountAsync(milestone =>
                milestone.ProjectId == projectId &&
                !milestone.DeletedAt.HasValue,
                cancellationToken);
        var totalLong = taskCount + milestoneCount;
        var totalItems = totalLong > int.MaxValue ? int.MaxValue : (int)totalLong;
        if (totalLong > maximumItems)
        {
            return new GanttSnapshotReadResult(null, totalItems, true, false);
        }

        var workflowVersion = await dbContext.TaskWorkflowDefinitions
            .AsNoTracking()
            .Where(definition => definition.ProjectId == projectId)
            .OrderBy(definition => definition.Id)
            .Select(definition => (long?)definition.VersionNo)
            .FirstOrDefaultAsync(cancellationToken) ?? 1L;

        // The count gate above bounds this entity read. Reference Includes do not
        // expand the graph and keep Stage/primary-assignee projection set based.
        var tasks = await dbContext.TaskItems
            .AsNoTracking()
            .Include(task => task.WorkflowStage)
            .Include(task => task.PrimaryAssigneeUser)
            .Where(task =>
                task.ProjectId == projectId &&
                task.Kind == WorkItemKind.Task &&
                !task.DeletedAt.HasValue)
            .OrderBy(task => task.SortKey)
            .ThenBy(task => task.Id)
            .Take(maximumItems + 1)
            .ToListAsync(cancellationToken);

        var milestones = await dbContext.Milestones
            .AsNoTracking()
            .Where(milestone => milestone.ProjectId == projectId && !milestone.DeletedAt.HasValue)
            .OrderBy(milestone => milestone.SortOrder)
            .ThenBy(milestone => milestone.Id)
            .Take(maximumItems + 1)
            .ToListAsync(cancellationToken);

        // The pre-read count is only an early gate. Under PostgreSQL's default
        // READ COMMITTED isolation, inserts can become visible between statements,
        // so enforce the combined bound again against the rows actually projected.
        // This also keeps TotalItems aligned when rows are deleted between the
        // initial count and the bounded reads.
        totalItems = tasks.Count + milestones.Count;
        if (totalItems > maximumItems)
        {
            return new GanttSnapshotReadResult(null, totalItems, true, false);
        }

        var taskIds = tasks.Select(task => task.Id).ToArray();
        var dependencies = await dbContext.TaskDependencies
            .AsNoTracking()
            .Where(dependency =>
                dependency.ProjectId == projectId &&
                Enumerable.Contains(taskIds, dependency.PredecessorTaskItemId) &&
                Enumerable.Contains(taskIds, dependency.SuccessorTaskItemId))
            .OrderBy(dependency => dependency.PredecessorTaskItemId)
            .ThenBy(dependency => dependency.SuccessorTaskItemId)
            .ThenBy(dependency => dependency.Id)
            .Take(MaximumGanttDependencies + 1)
            .ToListAsync(cancellationToken);

        if (dependencies.Count > MaximumGanttDependencies)
        {
            return new GanttSnapshotReadResult(null, totalItems, false, true);
        }

        var milestoneIds = milestones.Select(milestone => milestone.Id).ToHashSet();
        var visibleTaskIds = taskIds.ToHashSet();
        var taskWarnings = tasks.ToDictionary(task => task.Id, _ => new List<GanttWarningResponse>());
        var computedTasks = tasks.ToDictionary(
            task => task.Id,
            task =>
            {
                var derived = ParentTaskDerivedValuesCalculator.Calculate(task, tasks, GanttCategoryOf);
                var category = GanttCategoryOf(task);
                var mayEditOwnTask =
                    canContributeToOwnedTasks &&
                    (task.CreatedByUserId == actorUserId || task.PrimaryAssigneeUserId == actorUserId);
                var mayEditLeaf = !derived.IsDerived && (canManageProject || mayEditOwnTask);
                var mayEditProgress =
                    mayEditLeaf &&
                    category is not TaskStageCategory.Done and not TaskStageCategory.Cancelled;
                var permissions = new GanttPermissionsResponse(
                    mayEditLeaf,
                    mayEditProgress,
                    canManageProject,
                    mayEditLeaf,
                    true);
                var warnings = taskWarnings[task.Id];
                if (derived.IsDerived)
                {
                    warnings.Add(Warning(
                        "PARENT_DERIVED",
                        "Parent schedule and progress are derived from direct children and cannot be edited directly.",
                        GanttWarningSeverity.Info,
                        "Task",
                        task.Id,
                        "plannedStartDate"));
                }

                if (!derived.PlannedStartDate.HasValue && !derived.PlannedEndDate.HasValue)
                {
                    warnings.Add(Warning(
                        "UNSCHEDULED",
                        "This Task has no planned dates and is listed as unscheduled.",
                        GanttWarningSeverity.Info,
                        "Task",
                        task.Id,
                        "plannedStartDate"));
                }

                if (!derived.PlannedEndDate.HasValue &&
                    category is TaskStageCategory.InProgress or TaskStageCategory.Review)
                {
                    warnings.Add(Warning(
                        "MISSING_ACTIVE_PLANNED_END",
                        "Active work has no planned end date.",
                        GanttWarningSeverity.Warning,
                        "Task",
                        task.Id,
                        "plannedEndDate"));
                }

                return new GanttComputedTask(task, derived, category, permissions, warnings);
            });

        var dependencyResponses = new List<GanttDependencyResponse>(dependencies.Count);
        foreach (var dependency in dependencies)
        {
            var warnings = new List<GanttWarningResponse>();
            if (dependency.DependencyType != TaskDependencyType.FinishToStart)
            {
                warnings.Add(Warning(
                    "LEGACY_DEPENDENCY_TYPE",
                    "This legacy dependency type is read-only; new authoring supports Finish-to-Start only.",
                    GanttWarningSeverity.Warning,
                    "Dependency",
                    dependency.Id,
                    "type"));
            }

            var predecessor = computedTasks[dependency.PredecessorTaskItemId];
            var successor = computedTasks[dependency.SuccessorTaskItemId];
            if (dependency.DependencyType == TaskDependencyType.FinishToStart &&
                predecessor.Derived.PlannedEndDate.HasValue &&
                successor.Derived.PlannedStartDate.HasValue &&
                predecessor.Derived.PlannedEndDate.Value > successor.Derived.PlannedStartDate.Value)
            {
                var violation = Warning(
                    "DEPENDENCY_VIOLATION",
                    "The predecessor is planned to finish after the successor starts. No dates were changed automatically.",
                    GanttWarningSeverity.Warning,
                    "Dependency",
                    dependency.Id,
                    "plannedStartDate");
                warnings.Add(violation);
                taskWarnings[successor.Task.Id].Add(violation with
                {
                    TargetType = "Task",
                    TargetId = successor.Task.Id
                });
            }

            dependencyResponses.Add(new GanttDependencyResponse(
                dependency.Id,
                dependency.PredecessorTaskItemId,
                dependency.SuccessorTaskItemId,
                dependency.DependencyType,
                canManageProject && dependency.DependencyType == TaskDependencyType.FinishToStart,
                successor.Task.VersionNo,
                OrderedWarnings(warnings)));
        }

        var itemResponses = computedTasks.Values
            .OrderBy(item => item.Task.SortKey)
            .ThenBy(item => item.Task.Id)
            .Select(item => ToGanttTask(item, visibleTaskIds, milestoneIds))
            .ToList();
        var scheduledItems = itemResponses
            .Where(item => item.PlannedStartDate.HasValue || item.PlannedEndDate.HasValue)
            .ToList();
        var unscheduledItems = itemResponses
            .Where(item => !item.PlannedStartDate.HasValue && !item.PlannedEndDate.HasValue)
            .ToList();

        var milestoneResponses = new List<GanttItemResponse>(milestones.Count);
        foreach (var milestone in milestones)
        {
            var warnings = new List<GanttWarningResponse>();
            if (!milestone.DueDate.HasValue)
            {
                warnings.Add(Warning(
                    "MILESTONE_DATE_REQUIRED",
                    "This legacy Milestone has no date. Set a date before updating its progress.",
                    GanttWarningSeverity.Warning,
                    "Milestone",
                    milestone.Id,
                    "milestoneDate"));
            }

            var category = milestone.Status switch
            {
                MilestoneStatus.InProgress => TaskStageCategory.InProgress,
                MilestoneStatus.Completed => TaskStageCategory.Done,
                MilestoneStatus.Cancelled => TaskStageCategory.Cancelled,
                _ => TaskStageCategory.Todo
            };
            var permissions = new GanttPermissionsResponse(
                canManageProject,
                canManageProject &&
                milestone.DueDate.HasValue &&
                milestone.Status != MilestoneStatus.Cancelled,
                false,
                false,
                false);
            milestoneResponses.Add(new GanttItemResponse(
                milestone.Id,
                WorkItemKind.Milestone,
                null,
                null,
                milestone.Name,
                null,
                null,
                milestone.DueDate,
                milestone.Status == MilestoneStatus.Completed ? 100 : 0,
                false,
                null,
                milestone.Status.ToString(),
                category,
                TaskPriority.Medium,
                false,
                null,
                milestone.VersionNo,
                permissions,
                OrderedWarnings(warnings)));
        }

        var allWarnings = itemResponses.SelectMany(item => item.Warnings)
            .Concat(milestoneResponses.SelectMany(item => item.Warnings))
            .Concat(dependencyResponses.SelectMany(dependency => dependency.Warnings))
            .Distinct()
            .OrderBy(warning => warning.Code, StringComparer.Ordinal)
            .ThenBy(warning => warning.TargetId)
            .ToList();
        var topPermissions = new GanttPermissionsResponse(
            canManageProject || canContributeToOwnedTasks,
            canManageProject || canContributeToOwnedTasks,
            canManageProject,
            canManageProject || canContributeToOwnedTasks,
            true);
        var projectVersion = Math.Max(1L, project.VersionNo);
        var calendar = new GanttCalendarResponse(
            workspaceTimeZone,
            [],
            false,
            [
                "Workspace working-day configuration is not available in the canonical runtime.",
                "Workspace holiday data is unavailable; no holidays were inferred."
            ]);
        var snapshot = new ProjectGanttResponse(
            project.Id,
            project.Name,
            projectVersion,
            workflowVersion,
            null,
            calendar,
            scheduledItems,
            unscheduledItems,
            milestoneResponses,
            dependencyResponses,
            allWarnings,
            topPermissions,
            maximumItems,
            totalItems);
        return new GanttSnapshotReadResult(snapshot, totalItems, false, false);
    }

    private static GanttItemResponse ToGanttTask(
        GanttComputedTask computed,
        IReadOnlySet<Guid> visibleTaskIds,
        IReadOnlySet<Guid> visibleMilestoneIds)
    {
        var task = computed.Task;
        return new GanttItemResponse(
            task.Id,
            WorkItemKind.Task,
            task.ParentTaskItemId.HasValue && visibleTaskIds.Contains(task.ParentTaskItemId.Value)
                ? task.ParentTaskItemId
                : null,
            task.MilestoneId.HasValue && visibleMilestoneIds.Contains(task.MilestoneId.Value)
                ? task.MilestoneId
                : null,
            task.Title,
            computed.Derived.PlannedStartDate,
            computed.Derived.PlannedEndDate,
            null,
            computed.Derived.ProgressPercent,
            computed.Derived.IsDerived,
            task.WorkflowStageId,
            task.WorkflowStage?.Name ?? computed.Category.ToString(),
            task.WorkflowStage?.InternalCategory ?? computed.Category,
            task.Priority,
            task.IsBlocked,
            task.PrimaryAssigneeUserId.HasValue
                ? new GanttPersonSummary(
                    task.PrimaryAssigneeUserId.Value,
                    task.PrimaryAssigneeUser?.DisplayName ?? "Unknown")
                : null,
            task.VersionNo,
            computed.Permissions,
            OrderedWarnings(computed.Warnings));
    }

    private static GanttWarningResponse Warning(
        string code,
        string message,
        GanttWarningSeverity severity,
        string targetType,
        Guid targetId,
        string? field) =>
        new(code, message, severity, targetType, targetId, field, false);

    private static IReadOnlyList<GanttWarningResponse> OrderedWarnings(IEnumerable<GanttWarningResponse> warnings) =>
        warnings
            .OrderBy(warning => warning.Code, StringComparer.Ordinal)
            .ThenBy(warning => warning.TargetId)
            .ToList();

    private static TaskStageCategory GanttCategoryOf(TaskItem task) =>
        task.WorkflowStage?.InternalCategory ?? task.Status switch
        {
            TaskItemStatus.InProgress => TaskStageCategory.InProgress,
            TaskItemStatus.WaitingReview => TaskStageCategory.Review,
            TaskItemStatus.Completed => TaskStageCategory.Done,
            TaskItemStatus.Cancelled => TaskStageCategory.Cancelled,
            _ => TaskStageCategory.Todo
        };

    private sealed record GanttComputedTask(
        TaskItem Task,
        ParentTaskDerivedValues Derived,
        TaskStageCategory Category,
        GanttPermissionsResponse Permissions,
        List<GanttWarningResponse> Warnings);

    public async Task<ProjectDashboardResponse?> GetDashboardAsync(Guid projectId, DateOnly today, CancellationToken cancellationToken = default)
    {
        var project = await dbContext.Projects
            .AsNoTracking()
            .Where(project => project.Id == projectId && !project.DeletedAt.HasValue)
            .Select(project => new { project.Id, project.Name })
            .FirstOrDefaultAsync(cancellationToken);

        if (project is null)
        {
            return null;
        }

        var taskCounts = await dbContext.TaskItems
            .AsNoTracking()
            .Where(task => task.ProjectId == projectId && !task.DeletedAt.HasValue)
            .GroupBy(task => task.Status)
            .Select(group => new TaskStatusCountResponse(group.Key, group.Count()))
            .ToListAsync(cancellationToken);

        var taskRows = await dbContext.TaskItems
            .AsNoTracking()
            .Where(task => task.ProjectId == projectId && !task.DeletedAt.HasValue)
            .Select(task => new { task.Id, task.Title, task.DueDate, task.Status, task.Priority, ProjectTitle = project.Name })
            .ToListAsync(cancellationToken);

        var upcoming = taskRows
            .Where(task => task.DueDate.HasValue && task.DueDate.Value >= today && task.Status is not TaskItemStatus.Completed and not TaskItemStatus.Cancelled)
            .OrderBy(task => task.DueDate)
            .Take(10)
            .Select(task => new MyTaskListItemResponse(task.Id, projectId, task.ProjectTitle, task.Title, task.DueDate, task.Status, task.Priority, IsOverdue(task.DueDate, task.Status, today)))
            .ToList();

        var activity = await dbContext.ActivityLogs
            .AsNoTracking()
            .Where(log => log.ProjectId == projectId)
            .OrderByDescending(log => log.OccurredAt)
            .Take(10)
            .Select(log => new ActivityLogSummaryResponse(log.Id, log.ActivityType, log.Body, log.OccurredAt, log.AuthorUserId, log.AuthorUser!.DisplayName))
            .ToListAsync(cancellationToken);

        var comments = await dbContext.Comments
            .AsNoTracking()
            .Where(comment => !comment.DeletedAt.HasValue &&
                (comment.TargetType == CommentTargetType.Project && comment.TargetId == projectId ||
                 comment.TargetType == CommentTargetType.TaskItem && dbContext.TaskItems.Any(task => task.Id == comment.TargetId && task.ProjectId == projectId) ||
                 comment.TargetType == CommentTargetType.Artifact && dbContext.Artifacts.Any(artifact => artifact.Id == comment.TargetId && artifact.ProjectId == projectId)))
            .OrderByDescending(comment => comment.CreatedAt)
            .Take(10)
            .Select(comment => new CommentSummaryResponse(comment.Id, comment.TargetType, comment.TargetId, comment.Body, comment.CreatedAt, comment.AuthorUserId, comment.AuthorUser!.DisplayName))
            .ToListAsync(cancellationToken);

        var artifacts = await dbContext.Artifacts
            .AsNoTracking()
            .Where(artifact => artifact.ProjectId == projectId && !artifact.DeletedAt.HasValue)
            .OrderByDescending(artifact => artifact.CreatedAt)
            .Take(5)
            .Select(artifact => new DashboardArtifactResponse(artifact.Id, artifact.Name, artifact.ArtifactType, artifact.Status, artifact.CurrentVersionId, artifact.CreatedAt))
            .ToListAsync(cancellationToken);

        var members = await dbContext.ProjectMembers
            .AsNoTracking()
            .Where(member => member.ProjectId == projectId)
            .OrderBy(member => member.User!.DisplayName)
            .Select(member => new ProjectMemberSummaryResponse(member.UserId, member.User!.DisplayName, member.Role))
            .ToListAsync(cancellationToken);

        return new ProjectDashboardResponse(project.Id, project.Name, taskCounts, taskRows.Count(task => IsOverdue(task.DueDate, task.Status, today)), upcoming, activity, comments, artifacts, members);
    }

    public async Task<MyTasksProjectionPage> ListMyTasksAsync(Guid userId, MyTasksQuery query, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var source = ApplyMyTasksFilters(VisibleTasksFor(userId), userId, query, now, includeView: true);
        var page = query.SafePage;
        var pageSize = query.SafePageSize;
        var total = await source.CountAsync(cancellationToken);
        var rows = await source
            .OrderByDescending(task => task.IsBlocked)
            // Priority is persisted as text. Use an explicit rank so PostgreSQL
            // does not apply lexicographic ordering to the enum converter.
            .ThenByDescending(task =>
                task.Priority == TaskPriority.Critical ? 3 :
                task.Priority == TaskPriority.High ? 2 :
                task.Priority == TaskPriority.Medium ? 1 : 0)
            .ThenBy(task => task.DeadlineAt ?? DateTimeOffset.MaxValue)
            .ThenBy(task => task.PlannedEndDate ?? DateOnly.MaxValue)
            .ThenByDescending(task => task.UpdatedAt ?? task.CreatedAt)
            .ThenBy(task => task.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(task => new MyTaskRow(
                task.Id,
                userId,
                task.TenantId,
                task.WorkspaceId,
                task.ProjectId,
                task.Project!.Workspace!.Name,
                task.Project.Name,
                task.Project.OwnerUserId,
                task.Kind,
                task.ParentTaskItemId,
                task.Title,
                task.WorkflowStageId,
                task.WorkflowStage == null ? null : task.WorkflowStage.Name,
                task.WorkflowStage == null ? null : task.WorkflowStage.InternalCategory,
                task.Status,
                task.Priority,
                task.IsBlocked,
                task.PlannedStartDate,
                task.PlannedEndDate,
                task.DeadlineAt,
                task.ProgressPercent,
                task.PrimaryAssigneeUserId,
                task.PrimaryAssigneeUser == null ? null : task.PrimaryAssigneeUser.DisplayName,
                task.TargetGroupId,
                task.TargetGroup == null ? null : task.TargetGroup.Name,
                task.ReviewerUserId,
                task.ReviewerUser == null ? null : task.ReviewerUser.DisplayName,
                task.CreatedByUserId,
                task.VersionNo,
                dbContext.WorkItemCollaborators.Any(collaborator => collaborator.TaskItemId == task.Id && collaborator.UserId == userId),
                dbContext.WorkItemWatchStates.Any(watch =>
                    watch.TaskItemId == task.Id &&
                    watch.UserId == userId &&
                    (watch.IsManualWatch ||
                     (!watch.IsExplicitOptOut && watch.AutomaticSources != WorkItemWatchAutomaticSource.None))),
                dbContext.GroupMembers.Any(member => member.GroupId == task.TargetGroupId && member.UserId == userId),
                dbContext.ProjectMembers.Any(member => member.ProjectId == task.ProjectId && member.UserId == userId),
                dbContext.ProjectMembers.Any(member => member.ProjectId == task.ProjectId && member.UserId == userId && (member.Role == ProjectRole.Owner || member.Role == ProjectRole.Manager)),
                task.ChecklistItems.Count(item => item.IsCompleted),
                task.ChecklistItems.Count,
                task.ChildTaskItems.Any(child => !child.DeletedAt.HasValue),
                task.UpdatedAt ?? task.CreatedAt))
            .ToListAsync(cancellationToken);

        var taskIds = rows.Select(row => row.TaskId).ToArray();
        var labels = await dbContext.WorkItemLabels
            .AsNoTracking()
            .Where(item => Enumerable.Contains(taskIds, item.TaskItemId))
            .OrderBy(item => item.Label!.SortKey)
            .Select(item => new { item.TaskItemId, Label = new MyTaskLabelSummary(item.LabelId, item.Label!.Name) })
            .ToListAsync(cancellationToken);
        var labelsByTask = labels.GroupBy(item => item.TaskItemId).ToDictionary(group => group.Key, group => (IReadOnlyList<MyTaskLabelSummary>)group.Select(item => item.Label).ToList());

        var availableWorkspaceCount = await AccessibleWorkspacesFor(userId).CountAsync(cancellationToken);
        var items = rows.Select(row => ToProjection(row, labelsByTask.GetValueOrDefault(row.TaskId) ?? [], now)).ToList();
        return new MyTasksProjectionPage(items, page, pageSize, total, query.View, query.Scope, query.WorkspaceId, availableWorkspaceCount);
    }

    public async Task<MyTasksCountsResponse> GetMyTaskCountsAsync(Guid userId, MyTasksQuery query, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var visible = VisibleTasksFor(userId);
        var viewValues = Enum.GetValues<MyTasksRelationshipView>();
        var viewQuery = viewValues
            .Select(view => CountForView(visible, userId, query, view, now))
            .Aggregate((combined, next) => combined.Concat(next));
        var returnedViews = await viewQuery.ToListAsync(cancellationToken);
        var views = viewValues
            .Select(view =>
            {
                var count = returnedViews.FirstOrDefault(item => item.View == view);
                return new MyTasksViewCount(view, count?.Count ?? 0);
            })
            .ToList();

        var timeGroupValues = Enum.GetValues<MyTasksTimeGroup>();
        var timeGroupQuery = timeGroupValues
            .Select(group => CountForTimeGroup(visible, userId, query, group, now))
            .Aggregate((combined, next) => combined.Concat(next));
        var returnedTimeGroups = await timeGroupQuery.ToListAsync(cancellationToken);
        var timeGroups = timeGroupValues
            .Select(group =>
            {
                var count = returnedTimeGroups.FirstOrDefault(item => item.TimeGroup == group);
                return new MyTasksTimeGroupCount(group, count?.Count ?? 0);
            })
            .ToList();

        return new MyTasksCountsResponse(
            query.Scope,
            query.WorkspaceId,
            await AccessibleWorkspacesFor(userId).CountAsync(cancellationToken),
            views,
            timeGroups);
    }

    private IQueryable<ViewCountRow> CountForView(
        IQueryable<TaskItem> visible,
        Guid userId,
        MyTasksQuery query,
        MyTasksRelationshipView view,
        DateTimeOffset now) =>
        ApplyMyTasksFilters(visible, userId, query with { View = view }, now, includeView: true)
            .GroupBy(_ => 1)
            .Select(group => new ViewCountRow { View = view, Count = group.Count() });

    private IQueryable<TimeGroupCountRow> CountForTimeGroup(
        IQueryable<TaskItem> visible,
        Guid userId,
        MyTasksQuery query,
        MyTasksTimeGroup timeGroup,
        DateTimeOffset now) =>
        ApplyMyTasksFilters(visible, userId, query with { TimeGroup = timeGroup }, now, includeView: true)
            .GroupBy(_ => 1)
            .Select(group => new TimeGroupCountRow { TimeGroup = timeGroup, Count = group.Count() });

    public async Task<IReadOnlyList<Guid>> ListAccessibleWorkspaceIdsAsync(Guid userId, CancellationToken cancellationToken = default) =>
        await AccessibleWorkspacesFor(userId).Select(workspace => workspace.Id).ToListAsync(cancellationToken);

    public Task<bool> CanViewMyTasksProjectAsync(
        Guid userId,
        Guid projectId,
        CancellationToken cancellationToken = default) =>
        dbContext.VisibleProjectsFor(userId)
            .AnyAsync(project =>
                project.Id == projectId &&
                !project.Workspace!.DeletedAt.HasValue &&
                project.Workspace.Status == WorkspaceStatus.Active &&
                dbContext.WorkspaceMembers.Any(member =>
                    member.WorkspaceId == project.WorkspaceId &&
                    member.UserId == userId &&
                    member.Status == MembershipStatus.Active),
                cancellationToken);

    private IQueryable<Workspace> AccessibleWorkspacesFor(Guid userId) =>
        dbContext.Workspaces
            .AsNoTracking()
            .Where(workspace =>
                !workspace.DeletedAt.HasValue &&
                workspace.Status == WorkspaceStatus.Active &&
                dbContext.WorkspaceMembers.Any(member =>
                    member.WorkspaceId == workspace.Id &&
                    member.UserId == userId &&
                    member.Status == MembershipStatus.Active));

    private IQueryable<TaskItem> VisibleTasksFor(Guid userId)
    {
        var visibleProjectIds = dbContext.VisibleProjectsFor(userId).Select(project => project.Id);
        return dbContext.TaskItems
            .AsNoTracking()
            .Where(task =>
                !task.DeletedAt.HasValue &&
                task.Project != null &&
                task.Project.Workspace != null &&
                visibleProjectIds.Contains(task.ProjectId) &&
                !task.Project.Workspace.DeletedAt.HasValue &&
                task.Project.Workspace.Status == WorkspaceStatus.Active &&
                dbContext.WorkspaceMembers.Any(member =>
                    member.WorkspaceId == task.WorkspaceId &&
                    member.UserId == userId &&
                    member.Status == MembershipStatus.Active));
    }

    private IQueryable<TaskItem> ApplyMyTasksFilters(
        IQueryable<TaskItem> source,
        Guid userId,
        MyTasksQuery query,
        DateTimeOffset now,
        bool includeView)
    {
        if (query.Scope == MyTasksScope.CurrentWorkspace && query.WorkspaceId.HasValue)
        {
            source = source.Where(task => task.WorkspaceId == query.WorkspaceId.Value);
        }

        if (query.ProjectId.HasValue)
        {
            source = source.Where(task => task.ProjectId == query.ProjectId.Value);
        }

        if (query.StageCategory.HasValue)
        {
            source = source.Where(task => task.WorkflowStage != null && task.WorkflowStage.InternalCategory == query.StageCategory.Value);
        }

        if (query.Priority.HasValue)
        {
            source = source.Where(task => task.Priority == query.Priority.Value);
        }

        if (query.Blocked.HasValue)
        {
            source = source.Where(task => task.IsBlocked == query.Blocked.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            source = source.Where(task => task.Title.Contains(search) || task.Project!.Name.Contains(search));
        }

        if (query.Status.HasValue)
        {
            source = source.Where(task => task.Status == query.Status.Value);
        }

        if (query.DueBefore.HasValue)
        {
            source = source.Where(task => task.PlannedEndDate.HasValue && task.PlannedEndDate.Value <= query.DueBefore.Value);
        }

        if (query.OnlyOverdue)
        {
            source = ApplyTimeGroup(source, MyTasksTimeGroup.Overdue, now);
        }

        if (includeView)
        {
            source = ApplyView(source, userId, query.View);
        }

        // The active tabs deliberately exclude completed and cancelled work. Completed is
        // relationship-aware too, rather than being an assignment-only historical list.
        if (query.View == MyTasksRelationshipView.Completed)
        {
            source = source.Where(task =>
                task.WorkflowStage == null
                    ? task.Status == TaskItemStatus.Completed
                    : task.WorkflowStage.InternalCategory == TaskStageCategory.Done);
        }
        else
        {
            source = source.Where(task =>
                task.WorkflowStage == null
                    ? task.Status != TaskItemStatus.Completed && task.Status != TaskItemStatus.Cancelled
                    : task.WorkflowStage.InternalCategory != TaskStageCategory.Done && task.WorkflowStage.InternalCategory != TaskStageCategory.Cancelled);
        }

        return query.TimeGroup.HasValue
            ? ApplyTimeGroup(source, query.TimeGroup.Value, now)
            : source;
    }

    private IQueryable<TaskItem> ApplyView(IQueryable<TaskItem> source, Guid userId, MyTasksRelationshipView view) => view switch
    {
        MyTasksRelationshipView.Assigned => source.Where(task => task.PrimaryAssigneeUserId == userId),
        MyTasksRelationshipView.Participating => source.Where(task => dbContext.WorkItemCollaborators.Any(item => item.TaskItemId == task.Id && item.UserId == userId)),
        MyTasksRelationshipView.Reviews => source.Where(task => task.ReviewerUserId == userId),
        MyTasksRelationshipView.Created => source.Where(task => task.CreatedByUserId == userId),
        MyTasksRelationshipView.Watching => source.Where(task => dbContext.WorkItemWatchStates.Any(item =>
            item.TaskItemId == task.Id &&
            item.UserId == userId &&
            (item.IsManualWatch ||
             (!item.IsExplicitOptOut && item.AutomaticSources != WorkItemWatchAutomaticSource.None)))),
        MyTasksRelationshipView.TeamQueue => source.Where(task =>
            task.PrimaryAssigneeUserId == null &&
            task.TargetGroupId.HasValue &&
            dbContext.GroupMembers.Any(member => member.GroupId == task.TargetGroupId && member.UserId == userId) &&
            (task.WorkflowStage == null
                ? task.Status == TaskItemStatus.NotStarted || task.Status == TaskItemStatus.Blocked
                : task.WorkflowStage.InternalCategory == TaskStageCategory.Backlog ||
                  task.WorkflowStage.InternalCategory == TaskStageCategory.Todo)),
        MyTasksRelationshipView.Completed => source.Where(task =>
            task.PrimaryAssigneeUserId == userId ||
            task.CreatedByUserId == userId ||
            task.ReviewerUserId == userId ||
            dbContext.WorkItemCollaborators.Any(item => item.TaskItemId == task.Id && item.UserId == userId) ||
            dbContext.WorkItemWatchStates.Any(item =>
                item.TaskItemId == task.Id &&
                item.UserId == userId &&
                (item.IsManualWatch ||
                 (!item.IsExplicitOptOut && item.AutomaticSources != WorkItemWatchAutomaticSource.None)))),
        _ => source
    };

    private static IQueryable<TaskItem> ApplyTimeGroup(IQueryable<TaskItem> source, MyTasksTimeGroup group, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var tomorrow = today.AddDays(1);
        var nextWeek = today.AddDays(8);
        var todayStart = new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        var tomorrowStart = todayStart.AddDays(1);
        var nextWeekStart = todayStart.AddDays(8);

        return group switch
        {
            MyTasksTimeGroup.Overdue => source.Where(task =>
                task.DeadlineAt.HasValue ? task.DeadlineAt.Value < now : task.PlannedEndDate.HasValue && task.PlannedEndDate.Value < today),
            MyTasksTimeGroup.Today => source.Where(task =>
                task.DeadlineAt.HasValue
                    ? task.DeadlineAt.Value >= now && task.DeadlineAt.Value < tomorrowStart
                    : task.PlannedEndDate.HasValue && task.PlannedEndDate.Value == today),
            MyTasksTimeGroup.Next7Days => source.Where(task =>
                task.DeadlineAt.HasValue
                    ? task.DeadlineAt.Value >= tomorrowStart && task.DeadlineAt.Value < nextWeekStart
                    : task.PlannedEndDate.HasValue && task.PlannedEndDate.Value >= tomorrow && task.PlannedEndDate.Value < nextWeek),
            MyTasksTimeGroup.Later => source.Where(task =>
                task.DeadlineAt.HasValue
                    ? task.DeadlineAt.Value >= nextWeekStart
                    : task.PlannedEndDate.HasValue && task.PlannedEndDate.Value >= nextWeek),
            MyTasksTimeGroup.NoDeadline => source.Where(task => !task.DeadlineAt.HasValue && !task.PlannedEndDate.HasValue),
            _ => source
        };
    }

    private static MyTaskProjectionResponse ToProjection(MyTaskRow row, IReadOnlyList<MyTaskLabelSummary> labels, DateTimeOffset now)
    {
        var category = row.StageCategory ?? MapLegacyStatus(row.Status);
        var isDone = category == TaskStageCategory.Done;
        var timeGroup = ResolveTimeGroup(row.DeadlineAt, row.PlannedEndDate, now);
        var isTeamQueueEligible = row.TargetGroupId.HasValue && row.PrimaryAssigneeUserId is null && row.IsCurrentGroupMember &&
            category is TaskStageCategory.Backlog or TaskStageCategory.Todo;
        var canEdit = row.ProjectOwnerUserId == row.CurrentUserId || row.CreatedByUserId == row.CurrentUserId || row.PrimaryAssigneeUserId == row.CurrentUserId || row.IsProjectManager;
        var canEditDerivedFields = canEdit && !row.HasChildren;
        var permissions = new MyTaskQuickEditPermissions(
            canEdit,
            canEditDerivedFields,
            canEdit,
            canEditDerivedFields,
            false,
            canEdit,
            canEdit,
            canEdit,
            isTeamQueueEligible && row.IsProjectMember);
        return new MyTaskProjectionResponse(
            row.TaskId, row.TenantId, row.WorkspaceId, row.WorkspaceTitle, row.ProjectId, row.ProjectTitle, row.Kind, row.ParentTaskId,
            row.Title, row.WorkflowStageId, row.WorkflowStageName ?? category.ToString(), category, row.Priority, row.IsBlocked,
            row.PlannedStartDate, row.PlannedEndDate, row.DeadlineAt, row.ProgressPercent, row.HasChildren,
            row.PrimaryAssigneeUserId.HasValue ? new MyTaskPersonSummary(row.PrimaryAssigneeUserId.Value, row.PrimaryAssigneeName ?? "Unknown") : null,
            row.TargetGroupId.HasValue ? new MyTaskGroupSummary(row.TargetGroupId.Value, row.TargetGroupName ?? "Group") : null,
            row.ReviewerUserId.HasValue ? new MyTaskPersonSummary(row.ReviewerUserId.Value, row.ReviewerName ?? "Unknown") : null,
            labels, row.ChecklistCompletedCount, row.ChecklistTotalCount,
            new MyTaskRelationshipFlags(row.PrimaryAssigneeUserId == row.CurrentUserId, row.IsCollaborator, row.ReviewerUserId == row.CurrentUserId, row.CreatedByUserId == row.CurrentUserId, row.IsWatching, isTeamQueueEligible),
            timeGroup, timeGroup == MyTasksTimeGroup.Overdue && !isDone, row.Version, permissions, []);
    }

    private static TaskStageCategory MapLegacyStatus(TaskItemStatus status) => status switch
    {
        TaskItemStatus.InProgress => TaskStageCategory.InProgress,
        TaskItemStatus.WaitingReview => TaskStageCategory.Review,
        TaskItemStatus.Completed => TaskStageCategory.Done,
        TaskItemStatus.Cancelled => TaskStageCategory.Cancelled,
        _ => TaskStageCategory.Todo
    };

    private static MyTasksTimeGroup ResolveTimeGroup(DateTimeOffset? deadlineAt, DateOnly? plannedEndDate, DateTimeOffset now)
    {
        if (deadlineAt.HasValue)
        {
            var today = DateOnly.FromDateTime(now.UtcDateTime);
            if (deadlineAt.Value < now) return MyTasksTimeGroup.Overdue;
            var tomorrowStart = new DateTimeOffset(today.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
            if (deadlineAt.Value < tomorrowStart) return MyTasksTimeGroup.Today;
            var nextWeekStart = new DateTimeOffset(today.AddDays(8).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
            return deadlineAt.Value < nextWeekStart ? MyTasksTimeGroup.Next7Days : MyTasksTimeGroup.Later;
        }
        if (!plannedEndDate.HasValue) return MyTasksTimeGroup.NoDeadline;
        var day = DateOnly.FromDateTime(now.UtcDateTime);
        if (plannedEndDate.Value < day) return MyTasksTimeGroup.Overdue;
        if (plannedEndDate.Value == day) return MyTasksTimeGroup.Today;
        return plannedEndDate.Value < day.AddDays(8) ? MyTasksTimeGroup.Next7Days : MyTasksTimeGroup.Later;
    }

    private sealed class ViewCountRow
    {
        public MyTasksRelationshipView View { get; init; }
        public int Count { get; init; }
    }

    private sealed class TimeGroupCountRow
    {
        public MyTasksTimeGroup TimeGroup { get; init; }
        public int Count { get; init; }
    }

    private sealed record MyTaskRow(
        Guid TaskId, Guid CurrentUserId, Guid TenantId, Guid WorkspaceId, Guid ProjectId, string WorkspaceTitle, string ProjectTitle, Guid ProjectOwnerUserId,
        WorkItemKind Kind, Guid? ParentTaskId, string Title, Guid? WorkflowStageId, string? WorkflowStageName, TaskStageCategory? StageCategory,
        TaskItemStatus Status, TaskPriority Priority, bool IsBlocked, DateOnly? PlannedStartDate, DateOnly? PlannedEndDate, DateTimeOffset? DeadlineAt,
        int ProgressPercent, Guid? PrimaryAssigneeUserId, string? PrimaryAssigneeName, Guid? TargetGroupId, string? TargetGroupName,
        Guid? ReviewerUserId, string? ReviewerName, Guid CreatedByUserId, long Version, bool IsCollaborator, bool IsWatching,
        bool IsCurrentGroupMember, bool IsProjectMember, bool IsProjectManager, int ChecklistCompletedCount, int ChecklistTotalCount, bool HasChildren, DateTimeOffset UpdatedAt);

    public async Task<ProjectWorkloadResponse?> GetWorkloadAsync(Guid projectId, DateOnly today, CancellationToken cancellationToken = default)
    {
        var exists = await dbContext.Projects.AsNoTracking().AnyAsync(project => project.Id == projectId && !project.DeletedAt.HasValue, cancellationToken);
        if (!exists)
        {
            return null;
        }

        var members = await dbContext.ProjectMembers
            .AsNoTracking()
            .Where(member => member.ProjectId == projectId)
            .Select(member => new { member.UserId, member.Role, member.User!.DisplayName })
            .ToListAsync(cancellationToken);

        var assignments = await dbContext.TaskAssignments
            .AsNoTracking()
            .Where(assignment => assignment.TaskItem!.ProjectId == projectId && !assignment.TaskItem.DeletedAt.HasValue)
            .Select(assignment => new
            {
                assignment.UserId,
                assignment.EstimatedHours,
                assignment.ActualHours,
                assignment.TaskItem!.DueDate,
                assignment.TaskItem.Status
            })
            .ToListAsync(cancellationToken);

        var response = members.Select(member =>
        {
            var assigned = assignments.Where(assignment => assignment.UserId == member.UserId).ToList();
            return new ProjectMemberWorkloadResponse(
                member.UserId,
                member.DisplayName,
                member.Role,
                assigned.Count,
                assigned.Count(assignment => IsOverdue(assignment.DueDate, assignment.Status, today)),
                assigned.Sum(assignment => assignment.EstimatedHours ?? 0),
                assigned.Sum(assignment => assignment.ActualHours ?? 0));
        }).ToList();

        return new ProjectWorkloadResponse(projectId, response);
    }

    private static bool IsOverdue(DateOnly? dueDate, TaskItemStatus status, DateOnly today)
    {
        return dueDate.HasValue &&
            dueDate.Value < today &&
            status is not TaskItemStatus.Completed and not TaskItemStatus.Cancelled;
    }
}
