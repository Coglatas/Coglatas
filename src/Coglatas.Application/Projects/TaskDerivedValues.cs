using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;

namespace Coglatas.Application.Projects;

/// <summary>
/// Read-time authoritative values for a parent Task.  These values are never a
/// cache of the parent row: every projection must calculate them from direct
/// children using this class.
/// </summary>
public sealed record ParentTaskDerivedValues(
    bool IsDerived,
    DateOnly? PlannedStartDate,
    DateOnly? PlannedEndDate,
    int ProgressPercent);

public static class ParentTaskDerivedValuesCalculator
{
    public static ParentTaskDerivedValues Calculate(
        TaskItem parent,
        IEnumerable<TaskItem> projectTasks,
        Func<TaskItem, TaskStageCategory> categoryOf)
    {
        var children = projectTasks
            .Where(task => task.ParentTaskItemId == parent.Id)
            .Where(task => task.Kind == WorkItemKind.Task)
            .Where(task => !task.DeletedAt.HasValue)
            .ToArray();

        if (children.Length == 0)
        {
            return new ParentTaskDerivedValues(false, parent.PlannedStartDate ?? parent.StartDate,
                parent.PlannedEndDate ?? parent.DueDate, parent.ProgressPercent);
        }

        // A direct child makes the parent derived even when it is Cancelled.  A
        // cancellation only removes the child from progress aggregation; it
        // must never make the parent editable again while the child exists.
        var starts = children.Select(task => task.PlannedStartDate ?? task.StartDate).Where(date => date.HasValue).Select(date => date!.Value).ToArray();
        var ends = children.Select(task => task.PlannedEndDate ?? task.DueDate).Where(date => date.HasValue).Select(date => date!.Value).ToArray();
        var progressChildren = children.Where(task => categoryOf(task) != TaskStageCategory.Cancelled).ToArray();
        if (progressChildren.Length == 0)
        {
            return new ParentTaskDerivedValues(true, starts.Length == 0 ? null : starts.Min(), ends.Length == 0 ? null : ends.Max(), 0);
        }

        var estimates = progressChildren.Select(task => task.EstimatedEffortMinutes).ToArray();
        var useWeightedProgress = estimates.All(estimate => estimate is > 0);
        var progress = useWeightedProgress
            ? (int)Math.Round(progressChildren.Sum(task => task.ProgressPercent * (decimal)task.EstimatedEffortMinutes!.Value) / estimates.Sum(estimate => (decimal)estimate!.Value), MidpointRounding.AwayFromZero)
            : (int)Math.Round(progressChildren.Average(task => task.ProgressPercent), MidpointRounding.AwayFromZero);

        return new ParentTaskDerivedValues(
            true,
            starts.Length == 0 ? null : starts.Min(),
            ends.Length == 0 ? null : ends.Max(),
            Math.Clamp(progress, 0, 100));
    }
}

/// <summary>Central deadline semantics for Task detail and subtask summaries.</summary>
public static class TaskDeadlineCalculator
{
    public static bool IsOverdue(TaskItem task, TaskStageCategory category, TimeZoneInfo workspaceTimeZone, DateTimeOffset now, DateOnly? plannedEndOverride = null)
    {
        if (category is TaskStageCategory.Done or TaskStageCategory.Cancelled)
        {
            return false;
        }

        if (task.DeadlineAt.HasValue)
        {
            return task.DeadlineAt.Value < now;
        }

        var plannedEnd = plannedEndOverride ?? task.PlannedEndDate ?? task.DueDate;
        if (!plannedEnd.HasValue)
        {
            return false;
        }

        // Planning dates are whole workspace-local days.  Comparing local dates
        // avoids manufacturing an artificial UTC 23:59:59 deadline and remains
        // correct across daylight-saving transitions.
        var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, workspaceTimeZone).DateTime);
        return plannedEnd.Value < localToday;
    }
}

public interface ITaskWorkspaceTimeZoneResolver
{
    Task<TimeZoneInfo> ResolveAsync(Guid tenantId, Guid workspaceId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves the workspace-local planning timezone, using the owning tenant's
/// setting only when the Workspace candidate is absent or invalid.
/// </summary>
public sealed class TaskWorkspaceTimeZoneResolver(
    Coglatas.Application.Common.Interfaces.IWorkspaceRepository workspaces,
    Coglatas.Application.Common.Interfaces.ITenantPlanRepository tenantPlans) : ITaskWorkspaceTimeZoneResolver
{
    public async Task<TimeZoneInfo> ResolveAsync(Guid tenantId, Guid workspaceId, CancellationToken cancellationToken = default)
    {
        var workspace = await workspaces.GetByIdAsync(workspaceId, cancellationToken);
        var workspaceZone = workspace?.TenantId == tenantId ? TryResolve(workspace.TimeZone) : null;
        if (workspaceZone is not null)
            return workspaceZone;

        var tenantZone = TryResolve((await tenantPlans.GetTenantSettingsAsync(tenantId, cancellationToken))?.TimeZone);
        return tenantZone ?? TimeZoneInfo.Utc;
    }

    private static TimeZoneInfo? TryResolve(string? zoneId)
    {
        if (string.IsNullOrWhiteSpace(zoneId))
            return null;

        try { return TimeZoneInfo.FindSystemTimeZoneById(zoneId.Trim()); }
        catch (TimeZoneNotFoundException) { return null; }
        catch (InvalidTimeZoneException) { return null; }
    }
}

public static class TaskWatchStateInitializer
{
    public static WorkItemWatchState ForCreator(TaskItem task, Guid creatorUserId, DateTimeOffset now) => new()
    {
        TaskItemId = task.Id,
        UserId = creatorUserId,
        AutomaticSources = WorkItemWatchAutomaticSource.Creator,
        IsWatching = TaskWatchStateRules.IsWatching(false, false, WorkItemWatchAutomaticSource.Creator),
        UpdatedAt = now,
        VersionNo = 1
    };
}

/// <summary>One canonical state machine for private watch intent and relationship sources.</summary>
public static class TaskWatchStateRules
{
    public static bool IsWatching(bool isManualWatch, bool isExplicitOptOut, WorkItemWatchAutomaticSource automaticSources) =>
        isManualWatch || (!isExplicitOptOut && automaticSources != WorkItemWatchAutomaticSource.None);

    public static bool Normalize(WorkItemWatchState state)
    {
        var isWatching = IsWatching(state.IsManualWatch, state.IsExplicitOptOut, state.AutomaticSources);
        if (state.IsWatching == isWatching)
            return false;

        state.IsWatching = isWatching;
        return true;
    }
}
