using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;

namespace Coglatas.Application.Projects;

/// <summary>
/// Places newly created canonical Tasks on the existing Project workflow.
/// Creation and Kanban therefore share WorkflowStageId and SortKey instead of
/// requiring a board-only record or a later repair.
/// </summary>
internal static class TaskInitialPlacement
{
    private const int RankGap = 1000;
    private const long MaximumRankBeforeAppend = long.MaxValue - RankGap;

    public static async Task<Result> ApplyAsync(
        IProjectRepository projects,
        TaskItem task,
        CancellationToken cancellationToken)
    {
        if (task.WorkflowStageId.HasValue)
            return Result.Success();

        var initialStage = await projects.GetInitialWorkflowStageAsync(task.ProjectId, cancellationToken);
        if (initialStage is null)
            return Result.Failure("TASK_WORKFLOW_NOT_CONFIGURED|The Project workflow has no initial Stage.");

        var maximumRank = await projects.GetMaximumTaskSortKeyAsync(task.ProjectId, initialStage.Id, cancellationToken);
        if (maximumRank is > MaximumRankBeforeAppend)
            return Result.Failure("TASK_ORDER_CAPACITY_EXHAUSTED|The initial Workflow Stage cannot accept another stable Task rank.");

        task.WorkflowStageId = initialStage.Id;
        task.WorkflowStage = initialStage;
        task.Status = TaskItemStatus.NotStarted;
        task.SortKey = maximumRank.HasValue ? maximumRank.Value + RankGap : RankGap;
        return Result.Success();
    }
}
