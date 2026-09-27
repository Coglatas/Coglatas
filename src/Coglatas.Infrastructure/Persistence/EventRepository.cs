using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Events;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Infrastructure.Persistence;

public sealed class EventRepository(AppDbContext dbContext) : IEventRepository
{
    public async Task<IReadOnlyList<ActivityEvent>> ListAsync(EventListQuery query, CancellationToken cancellationToken = default)
    {
        return await ApplyEventFilters(BaseEventQuery(includeArchived: query.Status == EventStatus.Archived), query)
            .OrderBy(activityEvent => activityEvent.StartsAt)
            .ThenBy(activityEvent => activityEvent.Title)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ActivityEvent>> ListCalendarEventsAsync(CalendarQuery query, CancellationToken cancellationToken = default)
    {
        var source = BaseEventQuery(includeArchived: false)
            .Where(activityEvent => activityEvent.Status != EventStatus.Archived && activityEvent.DeletedAt == null);

        source = ApplyScopeFilters(
            source,
            query.WorkspaceId,
            query.GroupId,
            query.ProjectId);

        source = ApplyDateRange(source, query.FromDate, query.ToDate);

        return await source
            .OrderBy(activityEvent => activityEvent.StartsAt)
            .ThenBy(activityEvent => activityEvent.Title)
            .ToListAsync(cancellationToken);
    }

    public Task<ActivityEvent?> GetAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        return dbContext.ActivityEvents
            .Include(activityEvent => activityEvent.Workspace)
            .Include(activityEvent => activityEvent.Group)
            .Include(activityEvent => activityEvent.Project)
            .Include(activityEvent => activityEvent.CreatedByUser)
            .FirstOrDefaultAsync(activityEvent => activityEvent.Id == eventId, cancellationToken);
    }

    public async Task AddAsync(ActivityEvent activityEvent, CancellationToken cancellationToken = default)
    {
        await dbContext.ActivityEvents.AddAsync(activityEvent, cancellationToken);
    }

    public async Task<IReadOnlyList<EventAttendance>> ListAttendanceAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        return await dbContext.EventAttendances
            .Include(attendance => attendance.User)
            .Where(attendance => attendance.EventId == eventId)
            .OrderBy(attendance => attendance.User!.DisplayName)
            .ThenBy(attendance => attendance.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public Task<EventAttendance?> GetAttendanceAsync(Guid eventId, Guid userId, CancellationToken cancellationToken = default)
    {
        return dbContext.EventAttendances
            .Include(attendance => attendance.User)
            .FirstOrDefaultAsync(attendance => attendance.EventId == eventId && attendance.UserId == userId, cancellationToken);
    }

    public async Task AddAttendanceAsync(EventAttendance attendance, CancellationToken cancellationToken = default)
    {
        await dbContext.EventAttendances.AddAsync(attendance, cancellationToken);
    }

    public async Task<Dictionary<Guid, int>> GetAttendingCountsAsync(IReadOnlyCollection<Guid> eventIds, CancellationToken cancellationToken = default)
    {
        if (eventIds.Count == 0)
        {
            return [];
        }

        return await dbContext.EventAttendances
            .Where(attendance => eventIds.Contains(attendance.EventId) && attendance.Status == AttendanceStatus.Attending)
            .GroupBy(attendance => attendance.EventId)
            .ToDictionaryAsync(group => group.Key, group => group.Count(), cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> ListScopeRecipientUserIdsAsync(ActivityEvent activityEvent, CancellationToken cancellationToken = default)
    {
        if (activityEvent.WorkspaceId.HasValue)
        {
            return await dbContext.WorkspaceMembers
                .Where(member => member.WorkspaceId == activityEvent.WorkspaceId.Value && member.Status == MembershipStatus.Active)
                .Select(member => member.UserId)
                .Distinct()
                .ToListAsync(cancellationToken);
        }

        if (activityEvent.GroupId.HasValue)
        {
            return await dbContext.GroupMembers
                .Where(member => member.GroupId == activityEvent.GroupId.Value)
                .Select(member => member.UserId)
                .Distinct()
                .ToListAsync(cancellationToken);
        }

        if (activityEvent.ProjectId.HasValue)
        {
            return await dbContext.ProjectMembers
                .Where(member => member.ProjectId == activityEvent.ProjectId.Value)
                .Select(member => member.UserId)
                .Distinct()
                .ToListAsync(cancellationToken);
        }

        return [];
    }

    public async Task<IReadOnlyList<ProjectCalendarSourceItem>> ListProjectCalendarItemsAsync(CalendarQuery query, CancellationToken cancellationToken = default)
    {
        DateOnly? fromDate = query.FromDate.HasValue ? DateOnly.FromDateTime(query.FromDate.Value.UtcDateTime) : null;
        DateOnly? toDate = query.ToDate.HasValue ? DateOnly.FromDateTime(query.ToDate.Value.UtcDateTime) : null;

        var projects = dbContext.Projects
            .AsNoTracking()
            .Where(project => project.DeletedAt == null && project.Status != ProjectStatus.Archived);

        if (query.WorkspaceId.HasValue)
        {
            projects = projects.Where(project => project.WorkspaceId == query.WorkspaceId.Value);
        }

        if (query.GroupId.HasValue)
        {
            projects = projects.Where(project => project.GroupId == query.GroupId.Value);
        }

        if (query.ProjectId.HasValue)
        {
            projects = projects.Where(project => project.Id == query.ProjectId.Value);
        }

        var projectDeadlines = await projects
            .Where(project => project.DueDate.HasValue &&
                (!fromDate.HasValue || project.DueDate.Value >= fromDate.Value) &&
                (!toDate.HasValue || project.DueDate.Value <= toDate.Value))
            .Select(project => new ProjectCalendarSourceItem(
                "ProjectDeadline",
                project.Id,
                project.Id,
                project.WorkspaceId,
                project.GroupId,
                project.Name,
                project.Name,
                ToDateTimeOffset(project.DueDate!.Value),
                null,
                project.Status.ToString(),
                $"/projects/{project.Id}"))
            .ToListAsync(cancellationToken);

        var milestones = await dbContext.Milestones
            .AsNoTracking()
            .Where(milestone => milestone.DeletedAt == null && milestone.DueDate.HasValue)
            .Join(projects, milestone => milestone.ProjectId, project => project.Id, (milestone, project) => new { milestone, project })
            .Where(item =>
                (!fromDate.HasValue || item.milestone.DueDate!.Value >= fromDate.Value) &&
                (!toDate.HasValue || item.milestone.DueDate!.Value <= toDate.Value))
            .Select(item => new ProjectCalendarSourceItem(
                "Milestone",
                item.milestone.Id,
                item.project.Id,
                item.project.WorkspaceId,
                item.project.GroupId,
                item.project.Name,
                item.milestone.Name,
                ToDateTimeOffset(item.milestone.DueDate!.Value),
                null,
                item.milestone.Status.ToString(),
                $"/projects/{item.project.Id}/milestones/{item.milestone.Id}"))
            .ToListAsync(cancellationToken);

        var taskDueDates = await dbContext.TaskItems
            .AsNoTracking()
            .Where(task => task.DeletedAt == null && task.DueDate.HasValue)
            .Join(projects, task => task.ProjectId, project => project.Id, (task, project) => new { task, project })
            .Where(item =>
                (!fromDate.HasValue || item.task.DueDate!.Value >= fromDate.Value) &&
                (!toDate.HasValue || item.task.DueDate!.Value <= toDate.Value))
            .Select(item => new ProjectCalendarSourceItem(
                "TaskDueDate",
                item.task.Id,
                item.project.Id,
                item.project.WorkspaceId,
                item.project.GroupId,
                item.project.Name,
                item.task.Title,
                ToDateTimeOffset(item.task.DueDate!.Value),
                null,
                item.task.Status.ToString(),
                $"/tasks/{item.task.Id}"))
            .ToListAsync(cancellationToken);

        return projectDeadlines
            .Concat(milestones)
            .Concat(taskDueDates)
            .OrderBy(item => item.StartsAt)
            .ThenBy(item => item.Title)
            .ToList();
    }

    private IQueryable<ActivityEvent> BaseEventQuery(bool includeArchived)
    {
        var source = dbContext.ActivityEvents
            .AsNoTracking()
            .Include(activityEvent => activityEvent.Workspace)
            .Include(activityEvent => activityEvent.Group)
            .Include(activityEvent => activityEvent.Project)
            .Include(activityEvent => activityEvent.CreatedByUser)
            .AsQueryable();

        return includeArchived
            ? source
            : source.Where(activityEvent => activityEvent.DeletedAt == null && activityEvent.Status != EventStatus.Archived);
    }

    private static IQueryable<ActivityEvent> ApplyScopeFilters(
        IQueryable<ActivityEvent> source,
        Guid? workspaceId,
        Guid? groupId,
        Guid? projectId)
    {
        if (workspaceId.HasValue)
        {
            source = source.Where(activityEvent =>
                activityEvent.WorkspaceId == workspaceId ||
                (activityEvent.Project != null && activityEvent.Project.WorkspaceId == workspaceId));
        }

        if (groupId.HasValue)
        {
            source = source.Where(activityEvent =>
                activityEvent.GroupId == groupId ||
                (activityEvent.Project != null && activityEvent.Project.GroupId == groupId));
        }

        if (projectId.HasValue)
        {
            source = source.Where(activityEvent => activityEvent.ProjectId == projectId);
        }

        return source;
    }

    private static IQueryable<ActivityEvent> ApplyEventFilters(IQueryable<ActivityEvent> source, EventListQuery query)
    {
        source = ApplyScopeFilters(
            source,
            query.WorkspaceId,
            query.GroupId,
            query.ProjectId);

        source = ApplyDateRange(source, query.FromDate, query.ToDate);

        if (query.Status.HasValue)
        {
            source = source.Where(activityEvent => activityEvent.Status == query.Status.Value);
        }

        return source;
    }

    private static IQueryable<ActivityEvent> ApplyDateRange(IQueryable<ActivityEvent> source, DateTimeOffset? fromDate, DateTimeOffset? toDate)
    {
        // PostgreSQL timestamptz parameters require a zero UTC offset.
        var utcFrom = fromDate?.ToUniversalTime();
        var utcTo = toDate?.ToUniversalTime();
        if (utcFrom.HasValue)
        {
            source = source.Where(activityEvent => activityEvent.EndsAt >= utcFrom.Value);
        }

        if (utcTo.HasValue)
        {
            source = source.Where(activityEvent => activityEvent.StartsAt <= utcTo.Value);
        }

        return source;
    }

    private static DateTimeOffset ToDateTimeOffset(DateOnly date)
    {
        return new DateTimeOffset(DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc));
    }
}
