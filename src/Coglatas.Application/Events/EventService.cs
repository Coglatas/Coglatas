using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Projects;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;

namespace Coglatas.Application.Events;

public sealed class EventService(
    IEventRepository events,
    IUserRepository users,
    IWorkspaceRepository workspaces,
    IGroupRepository groups,
    IProjectRepository projects,
    IEventAuthorizationService authorization,
    IProjectAuthorizationService projectAuthorization,
    ICurrentUser currentUser,
    IClock clock,
    IAuditLogger auditLogger,
    INotificationService notifications,
    IUnitOfWork unitOfWork) : IEventService
{
    private const int MaxPageSize = 100;

    public async Task<Result<PagedResponse<EventListItemResponse>>> ListAsync(EventListQuery query, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId))
        {
            return Result<PagedResponse<EventListItemResponse>>.Failure("Authentication is required.");
        }

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);
        var normalizedQuery = query with { Page = page, PageSize = pageSize };
        var candidates = await events.ListAsync(normalizedQuery, cancellationToken);
        var visible = new List<ActivityEvent>();

        foreach (var activityEvent in candidates)
        {
            if (await authorization.CanViewEvent(userId, activityEvent, cancellationToken))
            {
                visible.Add(activityEvent);
            }
        }

        var pageItems = visible
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        var attendingCounts = await events.GetAttendingCountsAsync(pageItems.Select(item => item.Id).ToArray(), cancellationToken);
        var items = pageItems
            .Select(item => ToListItem(item, attendingCounts.GetValueOrDefault(item.Id)))
            .ToList();

        return Result<PagedResponse<EventListItemResponse>>.Success(new PagedResponse<EventListItemResponse>(items, page, pageSize, visible.Count));
    }

    public async Task<Result<EventDetailResponse>> CreateAsync(CreateEventRequest request, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId))
        {
            return Result<EventDetailResponse>.Failure("Authentication is required.");
        }

        var validation = await ValidateEventRequestAsync(
            request.WorkspaceId,
            request.GroupId,
            request.ProjectId,
            request.Title,
            request.StartsAt,
            request.EndsAt,
            request.AttendanceDeadline,
            request.Capacity,
            cancellationToken);
        if (!validation.IsSuccess)
        {
            return Result<EventDetailResponse>.Failure(validation.Error!);
        }

        if (!await authorization.CanCreateEvent(userId, request.WorkspaceId, request.GroupId, request.ProjectId, cancellationToken))
        {
            return Result<EventDetailResponse>.Failure("You are not allowed to create events in the selected scope.");
        }

        var activityEvent = new ActivityEvent
        {
            WorkspaceId = request.WorkspaceId,
            GroupId = request.GroupId,
            ProjectId = request.ProjectId,
            CreatedByUserId = userId,
            Title = request.Title.Trim(),
            Description = NormalizeOptionalText(request.Description),
            Location = NormalizeOptionalText(request.Location),
            StartsAt = request.StartsAt,
            EndsAt = request.EndsAt,
            AttendanceDeadline = request.AttendanceDeadline,
            Capacity = request.Capacity,
            BringItemsText = NormalizeOptionalText(request.BringItemsText),
            Status = request.Status
        };

        await events.AddAsync(activityEvent, cancellationToken);
        await auditLogger.LogUserActionAsync(userId, "EventCreated", "ActivityEvent", activityEvent.Id, "Event created.", cancellationToken: cancellationToken);
        await NotifyEventChangeAsync(userId, activityEvent, null, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var persisted = await events.GetAsync(activityEvent.Id, cancellationToken) ?? activityEvent;
        return Result<EventDetailResponse>.Success(ToDetail(persisted, 0, null));
    }

    public async Task<Result<EventDetailResponse>> GetAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        var activityEvent = await events.GetAsync(eventId, cancellationToken);
        if (activityEvent is null)
        {
            return Result<EventDetailResponse>.Failure("Event not found.");
        }

        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId) || !await authorization.CanViewEvent(userId, activityEvent, cancellationToken))
        {
            return Result<EventDetailResponse>.Failure("Event not found.");
        }

        var currentAttendance = await events.GetAttendanceAsync(activityEvent.Id, userId, cancellationToken);
        var attendingCounts = await events.GetAttendingCountsAsync([activityEvent.Id], cancellationToken);
        return Result<EventDetailResponse>.Success(ToDetail(activityEvent, attendingCounts.GetValueOrDefault(activityEvent.Id), currentAttendance));
    }

    public async Task<Result<EventDetailResponse>> UpdateAsync(Guid eventId, UpdateEventRequest request, CancellationToken cancellationToken = default)
    {
        var activityEvent = await events.GetAsync(eventId, cancellationToken);
        if (activityEvent is null)
        {
            return Result<EventDetailResponse>.Failure("Event not found.");
        }

        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId) || !await authorization.CanManageEvent(userId, activityEvent, cancellationToken))
        {
            return Result<EventDetailResponse>.Failure("You are not allowed to update this event.");
        }

        var hasScopeUpdate = request.WorkspaceId.HasValue || request.GroupId.HasValue || request.ProjectId.HasValue;
        var nextWorkspaceId = hasScopeUpdate ? request.WorkspaceId : activityEvent.WorkspaceId;
        var nextGroupId = hasScopeUpdate ? request.GroupId : activityEvent.GroupId;
        var nextProjectId = hasScopeUpdate ? request.ProjectId : activityEvent.ProjectId;
        var nextTitle = request.Title ?? activityEvent.Title;
        var nextStartsAt = request.StartsAt ?? activityEvent.StartsAt;
        var nextEndsAt = request.EndsAt ?? activityEvent.EndsAt;
        var nextAttendanceDeadline = request.AttendanceDeadline ?? activityEvent.AttendanceDeadline;
        var nextCapacity = request.Capacity ?? activityEvent.Capacity;

        var validation = await ValidateEventRequestAsync(
            nextWorkspaceId,
            nextGroupId,
            nextProjectId,
            nextTitle,
            nextStartsAt,
            nextEndsAt,
            nextAttendanceDeadline,
            nextCapacity,
            cancellationToken);
        if (!validation.IsSuccess)
        {
            return Result<EventDetailResponse>.Failure(validation.Error!);
        }

        if (hasScopeUpdate && !await authorization.CanCreateEvent(userId, nextWorkspaceId, nextGroupId, nextProjectId, cancellationToken))
        {
            return Result<EventDetailResponse>.Failure("You are not allowed to move this event to the selected scope.");
        }

        var previousStatus = activityEvent.Status;
        activityEvent.WorkspaceId = nextWorkspaceId;
        activityEvent.GroupId = nextGroupId;
        activityEvent.ProjectId = nextProjectId;
        activityEvent.Title = nextTitle.Trim();
        activityEvent.Description = request.Description is null ? activityEvent.Description : NormalizeOptionalText(request.Description);
        activityEvent.Location = request.Location is null ? activityEvent.Location : NormalizeOptionalText(request.Location);
        activityEvent.StartsAt = nextStartsAt;
        activityEvent.EndsAt = nextEndsAt;
        activityEvent.AttendanceDeadline = nextAttendanceDeadline;
        activityEvent.Capacity = nextCapacity;
        activityEvent.BringItemsText = request.BringItemsText is null ? activityEvent.BringItemsText : NormalizeOptionalText(request.BringItemsText);
        activityEvent.Status = request.Status ?? activityEvent.Status;

        if (activityEvent is { Status: EventStatus.Archived, DeletedAt: null })
        {
            activityEvent.MarkDeleted(clock.UtcNow);
        }

        await auditLogger.LogUserActionAsync(userId, "EventUpdated", "ActivityEvent", activityEvent.Id, "Event updated.", cancellationToken: cancellationToken);
        if (activityEvent.Status == EventStatus.Cancelled && previousStatus != EventStatus.Cancelled)
        {
            await auditLogger.LogUserActionAsync(userId, "EventCancelled", "ActivityEvent", activityEvent.Id, "Event cancelled.", cancellationToken: cancellationToken);
        }

        if (activityEvent.Status == EventStatus.Archived && previousStatus != EventStatus.Archived)
        {
            await auditLogger.LogUserActionAsync(userId, "EventArchived", "ActivityEvent", activityEvent.Id, "Event archived.", cancellationToken: cancellationToken);
        }

        await NotifyEventChangeAsync(userId, activityEvent, previousStatus, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var currentAttendance = await events.GetAttendanceAsync(activityEvent.Id, userId, cancellationToken);
        var attendingCounts = await events.GetAttendingCountsAsync([activityEvent.Id], cancellationToken);
        return Result<EventDetailResponse>.Success(ToDetail(activityEvent, attendingCounts.GetValueOrDefault(activityEvent.Id), currentAttendance));
    }

    public async Task<Result> DeleteAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        var activityEvent = await events.GetAsync(eventId, cancellationToken);
        if (activityEvent is null)
        {
            return Result.Failure("Event not found.");
        }

        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId) || !await authorization.CanManageEvent(userId, activityEvent, cancellationToken))
        {
            return Result.Failure("You are not allowed to archive this event.");
        }

        activityEvent.Status = EventStatus.Archived;
        if (!activityEvent.DeletedAt.HasValue)
        {
            activityEvent.MarkDeleted(clock.UtcNow);
        }

        await auditLogger.LogUserActionAsync(userId, "EventArchived", "ActivityEvent", activityEvent.Id, "Event archived.", cancellationToken: cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result<IReadOnlyList<AttendanceResponse>>> GetAttendanceAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        var activityEvent = await events.GetAsync(eventId, cancellationToken);
        if (activityEvent is null)
        {
            return Result<IReadOnlyList<AttendanceResponse>>.Failure("Event not found.");
        }

        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId) || !await authorization.CanManageAttendance(userId, activityEvent, cancellationToken))
        {
            return Result<IReadOnlyList<AttendanceResponse>>.Failure("You are not allowed to view attendance for this event.");
        }

        var attendances = await events.ListAttendanceAsync(eventId, cancellationToken);
        return Result<IReadOnlyList<AttendanceResponse>>.Success(attendances.Select(ToAttendance).ToList());
    }

    public async Task<Result<AttendanceResponse>> UpsertMyAttendanceAsync(Guid eventId, UpdateMyAttendanceRequest request, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId))
        {
            return Result<AttendanceResponse>.Failure("Authentication is required.");
        }

        var activityEvent = await events.GetAsync(eventId, cancellationToken);
        if (activityEvent is null || !await authorization.CanViewEvent(userId, activityEvent, cancellationToken))
        {
            return Result<AttendanceResponse>.Failure("Event not found.");
        }

        var canOverride = await authorization.CanManageAttendance(userId, activityEvent, cancellationToken);
        return await UpsertAttendanceCoreAsync(activityEvent, userId, userId, request.Status, request.Comment, canOverride, "AttendanceSubmitted", cancellationToken);
    }

    public async Task<Result<AttendanceResponse>> UpdateAttendanceAsync(Guid eventId, Guid userId, UpdateAttendanceRequest request, CancellationToken cancellationToken = default)
    {
        var activityEvent = await events.GetAsync(eventId, cancellationToken);
        if (activityEvent is null)
        {
            return Result<AttendanceResponse>.Failure("Event not found.");
        }

        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var actorUserId) || !await authorization.CanManageAttendance(actorUserId, activityEvent, cancellationToken))
        {
            return Result<AttendanceResponse>.Failure("You are not allowed to update attendance for this event.");
        }

        var targetUser = await users.GetByIdAsync(userId, cancellationToken);
        if (targetUser is null || targetUser.Status != UserStatus.Active)
        {
            return Result<AttendanceResponse>.Failure("User not found.");
        }

        if (!await authorization.CanAccessScope(userId, activityEvent, cancellationToken))
        {
            return Result<AttendanceResponse>.Failure("The selected user cannot access this event scope.");
        }

        return await UpsertAttendanceCoreAsync(activityEvent, actorUserId, userId, request.Status, request.Comment, true, "AttendanceChangedByAdmin", cancellationToken);
    }

    public async Task<Result<IReadOnlyList<CalendarItemResponse>>> GetCalendarAsync(CalendarQuery query, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId))
        {
            return Result<IReadOnlyList<CalendarItemResponse>>.Failure("Authentication is required.");
        }

        var items = new List<CalendarItemResponse>();
        var visibleEvents = await events.ListCalendarEventsAsync(query, cancellationToken);
        foreach (var activityEvent in visibleEvents)
        {
            if (await authorization.CanViewEvent(userId, activityEvent, cancellationToken))
            {
                items.Add(new CalendarItemResponse(
                    "Event",
                    activityEvent.Id,
                    activityEvent.Title,
                    activityEvent.StartsAt,
                    activityEvent.EndsAt,
                    $"/events/{activityEvent.Id}",
                    activityEvent.Status.ToString(),
                    ToScopeSummary(activityEvent)));
            }
        }

        var projectItems = await events.ListProjectCalendarItemsAsync(query, cancellationToken);
        foreach (var item in projectItems)
        {
            if (await projectAuthorization.CanViewProject(userId, item.ProjectId, cancellationToken))
            {
                items.Add(new CalendarItemResponse(
                    item.ItemType,
                    item.Id,
                    item.Title,
                    item.StartsAt,
                    item.EndsAt,
                    item.Route,
                    item.Status,
                    new ScopeSummaryResponse("Project", item.WorkspaceId, item.GroupId, item.ProjectId, item.ScopeLabel)));
            }
        }

        return Result<IReadOnlyList<CalendarItemResponse>>.Success(items
            .OrderBy(item => item.StartsAt)
            .ThenBy(item => item.Title)
            .ToList());
    }

    private async Task<Result<AttendanceResponse>> UpsertAttendanceCoreAsync(
        ActivityEvent activityEvent,
        Guid actorUserId,
        Guid userId,
        AttendanceStatus status,
        string? comment,
        bool allowOverride,
        string auditAction,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(status))
        {
            return Result<AttendanceResponse>.Failure("Attendance status is invalid.");
        }

        if (activityEvent.Status != EventStatus.Published)
        {
            return Result<AttendanceResponse>.Failure("Attendance is only available for published events.");
        }

        if (activityEvent.Status is EventStatus.Cancelled or EventStatus.Completed or EventStatus.Archived)
        {
            return Result<AttendanceResponse>.Failure("Attendance is closed for this event.");
        }

        if (!allowOverride &&
            activityEvent.AttendanceDeadline.HasValue &&
            clock.UtcNow >= activityEvent.AttendanceDeadline.Value)
        {
            return Result<AttendanceResponse>.Failure("Attendance is closed for this event.");
        }

        var attendance = await events.GetAttendanceAsync(activityEvent.Id, userId, cancellationToken);
        var normalizedComment = NormalizeOptionalText(comment);
        if (status == AttendanceStatus.Attending && activityEvent.Capacity.HasValue && !allowOverride)
        {
            var attendingCounts = await events.GetAttendingCountsAsync([activityEvent.Id], cancellationToken);
            var currentCount = attendingCounts.GetValueOrDefault(activityEvent.Id);
            if (currentCount - (attendance?.Status == AttendanceStatus.Attending ? 1 : 0) >= activityEvent.Capacity.Value)
            {
                return Result<AttendanceResponse>.Failure("Event capacity has been reached.");
            }
        }

        if (attendance is null)
        {
            var user = await users.GetByIdAsync(userId, cancellationToken);
            attendance = new EventAttendance
            {
                EventId = activityEvent.Id,
                UserId = userId,
                User = user,
                Status = status,
                Comment = normalizedComment,
                RespondedAt = ShouldRefreshRespondedAt(null, null, status, normalizedComment) ? clock.UtcNow : null
            };
            await events.AddAttendanceAsync(attendance, cancellationToken);
        }
        else
        {
            if (ShouldRefreshRespondedAt(attendance.Status, attendance.Comment, status, normalizedComment))
            {
                attendance.RespondedAt = clock.UtcNow;
            }

            attendance.Status = status;
            attendance.Comment = normalizedComment;
        }

        await auditLogger.LogUserActionAsync(actorUserId, auditAction, "ActivityEvent", activityEvent.Id, "Event attendance updated.", cancellationToken: cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result<AttendanceResponse>.Success(ToAttendance(attendance));
    }

    private async Task<Result> ValidateEventRequestAsync(
        Guid? workspaceId,
        Guid? groupId,
        Guid? projectId,
        string title,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        DateTimeOffset? attendanceDeadline,
        int? capacity,
        CancellationToken cancellationToken)
    {
        if (!ScopedResourceValidation.HasExactlyOneScope(workspaceId, groupId, projectId))
        {
            return Result.Failure("Exactly one of WorkspaceId, GroupId, or ProjectId must be set.");
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return Result.Failure("Event title is required.");
        }

        if (startsAt >= endsAt)
        {
            return Result.Failure("Event start time must be before the end time.");
        }

        if (attendanceDeadline.HasValue && attendanceDeadline.Value > startsAt)
        {
            return Result.Failure("Attendance deadline cannot be after the event start time.");
        }

        if (capacity is < 0)
        {
            return Result.Failure("Capacity must be greater than or equal to 0.");
        }

        return await ScopedResourceValidation.ValidateExistingScopeAsync(
            workspaces,
            groups,
            projects,
            workspaceId,
            groupId,
            projectId,
            cancellationToken);
    }

    private async Task NotifyEventChangeAsync(Guid actorUserId, ActivityEvent activityEvent, EventStatus? previousStatus, CancellationToken cancellationToken)
    {
        if (activityEvent.Status == EventStatus.Draft || activityEvent.Status == EventStatus.Archived)
        {
            return;
        }

        var recipientUserIds = await events.ListScopeRecipientUserIdsAsync(activityEvent, cancellationToken);
        if (recipientUserIds.Count == 0)
        {
            return;
        }

        if (activityEvent.Status == EventStatus.Published && previousStatus != EventStatus.Published)
        {
            await notifications.CreateManyAsync(
                recipientUserIds,
                NotificationType.Event,
                $"Event published: {activityEvent.Title}",
                activityEvent.Description,
                "ActivityEvent",
                activityEvent.Id,
                actorUserId,
                cancellationToken);
            return;
        }

        if (activityEvent.Status == EventStatus.Cancelled && previousStatus != EventStatus.Cancelled)
        {
            await notifications.CreateManyAsync(
                recipientUserIds,
                NotificationType.Event,
                $"Event cancelled: {activityEvent.Title}",
                activityEvent.Description,
                "ActivityEvent",
                activityEvent.Id,
                actorUserId,
                cancellationToken);
            return;
        }

        if (activityEvent.Status == EventStatus.Published)
        {
            await notifications.CreateManyAsync(
                recipientUserIds,
                NotificationType.Event,
                $"Event updated: {activityEvent.Title}",
                activityEvent.Description,
                "ActivityEvent",
                activityEvent.Id,
                actorUserId,
                cancellationToken);
        }

        // TODO: add scheduled attendance deadline reminders when background jobs are introduced.
    }

    private static string? NormalizeOptionalText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool ShouldRefreshRespondedAt(AttendanceStatus? existingStatus, string? existingComment, AttendanceStatus newStatus, string? newComment)
    {
        if (newStatus != AttendanceStatus.Unanswered && existingStatus != newStatus)
        {
            return true;
        }

        return newStatus != AttendanceStatus.Unanswered &&
            !string.Equals(NormalizeOptionalText(existingComment), NormalizeOptionalText(newComment), StringComparison.Ordinal);
    }

    private static EventListItemResponse ToListItem(ActivityEvent activityEvent, int attendingCount)
    {
        return new EventListItemResponse(
            activityEvent.Id,
            activityEvent.Title,
            activityEvent.StartsAt,
            activityEvent.EndsAt,
            activityEvent.Location,
            activityEvent.Status,
            ToScopeSummary(activityEvent),
            activityEvent.Capacity,
            attendingCount);
    }

    private static EventDetailResponse ToDetail(ActivityEvent activityEvent, int attendingCount, EventAttendance? currentAttendance)
    {
        return new EventDetailResponse(
            activityEvent.Id,
            activityEvent.WorkspaceId,
            activityEvent.GroupId,
            activityEvent.ProjectId,
            activityEvent.CreatedByUserId,
            activityEvent.Title,
            activityEvent.Description,
            activityEvent.Location,
            activityEvent.StartsAt,
            activityEvent.EndsAt,
            activityEvent.AttendanceDeadline,
            activityEvent.Capacity,
            activityEvent.BringItemsText,
            activityEvent.Status,
            activityEvent.CreatedAt,
            activityEvent.UpdatedAt,
            activityEvent.DeletedAt,
            ToScopeSummary(activityEvent),
            attendingCount,
            currentAttendance is null ? null : ToAttendance(currentAttendance));
    }

    private static AttendanceResponse ToAttendance(EventAttendance attendance)
    {
        return new AttendanceResponse(
            attendance.EventId,
            attendance.UserId,
            attendance.User?.DisplayName,
            attendance.Status,
            attendance.Comment,
            attendance.RespondedAt,
            attendance.UpdatedAt);
    }

    private static ScopeSummaryResponse ToScopeSummary(ActivityEvent activityEvent)
    {
        if (activityEvent.ProjectId.HasValue)
        {
            return new ScopeSummaryResponse(
                "Project",
                activityEvent.Project?.WorkspaceId,
                activityEvent.Project?.GroupId,
                activityEvent.ProjectId,
                activityEvent.Project?.Name ?? "Project");
        }

        if (activityEvent.GroupId.HasValue)
        {
            return new ScopeSummaryResponse(
                "Group",
                activityEvent.Group?.WorkspaceId,
                activityEvent.GroupId,
                null,
                activityEvent.Group?.Name ?? "Group");
        }

        return new ScopeSummaryResponse(
            "Workspace",
            activityEvent.WorkspaceId,
            null,
            null,
            activityEvent.Workspace?.Name ?? "Workspace");
    }
}
