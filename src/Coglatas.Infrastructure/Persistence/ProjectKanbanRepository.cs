using Coglatas.Application.Common.Interfaces;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Infrastructure.Persistence;

/// <summary>
/// Purpose-built, bounded Project-board queries. The read path uses a fixed
/// number of projected SQL statements and never materializes Task descriptions,
/// blocked reasons, comments, files, or other private Task subresources.
/// </summary>
public sealed class ProjectKanbanRepository(AppDbContext dbContext) : IProjectKanbanRepository
{
    public async Task<ProjectKanbanReadModel?> ReadAsync(
        Guid projectId,
        DateTimeOffset completedCutoffUtc,
        bool includeOlderCompleted,
        Guid? primaryAssigneeUserId,
        Guid? targetGroupId,
        TaskPriority? priority,
        Guid? parentTaskId,
        int take,
        CancellationToken cancellationToken = default)
    {
        var definition = await dbContext.TaskWorkflowDefinitions
            .AsNoTracking()
            .Where(item => item.ProjectId == projectId)
            .Select(item => new ProjectKanbanDefinitionReadModel(
                item.Id,
                item.TenantId,
                item.WorkspaceId,
                item.ProjectId,
                item.ReviewEnforcementEnabled,
                item.KanbanDefaultSwimlane,
                item.VersionNo))
            .SingleOrDefaultAsync(cancellationToken);
        if (definition is null)
            return null;

        var stages = await dbContext.TaskWorkflowStages
            .AsNoTracking()
            .Where(stage => stage.ProjectId == projectId && stage.DefinitionId == definition.Id)
            .OrderBy(stage => stage.SortKey)
            .ThenBy(stage => stage.Id)
            .Select(stage => new ProjectKanbanStageReadModel(
                stage.Id,
                stage.Name,
                stage.InternalCategory,
                stage.SortKey,
                stage.WipWarningLimit))
            .ToListAsync(cancellationToken);

        var boardQuery = dbContext.TaskItems
            .AsNoTracking()
            .Where(task =>
                task.ProjectId == projectId &&
                !task.DeletedAt.HasValue &&
                task.WorkflowStageId.HasValue &&
                (includeOlderCompleted ||
                 task.WorkflowStage!.InternalCategory != TaskStageCategory.Done ||
                 !task.CompletedAt.HasValue ||
                 task.CompletedAt >= completedCutoffUtc));

        var counts = await boardQuery
            .GroupBy(task => task.WorkflowStageId!.Value)
            .Select(group => new { StageId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.StageId, item => item.Count, cancellationToken);

        var query = boardQuery;
        if (primaryAssigneeUserId.HasValue)
            query = query.Where(task => task.PrimaryAssigneeUserId == primaryAssigneeUserId);
        if (targetGroupId.HasValue)
            query = query.Where(task => task.TargetGroupId == targetGroupId);
        if (priority.HasValue)
            query = query.Where(task => task.Priority == priority);
        if (parentTaskId.HasValue)
            query = query.Where(task => task.Id == parentTaskId || task.ParentTaskItemId == parentTaskId);

        var totalCount = await query.CountAsync(cancellationToken);
        var tasks = await query
            .OrderBy(task => task.WorkflowStage!.SortKey)
            .ThenBy(task => task.SortKey)
            .ThenBy(task => task.Id)
            .Take(take)
            .Select(task => new ProjectKanbanTaskReadModel(
                task.Id,
                task.ProjectId,
                task.WorkflowStageId!.Value,
                task.Title,
                task.SortKey,
                task.ParentTaskItem != null && !task.ParentTaskItem.DeletedAt.HasValue ? task.ParentTaskItemId : null,
                task.ParentTaskItem != null && !task.ParentTaskItem.DeletedAt.HasValue ? task.ParentTaskItem.Title : null,
                0,
                0,
                0,
                task.ProgressPercent,
                task.PlannedStartDate ?? task.StartDate,
                task.PlannedEndDate ?? task.DueDate,
                task.CreatedByUserId,
                task.PrimaryAssigneeUserId,
                task.PrimaryAssigneeUser == null ? null : task.PrimaryAssigneeUser.DisplayName,
                task.TargetGroupId,
                task.TargetGroup == null ? null : task.TargetGroup.Name,
                task.ReviewerUserId,
                task.ReviewStatus,
                task.Priority,
                task.IsBlocked,
                task.VersionNo))
            .ToListAsync(cancellationToken);

        // Parent progress and planning dates are canonical read-time values,
        // never cached parent-row values. Aggregate all direct children for the
        // bounded card IDs in one SQL statement so the board follows the same
        // derivation rules as Task List/Detail without a per-card query.
        var cardIds = tasks.Select(task => task.Id).ToArray();
        var childAggregates = await dbContext.TaskItems
            .AsNoTracking()
            .Where(child =>
                child.ProjectId == projectId &&
                child.ParentTaskItemId.HasValue &&
                Enumerable.Contains(cardIds, child.ParentTaskItemId.Value) &&
                !child.DeletedAt.HasValue)
            .GroupBy(child => child.ParentTaskItemId!.Value)
            .Select(group => new
            {
                ParentId = group.Key,
                ChildCount = group.Count(),
                CompletedChildCount = group.Count(child =>
                    child.WorkflowStage != null
                        ? child.WorkflowStage.InternalCategory == TaskStageCategory.Done
                        : child.Status == TaskItemStatus.Completed),
                IncompleteChildCount = group.Count(child =>
                    child.WorkflowStage != null
                        ? child.WorkflowStage.InternalCategory != TaskStageCategory.Done &&
                          child.WorkflowStage.InternalCategory != TaskStageCategory.Cancelled
                        : child.Status != TaskItemStatus.Completed &&
                          child.Status != TaskItemStatus.Cancelled),
                PlannedStartDate = group.Min(child => child.PlannedStartDate ?? child.StartDate),
                PlannedEndDate = group.Max(child => child.PlannedEndDate ?? child.DueDate),
                ProgressChildCount = group.Count(child =>
                    child.WorkflowStage != null
                        ? child.WorkflowStage.InternalCategory != TaskStageCategory.Cancelled
                        : child.Status != TaskItemStatus.Cancelled),
                PositiveEstimateCount = group.Count(child =>
                    (child.WorkflowStage != null
                        ? child.WorkflowStage.InternalCategory != TaskStageCategory.Cancelled
                        : child.Status != TaskItemStatus.Cancelled) &&
                    child.EstimatedEffortMinutes > 0),
                ProgressTotal = group
                    .Where(child => child.WorkflowStage != null
                        ? child.WorkflowStage.InternalCategory != TaskStageCategory.Cancelled
                        : child.Status != TaskItemStatus.Cancelled)
                    .Sum(child => (decimal?)child.ProgressPercent),
                EstimateTotal = group
                    .Where(child => child.WorkflowStage != null
                        ? child.WorkflowStage.InternalCategory != TaskStageCategory.Cancelled
                        : child.Status != TaskItemStatus.Cancelled)
                    .Sum(child => (decimal?)child.EstimatedEffortMinutes),
                WeightedProgressTotal = group
                    .Where(child => child.WorkflowStage != null
                        ? child.WorkflowStage.InternalCategory != TaskStageCategory.Cancelled
                        : child.Status != TaskItemStatus.Cancelled)
                    .Sum(child => child.ProgressPercent * (decimal?)child.EstimatedEffortMinutes)
            })
            .ToListAsync(cancellationToken);

        var aggregatesByParent = childAggregates.ToDictionary(item => item.ParentId);
        tasks = tasks.Select(task =>
        {
            if (!aggregatesByParent.TryGetValue(task.Id, out var aggregate))
                return task;

            var progress = 0;
            if (aggregate.ProgressChildCount > 0)
            {
                var useWeighted = aggregate.PositiveEstimateCount == aggregate.ProgressChildCount &&
                    aggregate.EstimateTotal is > 0;
                var value = useWeighted
                    ? aggregate.WeightedProgressTotal.GetValueOrDefault() / aggregate.EstimateTotal!.Value
                    : aggregate.ProgressTotal.GetValueOrDefault() / aggregate.ProgressChildCount;
                progress = Math.Clamp(
                    (int)Math.Round(value, MidpointRounding.AwayFromZero),
                    0,
                    100);
            }

            return task with
            {
                ChildCount = aggregate.ChildCount,
                CompletedChildCount = aggregate.CompletedChildCount,
                IncompleteChildCount = aggregate.IncompleteChildCount,
                ProgressPercent = progress,
                PlannedStartDate = aggregate.PlannedStartDate,
                PlannedEndDate = aggregate.PlannedEndDate
            };
        }).ToList();

        return new ProjectKanbanReadModel(definition, stages, tasks, counts, totalCount);
    }

    public Task<TaskWorkflowDefinition?> GetDefinitionForUpdateAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        dbContext.TaskWorkflowDefinitions
            .Include(definition => definition.Stages)
            .SingleOrDefaultAsync(definition => definition.ProjectId == projectId, cancellationToken);

    public async Task<IReadOnlyList<TaskItem>> ListStageTasksForUpdateAsync(
        Guid projectId,
        Guid workflowStageId,
        int take,
        CancellationToken cancellationToken = default) =>
        await dbContext.TaskItems
            .Where(task =>
                task.ProjectId == projectId &&
                task.WorkflowStageId == workflowStageId &&
                !task.DeletedAt.HasValue)
            .OrderBy(task => task.SortKey)
            .ThenBy(task => task.Id)
            .Take(take)
            .ToListAsync(cancellationToken);
}
