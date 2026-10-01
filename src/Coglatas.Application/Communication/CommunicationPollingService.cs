using System.Collections.Concurrent;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Messaging;
using Coglatas.Application.Notifications;
using Coglatas.Application.Projects;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;

namespace Coglatas.Application.Communication;

public sealed class CommunicationPollingService(
    IMessagingRepository messaging,
    INotificationService notifications,
    IConversationAuthorizationService conversationAuthorization,
    IProjectAuthorizationService projectAuthorization,
    ICurrentUser currentUser,
    ICurrentTenant currentTenant,
    IClock clock,
    IAuditLogger auditLogger,
    IUnitOfWork unitOfWork) : ICommunicationPollingService
{
    private const int MaxPageSize = 100;
    private const int MaxBurstPerMinute = 120;
    private static readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> PollingWindows = new();

    public async Task<Result<ConversationUnreadPollingResponse>> GetUnreadCountsAsync(CommunicationPollingQuery query, CancellationToken cancellationToken = default)
    {
        if (!TryCurrentUser(out var userId))
        {
            return Result<ConversationUnreadPollingResponse>.Failure("Authentication is required.");
        }

        if (!await AllowPollingAsync(userId, "unread_counts", query.WorkspaceId))
        {
            await AuditPollingAsync(userId, "unread_counts", "rate_limited", "rate_limited", query.WorkspaceId, 0, cancellationToken);
            return Result<ConversationUnreadPollingResponse>.Failure("Polling rate limit exceeded.");
        }

        var page = SafePage(query.Page);
        var pageSize = SafePageSize(query.PageSize);
        var conversations = await messaging.ListForUserAsync(userId, page, pageSize, cancellationToken);
        var items = new List<ConversationUnreadPollingItem>();
        var scopedConversations = conversations.Items
            .Where(item => WorkspaceMatches(item, query.WorkspaceId))
            .ToList();
        var readableIds = await messaging.FilterReadableConversationIdsAsync(
            userId,
            scopedConversations.Select(conversation => conversation.Id).ToArray(),
            cancellationToken);

        foreach (var conversation in scopedConversations)
        {
            var item = await BuildUnreadItemAsync(
                userId,
                conversation,
                readableIds.Contains(conversation.Id),
                cancellationToken);
            if (item is not null)
            {
                items.Add(item);
            }
        }

        return Result<ConversationUnreadPollingResponse>.Success(new ConversationUnreadPollingResponse(items, page, pageSize, conversations.TotalCount, clock.UtcNow));
    }

    public async Task<Result<NotificationPollingResponse>> GetNotificationsAsync(CommunicationPollingQuery query, CancellationToken cancellationToken = default)
    {
        if (!TryCurrentUser(out var userId))
        {
            return Result<NotificationPollingResponse>.Failure("Authentication is required.");
        }

        if (!await AllowPollingAsync(userId, "notifications", query.WorkspaceId))
        {
            await AuditPollingAsync(userId, "notifications", "rate_limited", "rate_limited", query.WorkspaceId, 0, cancellationToken);
            return Result<NotificationPollingResponse>.Failure("Polling rate limit exceeded.");
        }

        var page = SafePage(query.Page);
        var pageSize = SafePageSize(query.PageSize);
        var rawPage = await notifications.ListAsync(userId, page, pageSize, cancellationToken);
        var items = new List<NotificationPollingItem>();

        foreach (var notification in rawPage.Items)
        {
            var shaped = await ShapeNotificationAsync(userId, notification, query.WorkspaceId, cancellationToken);
            if (shaped is not null)
            {
                items.Add(shaped);
            }
        }

        return Result<NotificationPollingResponse>.Success(new NotificationPollingResponse(items, page, pageSize, rawPage.TotalCount, clock.UtcNow));
    }

    public async Task<Result<CommunicationUpdatesPollingResponse>> GetUpdatesAsync(CommunicationUpdatesPollingQuery query, CancellationToken cancellationToken = default)
    {
        if (!TryCurrentUser(out var userId))
        {
            return Result<CommunicationUpdatesPollingResponse>.Failure("Authentication is required.");
        }

        if (!await AllowPollingAsync(userId, "updates", query.WorkspaceId))
        {
            await AuditPollingAsync(userId, "updates", "rate_limited", "rate_limited", query.WorkspaceId, 0, cancellationToken);
            return Result<CommunicationUpdatesPollingResponse>.Failure("Polling rate limit exceeded.");
        }

        var cursorResult = await ResolveCursorAsync(userId, query, cancellationToken);
        if (!cursorResult.IsSuccess)
        {
            return Result<CommunicationUpdatesPollingResponse>.Failure(cursorResult.Error!);
        }

        var since = cursorResult.Value!.Since;
        var page = SafePage(query.Page);
        var pageSize = SafePageSize(query.PageSize);
        var conversations = await messaging.ListForUserAsync(userId, page, pageSize, cancellationToken);
        var updates = new List<CommunicationUpdatePollingItem>();
        var scopedConversations = conversations.Items
            .Where(item => WorkspaceMatches(item, query.WorkspaceId))
            .ToList();
        var readableIds = await messaging.FilterReadableConversationIdsAsync(
            userId,
            scopedConversations.Select(conversation => conversation.Id).ToArray(),
            cancellationToken);

        foreach (var conversation in scopedConversations)
        {
            var updatedAt = conversation.UpdatedAt ?? conversation.CreatedAt;
            if (updatedAt <= since)
            {
                continue;
            }

            var unread = await BuildUnreadItemAsync(
                userId,
                conversation,
                readableIds.Contains(conversation.Id),
                cancellationToken);
            if (unread is not null)
            {
                updates.Add(new CommunicationUpdatePollingItem("conversation", conversation.Id, null, unread.UnreadCount, updatedAt));
            }
        }

        var notificationPage = await notifications.ListAsync(userId, 1, pageSize, cancellationToken);
        foreach (var notification in notificationPage.Items.Where(item => item.CreatedAt > since))
        {
            var shaped = await ShapeNotificationAsync(userId, notification, query.WorkspaceId, cancellationToken);
            if (shaped is not null)
            {
                updates.Add(new CommunicationUpdatePollingItem("notification", shaped.ConversationId, notification.Id, null, notification.CreatedAt));
            }
        }

        var cursorTo = updates.Count == 0 ? clock.UtcNow : updates.Max(item => item.UpdatedAt);
        var nextCursor = new CommunicationPollingCursor(userId, CurrentTenantIdOrNull(), query.WorkspaceId, cursorTo).Encode();
        return Result<CommunicationUpdatesPollingResponse>.Success(new CommunicationUpdatesPollingResponse(
            updates.OrderBy(item => item.UpdatedAt).Take(pageSize).ToList(),
            since,
            cursorTo,
            nextCursor,
            page,
            pageSize,
            cursorResult.Value.FullRefresh,
            clock.UtcNow));
    }

    private async Task<ConversationUnreadPollingItem?> BuildUnreadItemAsync(
        Guid userId,
        Conversation conversation,
        bool isCurrentlyReadable,
        CancellationToken cancellationToken)
    {
        if (!IsSupportedMvpType(conversation.Type) ||
            !isCurrentlyReadable)
        {
            await AuditPollingAsync(userId, "unread_counts", "deny", "participant_missing", conversation.WorkspaceId, 0, cancellationToken);
            return null;
        }

        var member = await messaging.GetMemberAsync(conversation.Id, userId, cancellationToken);
        if (!IsActiveParticipant(member))
        {
            await AuditPollingAsync(userId, "unread_counts", "deny", "participant_removed", conversation.WorkspaceId, 0, cancellationToken);
            return null;
        }

        var latestPage = await messaging.ListMessagesAsync(conversation.Id, 1, null, cancellationToken);
        var latest = latestPage.Items.FirstOrDefault();
        var unread = await messaging.CountUnreadMessagesAsync(conversation.Id, userId, member!.LastReadAt, cancellationToken);
        return new ConversationUnreadPollingItem(
            conversation.Id,
            conversation.Type,
            unread,
            unread > 0,
            latest?.CreatedAt,
            latest?.Id,
            conversation.UpdatedAt ?? conversation.CreatedAt,
            member.IsMuted,
            member.IsArchived);
    }

    private async Task<NotificationPollingItem?> ShapeNotificationAsync(
        Guid userId,
        NotificationListItemResponse notification,
        Guid? workspaceId,
        CancellationToken cancellationToken)
    {
        if (notification.UserId != userId)
        {
            await AuditPollingAsync(userId, "notifications", "deny", "recipient_mismatch", workspaceId, 0, cancellationToken);
            return null;
        }

        var access = await AuthorizeNotificationTargetAsync(userId, notification.RelatedEntityType, notification.RelatedEntityId, cancellationToken);
        if (!access.IsVisible)
        {
            await AuditPollingAsync(userId, "notifications", "deny", access.ReasonCode, workspaceId, 0, cancellationToken);
            return new NotificationPollingItem(
                notification.Id,
                notification.NotificationType,
                "Inaccessible",
                null,
                "Notification unavailable",
                "Related item is unavailable.",
                notification.CreatedAt,
                notification.IsRead,
                notification.ReadAt,
                null,
                null);
        }

        if (workspaceId.HasValue && access.WorkspaceId.HasValue && access.WorkspaceId.Value != workspaceId.Value)
        {
            await AuditPollingAsync(userId, "notifications", "deny", "workspace_mismatch", workspaceId, 0, cancellationToken);
            return null;
        }

        return new NotificationPollingItem(
            notification.Id,
            notification.NotificationType,
            access.TargetType,
            access.TargetId,
            SafeTitle(notification.NotificationType, notification.Title),
            SafeSummary(notification.NotificationType),
            notification.CreatedAt,
            notification.IsRead,
            notification.ReadAt,
            access.ConversationId,
            access.ProjectId);
    }

    private async Task<NotificationTargetAccess> AuthorizeNotificationTargetAsync(Guid userId, string? targetType, Guid? targetId, CancellationToken cancellationToken)
    {
        if (!targetId.HasValue || string.IsNullOrWhiteSpace(targetType))
        {
            return NotificationTargetAccess.Visible("None", null, null, null, null);
        }

        if (targetType.Equals("Message", StringComparison.OrdinalIgnoreCase))
        {
            var message = await messaging.GetMessageAsync(targetId.Value, cancellationToken);
            if (message is null || message.DeletedAt.HasValue)
            {
                return NotificationTargetAccess.Hidden("notification_target_hidden");
            }

            if (!await conversationAuthorization.CanViewConversation(userId, message.ConversationId, cancellationToken))
            {
                return NotificationTargetAccess.Hidden("dm_non_participant");
            }

            return NotificationTargetAccess.Visible("Message", targetId, message.ConversationId, message.WorkspaceId, null);
        }

        if (targetType.Equals("Conversation", StringComparison.OrdinalIgnoreCase))
        {
            var conversation = await messaging.GetConversationAsync(targetId.Value, cancellationToken);
            if (conversation is null || !await conversationAuthorization.CanViewConversation(userId, targetId.Value, cancellationToken))
            {
                return NotificationTargetAccess.Hidden("participant_missing");
            }

            return NotificationTargetAccess.Visible("Conversation", targetId, conversation.Id, conversation.WorkspaceId, conversation.ProjectId);
        }

        if (targetType.Equals("Project", StringComparison.OrdinalIgnoreCase))
        {
            if (!await projectAuthorization.CanViewProject(userId, targetId.Value, cancellationToken))
            {
                return NotificationTargetAccess.Hidden("target_access_denied");
            }

            return NotificationTargetAccess.Visible("Project", targetId, null, null, targetId);
        }

        if (targetType.Contains("StudentRecord", StringComparison.OrdinalIgnoreCase))
        {
            return NotificationTargetAccess.Hidden("field_access_denied");
        }

        if (targetType.Contains("File", StringComparison.OrdinalIgnoreCase) ||
            targetType.Contains("Export", StringComparison.OrdinalIgnoreCase) ||
            targetType.Contains("Attachment", StringComparison.OrdinalIgnoreCase))
        {
            return NotificationTargetAccess.Hidden("target_access_denied");
        }

        return NotificationTargetAccess.Hidden("notification_target_hidden");
    }

    private async Task<Result<CursorResolution>> ResolveCursorAsync(Guid userId, CommunicationUpdatesPollingQuery query, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(query.Cursor))
        {
            if (!CommunicationPollingCursor.TryDecode(query.Cursor, out var cursor) || cursor is null)
            {
                await AuditPollingAsync(userId, "updates", "deny", "cursor_invalid", query.WorkspaceId, 0, cancellationToken);
                return Result<CursorResolution>.Failure("Invalid polling cursor.");
            }

            if (cursor.ActorUserId != userId)
            {
                await AuditPollingAsync(userId, "updates", "deny", "cursor_scope_mismatch", query.WorkspaceId, 0, cancellationToken);
                return Result<CursorResolution>.Failure("Invalid polling cursor.");
            }

            if (cursor.TenantId != CurrentTenantIdOrNull() || cursor.WorkspaceId != query.WorkspaceId)
            {
                await AuditPollingAsync(userId, "updates", "deny", "cursor_scope_mismatch", query.WorkspaceId, 0, cancellationToken);
                return Result<CursorResolution>.Failure("Invalid polling cursor.");
            }

            return Result<CursorResolution>.Success(new CursorResolution(cursor.Since, false));
        }

        var since = query.Since ?? clock.UtcNow.AddMinutes(-15);
        var oldestAllowed = clock.UtcNow.AddDays(-7);
        if (since < oldestAllowed)
        {
            return Result<CursorResolution>.Success(new CursorResolution(oldestAllowed, true));
        }

        return Result<CursorResolution>.Success(new CursorResolution(since, false));
    }

    private async Task<bool> AllowPollingAsync(Guid userId, string operation, Guid? workspaceId)
    {
        var now = clock.UtcNow;
        var key = $"{CurrentTenantIdOrNull()}:{workspaceId}:{userId}:{operation}";
        var queue = PollingWindows.GetOrAdd(key, _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() > TimeSpan.FromMinutes(1))
            {
                queue.Dequeue();
            }

            if (queue.Count >= MaxBurstPerMinute)
            {
                return false;
            }

            queue.Enqueue(now);
            return true;
        }
    }

    private async Task AuditPollingAsync(Guid actorUserId, string operation, string decision, string reasonCode, Guid? workspaceId, int returnedCount, CancellationToken cancellationToken)
    {
        await auditLogger.LogAsync(new AuditLogEntry(
            actorUserId,
            "CommunicationPolling",
            "CommunicationPoll",
            null,
            "Communication polling request processed.",
            // The requested Workspace scope is untrusted until the individual
            // Conversation/Notification authorization checks have completed.
            // Persisting it in the indexed foreign-key column made a missing or
            // cross-Tenant UUID turn an otherwise redacted polling response into
            // a database exception. Keep the opaque requested value only in the
            // audit metadata; no resource relation is claimed by this event.
            WorkspaceId: null,
            Metadata: new Dictionary<string, object?>
            {
                ["operation"] = operation,
                ["decision"] = decision,
                ["reasonCode"] = reasonCode,
                ["returnedCount"] = returnedCount,
                ["tenantId"] = CurrentTenantIdOrNull(),
                ["workspaceId"] = workspaceId
            }),
            cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private bool TryCurrentUser(out Guid userId)
    {
        userId = currentUser.UserId ?? Guid.Empty;
        return currentUser.IsAuthenticated && currentUser.UserId.HasValue;
    }

    private Guid? CurrentTenantIdOrNull()
    {
        return currentTenant.IsAvailable && !currentTenant.IsPlatformScope ? currentTenant.TenantId : null;
    }

    private static int SafePage(int page) => Math.Max(1, page);
    private static int SafePageSize(int pageSize) => Math.Clamp(pageSize, 1, MaxPageSize);

    private static bool WorkspaceMatches(Conversation conversation, Guid? workspaceId)
    {
        return !workspaceId.HasValue || conversation.WorkspaceId == workspaceId.Value;
    }

    private static bool IsSupportedMvpType(ConversationType type)
    {
        return type is ConversationType.DirectMessage or ConversationType.ProjectChannel or ConversationType.Thread;
    }

    private static bool IsActiveParticipant(ConversationMember? member)
    {
        return member is { LeftAt: null, RemovedAt: null, CanRead: true };
    }

    private static string SafeTitle(NotificationType type, string title)
    {
        if (type is NotificationType.DirectMessage or NotificationType.Message)
        {
            return "New message";
        }

        return string.IsNullOrWhiteSpace(title) ? "Notification" : title.Trim();
    }

    private static string SafeSummary(NotificationType type)
    {
        return type switch
        {
            NotificationType.DirectMessage or NotificationType.Message => "A conversation has new activity.",
            NotificationType.ArtifactUploaded => "A file-related item changed.",
            NotificationType.TaskAssigned or NotificationType.TaskDueSoon or NotificationType.TaskStatusChanged => "A task-related item changed.",
            _ => "Open the related item to view details."
        };
    }

    private sealed record CursorResolution(DateTimeOffset Since, bool FullRefresh);

    private sealed record NotificationTargetAccess(
        bool IsVisible,
        string ReasonCode,
        string TargetType,
        Guid? TargetId,
        Guid? ConversationId,
        Guid? WorkspaceId,
        Guid? ProjectId)
    {
        public static NotificationTargetAccess Visible(string targetType, Guid? targetId, Guid? conversationId, Guid? workspaceId, Guid? projectId)
        {
            return new NotificationTargetAccess(true, "target_access_allowed", targetType, targetId, conversationId, workspaceId, projectId);
        }

        public static NotificationTargetAccess Hidden(string reasonCode)
        {
            return new NotificationTargetAccess(false, reasonCode, "Inaccessible", null, null, null, null);
        }
    }
}
