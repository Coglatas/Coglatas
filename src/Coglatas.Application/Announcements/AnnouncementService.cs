using Coglatas.Application.Channels;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Groups;
using Coglatas.Application.Workspaces;
using Coglatas.Application.Realtime;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;

namespace Coglatas.Application.Announcements;

public sealed class AnnouncementService(
    IAnnouncementRepository announcements,
    IWorkspaceRepository workspaces,
    IGroupRepository groups,
    IChannelRepository channels,
    IUserRepository users,
    IWorkspaceAuthorizationService workspaceAuthorization,
    IGroupAuthorizationService groupAuthorization,
    IChannelAuthorizationService channelAuthorization,
    ICurrentUser currentUser,
    IClock clock,
    IAuditLogger auditLogger,
    INotificationService notifications,
    IBusinessInvalidationPublisher invalidations,
    IUnitOfWork unitOfWork) : IAnnouncementService
{
    private const int MaxPageSize = 100;

    public async Task<Result<PagedResponse<AnnouncementListItemResponse>>> ListAsync(AnnouncementListQuery query, CancellationToken cancellationToken = default)
    {
        if (!TryCurrentUser(out var userId))
        {
            return Result<PagedResponse<AnnouncementListItemResponse>>.Failure("Authentication is required.");
        }

        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);
        var page = NormalizePage(query.Page, pageSize);
        var normalizedQuery = query with { Page = page, PageSize = pageSize };
        var result = await announcements.ListVisibleAsync(userId, await IsSystemAdminAsync(userId, cancellationToken), normalizedQuery, cancellationToken);
        var items = new List<AnnouncementListItemResponse>();
        foreach (var announcement in result.Items)
        {
            items.Add(ToListItem(announcement, await announcements.HasReadAsync(announcement.Id, userId, cancellationToken)));
        }

        return Result<PagedResponse<AnnouncementListItemResponse>>.Success(new PagedResponse<AnnouncementListItemResponse>(items, result.Page, result.PageSize, result.TotalCount));
    }

    public async Task<Result<AnnouncementDetailResponse>> CreateAsync(CreateAnnouncementRequest request, CancellationToken cancellationToken = default)
    {
        if (!TryCurrentUser(out var userId))
        {
            return Result<AnnouncementDetailResponse>.Failure("Authentication is required.");
        }

        var validation = await ValidateRequestAsync(request.Title, request.Body, request.PublishedAt ?? clock.UtcNow, request.ExpiresAt);
        if (!validation.IsSuccess)
        {
            return Result<AnnouncementDetailResponse>.Failure(validation.Error!);
        }

        var scope = await ResolveCreateScopeAsync(userId, request, cancellationToken);
        if (!scope.IsSuccess)
        {
            return Result<AnnouncementDetailResponse>.Failure(scope.Error!);
        }

        var announcement = new Announcement
        {
            WorkspaceId = scope.Value!.WorkspaceId,
            GroupId = scope.Value.GroupId,
            ChannelId = scope.Value.ChannelId,
            AuthorUserId = userId,
            Title = request.Title.Trim(),
            Body = request.Body.Trim(),
            Priority = request.Priority,
            IsPinned = request.IsPinned,
            RequiresReadConfirmation = request.RequiresReadConfirmation,
            PublishedAt = request.PublishedAt ?? clock.UtcNow,
            ExpiresAt = request.ExpiresAt
        };

        await announcements.AddAsync(announcement, cancellationToken);
        await auditLogger.LogUserActionAsync(userId, "AnnouncementCreated", "Announcement", announcement.Id, "Announcement created.", cancellationToken: cancellationToken);
        if (announcement.IsPinned)
        {
            await auditLogger.LogUserActionAsync(userId, "AnnouncementPinned", "Announcement", announcement.Id, "Announcement pinned.", cancellationToken: cancellationToken);
        }

        await PublishInvalidationAsync(announcement, userId, "created", cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result<AnnouncementDetailResponse>.Success(ToDetail(announcement, false));
    }

    public async Task<Result<AnnouncementDetailResponse>> GetAsync(Guid announcementId, CancellationToken cancellationToken = default)
    {
        if (!TryCurrentUser(out var userId))
        {
            return Result<AnnouncementDetailResponse>.Failure("Authentication is required.");
        }

        if (!await announcements.IsVisibleToUserAsync(announcementId, userId, await IsSystemAdminAsync(userId, cancellationToken), cancellationToken))
        {
            return Result<AnnouncementDetailResponse>.Failure("Announcement not found.");
        }

        var announcement = await announcements.GetAsync(announcementId, cancellationToken);
        if (announcement is null || announcement.DeletedAt.HasValue)
        {
            return Result<AnnouncementDetailResponse>.Failure("Announcement not found.");
        }

        return Result<AnnouncementDetailResponse>.Success(ToDetail(announcement, await announcements.HasReadAsync(announcementId, userId, cancellationToken)));
    }

    public async Task<Result<AnnouncementDetailResponse>> UpdateAsync(Guid announcementId, UpdateAnnouncementRequest request, CancellationToken cancellationToken = default)
    {
        var announcement = await announcements.GetAsync(announcementId, cancellationToken);
        if (announcement is null || announcement.DeletedAt.HasValue)
        {
            return Result<AnnouncementDetailResponse>.Failure("Announcement not found.");
        }

        if (!TryCurrentUser(out var userId) || !await CanManageAnnouncementAsync(userId, announcement, cancellationToken))
        {
            return Result<AnnouncementDetailResponse>.Failure("You are not allowed to update this announcement.");
        }

        var nextPublished = request.PublishedAt ?? announcement.PublishedAt;
        var nextExpires = request.ExpiresAt ?? announcement.ExpiresAt;
        var validation = await ValidateRequestAsync(request.Title ?? announcement.Title, request.Body ?? announcement.Body, nextPublished, nextExpires);
        if (!validation.IsSuccess)
        {
            return Result<AnnouncementDetailResponse>.Failure(validation.Error!);
        }

        var wasPinned = announcement.IsPinned;
        announcement.Title = request.Title?.Trim() ?? announcement.Title;
        announcement.Body = request.Body?.Trim() ?? announcement.Body;
        announcement.Priority = request.Priority ?? announcement.Priority;
        announcement.IsPinned = request.IsPinned ?? announcement.IsPinned;
        announcement.RequiresReadConfirmation = request.RequiresReadConfirmation ?? announcement.RequiresReadConfirmation;
        announcement.PublishedAt = nextPublished;
        announcement.ExpiresAt = nextExpires;

        await auditLogger.LogUserActionAsync(userId, "AnnouncementUpdated", "Announcement", announcement.Id, "Announcement updated.", cancellationToken: cancellationToken);
        if (announcement.IsPinned != wasPinned)
        {
            await auditLogger.LogUserActionAsync(userId, announcement.IsPinned ? "AnnouncementPinned" : "AnnouncementUnpinned", "Announcement", announcement.Id, cancellationToken: cancellationToken);
        }

        await PublishInvalidationAsync(announcement, userId, "updated", cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result<AnnouncementDetailResponse>.Success(ToDetail(announcement, await announcements.HasReadAsync(announcementId, userId, cancellationToken)));
    }

    public async Task<Result> DeleteAsync(Guid announcementId, CancellationToken cancellationToken = default)
    {
        var announcement = await announcements.GetAsync(announcementId, cancellationToken);
        if (announcement is null)
        {
            return Result.Failure("Announcement not found.");
        }

        if (!TryCurrentUser(out var userId) || !await CanManageAnnouncementAsync(userId, announcement, cancellationToken))
        {
            return Result.Failure("You are not allowed to delete this announcement.");
        }

        // Resolve before deletion while the authoritative audience can still
        // be evaluated. The emitted payload contains only an opaque ID.
        var audience = await announcements.ListTargetUsersAsync(announcement, cancellationToken);
        announcement.MarkDeleted(clock.UtcNow);
        await auditLogger.LogUserActionAsync(userId, "AnnouncementDeleted", "Announcement", announcement.Id, "Announcement deleted.", cancellationToken: cancellationToken);
        await invalidations.AnnouncementChangedAsync(announcement, userId, "deleted", audience.Select(target => target.UserId), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result> MarkReadAsync(Guid announcementId, CancellationToken cancellationToken = default)
    {
        if (!TryCurrentUser(out var userId))
        {
            return Result.Failure("Authentication is required.");
        }

        var isSystemAdmin = await IsSystemAdminAsync(userId, cancellationToken);
        if (!await announcements.IsVisibleToUserAsync(announcementId, userId, isSystemAdmin, cancellationToken))
        {
            return Result.Failure("Announcement not found.");
        }

        var announcement = await announcements.GetAsync(announcementId, cancellationToken);
        if (announcement is null || announcement.DeletedAt.HasValue)
        {
            return Result.Failure("Announcement not found.");
        }

        if (!await announcements.HasReadAsync(announcementId, userId, cancellationToken))
        {
            await announcements.AddReadAsync(new AnnouncementRead
            {
                AnnouncementId = announcementId,
                UserId = userId,
                ReadAt = clock.UtcNow
            }, cancellationToken);
            await auditLogger.LogUserActionAsync(userId, "AnnouncementMarkedRead", "Announcement", announcementId, "Announcement marked read.", cancellationToken: cancellationToken);
            await invalidations.AnnouncementChangedAsync(announcement, userId, "readStateChanged", [userId], cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return Result.Success();
    }

    public async Task<Result<AnnouncementReadStatusResponse>> GetReadStatusAsync(Guid announcementId, CancellationToken cancellationToken = default)
    {
        var announcement = await announcements.GetAsync(announcementId, cancellationToken);
        if (announcement is null || announcement.DeletedAt.HasValue)
        {
            return Result<AnnouncementReadStatusResponse>.Failure("Announcement not found.");
        }

        if (!TryCurrentUser(out var userId) || !await CanViewReadStatusAsync(userId, announcement, cancellationToken))
        {
            return Result<AnnouncementReadStatusResponse>.Failure("You are not allowed to view read status.");
        }

        var targets = await announcements.ListTargetUsersAsync(announcement, cancellationToken);
        var unread = targets
            .Where(target => !target.HasRead)
            .Select(target => new AnnouncementUnreadUserResponse(target.UserId, target.DisplayName, target.Email))
            .ToList();

        return Result<AnnouncementReadStatusResponse>.Success(new AnnouncementReadStatusResponse(
            announcement.Id,
            targets.Count,
            targets.Count - unread.Count,
            unread.Count,
            unread));
    }

    public async Task<Result> ResendUnreadAsync(Guid announcementId, CancellationToken cancellationToken = default)
    {
        var status = await GetReadStatusAsync(announcementId, cancellationToken);
        if (!status.IsSuccess)
        {
            return Result.Failure(status.Error!);
        }

        var announcement = await announcements.GetAsync(announcementId, cancellationToken);
        if (announcement is null)
        {
            return Result.Failure("Announcement not found.");
        }

        var actorUserId = currentUser.UserId!.Value;
        await notifications.CreateManyAsync(
            status.Value!.UnreadUsers.Select(user => user.UserId).ToArray(),
            NotificationType.Announcement,
            $"Reminder: {announcement.Title}",
            announcement.Body.Length > 500 ? announcement.Body[..500] : announcement.Body,
            "Announcement",
            announcement.Id,
            actorUserId,
            cancellationToken);
        await auditLogger.LogUserActionAsync(actorUserId, "AnnouncementUnreadReminderResent", "Announcement", announcement.Id, "Unread announcement reminder resent.", cancellationToken: cancellationToken);
        await invalidations.AnnouncementChangedAsync(announcement, actorUserId, "resent", status.Value!.UnreadUsers.Select(user => user.UserId), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    private async Task<Result<AnnouncementScope>> ResolveCreateScopeAsync(Guid userId, CreateAnnouncementRequest request, CancellationToken cancellationToken)
    {
        if (request.ChannelId.HasValue)
        {
            var channel = await channels.GetByIdAsync(request.ChannelId.Value, cancellationToken);
            if (channel is null || channel.DeletedAt.HasValue || !await channelAuthorization.CanManageChannel(userId, channel.Id, cancellationToken))
            {
                return Result<AnnouncementScope>.Failure("You are not allowed to create channel announcements.");
            }

            return Result<AnnouncementScope>.Success(new AnnouncementScope(channel.WorkspaceId, channel.GroupId, channel.Id));
        }

        if (request.GroupId.HasValue)
        {
            var group = await groups.GetByIdAsync(request.GroupId.Value, cancellationToken);
            if (group is null || group.DeletedAt.HasValue || !await AnnouncementScopeAuthorization.CanCreateGroupAsync(groupAuthorization, userId, group.Id, IsTeacherAsync, cancellationToken))
            {
                return Result<AnnouncementScope>.Failure("You are not allowed to create group announcements.");
            }

            return Result<AnnouncementScope>.Success(new AnnouncementScope(group.WorkspaceId, group.Id, null));
        }

        if (request.WorkspaceId.HasValue)
        {
            if (await workspaces.GetByIdAsync(request.WorkspaceId.Value, cancellationToken) is null ||
                !await AnnouncementScopeAuthorization.CanCreateWorkspaceAsync(workspaceAuthorization, userId, request.WorkspaceId.Value, IsTeacherAsync, cancellationToken))
            {
                return Result<AnnouncementScope>.Failure("You are not allowed to create workspace announcements.");
            }

            return Result<AnnouncementScope>.Success(new AnnouncementScope(request.WorkspaceId.Value, null, null));
        }

        return await IsSystemAdminAsync(userId, cancellationToken)
            ? Result<AnnouncementScope>.Success(new AnnouncementScope(null, null, null))
            : Result<AnnouncementScope>.Failure("Only system admins can create global announcements.");
    }

    private async Task<bool> CanManageAnnouncementAsync(Guid userId, Announcement announcement, CancellationToken cancellationToken)
    {
        if (announcement.AuthorUserId == userId || await IsSystemAdminAsync(userId, cancellationToken))
        {
            return true;
        }

        if (announcement.ChannelId.HasValue)
        {
            return await channelAuthorization.CanManageChannel(userId, announcement.ChannelId.Value, cancellationToken);
        }

        if (announcement.GroupId.HasValue)
        {
            return await AnnouncementScopeAuthorization.CanCreateGroupAsync(
                groupAuthorization,
                userId,
                announcement.GroupId.Value,
                IsTeacherAsync,
                cancellationToken);
        }

        if (announcement.WorkspaceId.HasValue)
        {
            return await AnnouncementScopeAuthorization.CanCreateWorkspaceAsync(
                workspaceAuthorization,
                userId,
                announcement.WorkspaceId.Value,
                IsTeacherAsync,
                cancellationToken);
        }

        return false;
    }

    private async Task<bool> CanViewReadStatusAsync(Guid userId, Announcement announcement, CancellationToken cancellationToken)
    {
        return announcement.AuthorUserId == userId ||
            await IsSystemAdminAsync(userId, cancellationToken) ||
            await CanManageAnnouncementAsync(userId, announcement, cancellationToken);
    }

    private async Task<bool> IsSystemAdminAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await users.GetByIdAsync(userId, cancellationToken);
        return user is { Status: UserStatus.Active, SystemRole: SystemRole.SystemAdmin };
    }

    private async Task<bool> IsTeacherAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await users.GetByIdAsync(userId, cancellationToken);
        return user is { Status: UserStatus.Active, SystemRole: SystemRole.Teacher or SystemRole.Admin or SystemRole.SystemAdmin };
    }

    private static Task<Result> ValidateRequestAsync(string title, string body, DateTimeOffset publishedAt, DateTimeOffset? expiresAt)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return Task.FromResult(Result.Failure("Announcement title is required."));
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return Task.FromResult(Result.Failure("Announcement body is required."));
        }

        if (expiresAt.HasValue && expiresAt.Value <= publishedAt)
        {
            return Task.FromResult(Result.Failure("Announcement expiration must be after publication."));
        }

        return Task.FromResult(Result.Success());
    }

    private static int NormalizePage(int requestedPage, int pageSize)
    {
        var maxPage = Math.Min(
            2_147_483_647L,
            (2_147_483_647L / pageSize) + 1L);
        return (int)Math.Clamp(requestedPage, 1L, maxPage);
    }

    private bool TryCurrentUser(out Guid userId)
    {
        if (currentUser is { IsAuthenticated: true, UserId: { } authenticatedUserId })
        {
            userId = authenticatedUserId;
            return true;
        }

        userId = Guid.Empty;
        return false;
    }

    private async Task PublishInvalidationAsync(Announcement announcement, Guid actorUserId, string change, CancellationToken cancellationToken)
    {
        var audience = await announcements.ListTargetUsersAsync(announcement, cancellationToken);
        await invalidations.AnnouncementChangedAsync(announcement, actorUserId, change, audience.Select(target => target.UserId), cancellationToken);
    }

    private static AnnouncementListItemResponse ToListItem(Announcement announcement, bool isRead)
    {
        return new AnnouncementListItemResponse(
            announcement.Id,
            announcement.WorkspaceId,
            announcement.GroupId,
            announcement.ChannelId,
            announcement.AuthorUserId,
            announcement.Title,
            announcement.Priority,
            announcement.IsPinned,
            announcement.RequiresReadConfirmation,
            isRead,
            announcement.PublishedAt,
            announcement.ExpiresAt);
    }

    private static AnnouncementDetailResponse ToDetail(Announcement announcement, bool isRead)
    {
        return new AnnouncementDetailResponse(
            announcement.Id,
            announcement.WorkspaceId,
            announcement.GroupId,
            announcement.ChannelId,
            announcement.AuthorUserId,
            announcement.Title,
            announcement.Body,
            announcement.Priority,
            announcement.IsPinned,
            announcement.RequiresReadConfirmation,
            isRead,
            announcement.PublishedAt,
            announcement.ExpiresAt,
            announcement.CreatedAt,
            announcement.UpdatedAt);
    }

    private sealed record AnnouncementScope(Guid? WorkspaceId, Guid? GroupId, Guid? ChannelId);
}
