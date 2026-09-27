using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Notifications;
using Coglatas.Application.Realtime;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Npgsql;

namespace Coglatas.Infrastructure.Persistence;

public sealed class DbNotificationService(
    AppDbContext dbContext,
    IClock clock,
    ICurrentTenant currentTenant,
    ITransactionalOutbox? outbox = null,
    INotificationTargetResolver? targets = null) : INotificationService
{
    private const string NotificationUserStateIdentityIndex = "IX_notification_user_states_TenantId_UserId";
    private const int AuthorizationScanBatchSize = 100;

    public async Task<Guid> StageTaskDeadlineDigestByLogicalKeyAsync(
        Guid userId,
        Guid digestJobId,
        string logicalKey,
        CancellationToken cancellationToken = default)
    {
        if (!currentTenant.IsAvailable)
        {
            throw new InvalidOperationException("A tenant scope is required to stage a Task deadline digest.");
        }

        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A notification recipient is required.", nameof(userId));
        }

        if (digestJobId == Guid.Empty)
        {
            throw new ArgumentException("A digest job reference is required.", nameof(digestJobId));
        }

        var normalizedLogicalKey = NormalizeLogicalKey(logicalKey);
        var existing = await FindExistingLogicalNotificationIdAsync(
            userId,
            normalizedLogicalKey,
            cancellationToken);
        if (existing.HasValue)
        {
            return existing.Value;
        }

        var now = clock.UtcNow;
        var state = await GetOrCreateUserStateAsync(userId, now, cancellationToken);
        var stateVersion = AdvanceState(state, now);
        var notification = new Notification
        {
            TenantId = currentTenant.TenantId,
            UserId = userId,
            LogicalKey = normalizedLogicalKey,
            NotificationType = NotificationType.TaskDueSoon,
            Title = TaskDeadlineDigestPolicy.NotificationTitle,
            Body = null,
            RelatedEntityType = TaskDeadlineDigestPolicy.RelatedEntityType,
            RelatedEntityId = digestJobId,
            CreatedAt = now,
            StateVersion = stateVersion
        };

        await dbContext.Notifications.AddAsync(notification, cancellationToken);
        await EnqueueTaskCreatedAsync(notification, stateVersion, cancellationToken);
        return notification.Id;
    }

    public async Task<Guid> StageTaskByLogicalKeyAsync(
        Guid userId,
        NotificationType type,
        string title,
        Guid taskId,
        string logicalKey,
        CancellationToken cancellationToken = default)
    {
        if (!currentTenant.IsAvailable)
        {
            throw new InvalidOperationException("A tenant scope is required to stage a task notification.");
        }

        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A notification recipient is required.", nameof(userId));
        }

        if (taskId == Guid.Empty)
        {
            throw new ArgumentException("A task reference is required.", nameof(taskId));
        }

        var normalizedTitle = title.Trim();
        if (normalizedTitle.Length == 0)
        {
            throw new ArgumentException("A notification title is required.", nameof(title));
        }

        var normalizedLogicalKey = NormalizeLogicalKey(logicalKey);
        var existing = await FindExistingLogicalNotificationIdAsync(
            userId,
            normalizedLogicalKey,
            cancellationToken);
        if (existing.HasValue)
        {
            // Logical identity includes soft-deleted rows. A retry must not
            // resurrect the notification or emit another realtime signal.
            return existing.Value;
        }

        var now = clock.UtcNow;
        var state = await GetOrCreateUserStateAsync(userId, now, cancellationToken);
        var stateVersion = AdvanceState(state, now);
        var notification = new Notification
        {
            TenantId = currentTenant.TenantId,
            UserId = userId,
            LogicalKey = normalizedLogicalKey,
            NotificationType = type,
            Title = normalizedTitle,
            Body = null,
            RelatedEntityType = "TaskItem",
            RelatedEntityId = taskId,
            CreatedAt = now,
            StateVersion = stateVersion
        };

        await dbContext.Notifications.AddAsync(notification, cancellationToken);
        await EnqueueTaskCreatedAsync(notification, stateVersion, cancellationToken);
        return notification.Id;
    }

    public async Task<Guid> CreateOrGetByLogicalKeyAsync(
        Guid userId,
        NotificationType type,
        string title,
        string? body,
        string? relatedEntityType,
        Guid? relatedEntityId,
        string logicalKey,
        CancellationToken cancellationToken = default)
    {
        if (!currentTenant.IsAvailable)
        {
            throw new InvalidOperationException("A tenant scope is required to create a logical notification.");
        }

        var normalizedLogicalKey = NormalizeLogicalKey(logicalKey);
        var existing = await FindExistingLogicalNotificationIdAsync(
            userId,
            normalizedLogicalKey,
            cancellationToken);
        if (existing.HasValue)
        {
            // This intentionally includes soft-deleted rows. A recipient's
            // deletion must not let an Outbox replay resurrect the same event.
            return existing.Value;
        }

        // The first ever notification for a recipient may also race on the
        // NotificationUserState identity. Retry that narrow setup race once;
        // a logical-key unique violation itself must always re-read a row.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var originalEntries = CaptureTrackedEntries();
            var now = clock.UtcNow;
            var state = await GetOrCreateUserStateAsync(userId, now, cancellationToken);
            var stateVersion = AdvanceState(state, now);
            var notification = new Notification
            {
                TenantId = currentTenant.TenantId,
                UserId = userId,
                LogicalKey = normalizedLogicalKey,
                NotificationType = type,
                Title = title.Trim(),
                Body = string.IsNullOrWhiteSpace(body) ? null : body.Trim(),
                RelatedEntityType = string.IsNullOrWhiteSpace(relatedEntityType) ? null : relatedEntityType.Trim(),
                RelatedEntityId = relatedEntityId,
                CreatedAt = now,
                StateVersion = stateVersion
            };

            await dbContext.Notifications.AddAsync(notification, cancellationToken);
            await EnqueueCreatedAsync(notification, stateVersion, cancellationToken);

            try
            {
                // This makes the unique index authoritative for this explicit
                // create-or-get primitive. A future business mutation must run
                // it inside the caller-owned transaction to retain atomicity.
                await dbContext.SaveChangesAsync(cancellationToken);
                return notification.Id;
            }
            catch (DbUpdateException exception) when (TryGetRetriableUniqueConflict(exception, out var isLogicalKeyConflict))
            {
                RestoreTrackedEntries(originalEntries);

                existing = await FindLogicalNotificationAsync(userId, normalizedLogicalKey, cancellationToken);
                if (existing.HasValue)
                {
                    return existing.Value;
                }

                if (!isLogicalKeyConflict && attempt == 0)
                {
                    continue;
                }

                // A matching logical-key violation without a safely readable
                // row is not success. Preserve the original database failure.
                throw;
            }
        }

        throw new InvalidOperationException("The notification logical identity could not be persisted.");
    }

    public async Task<Guid> CreateAsync(
        Guid userId,
        NotificationType type,
        string title,
        string? body,
        string? relatedEntityType,
        Guid? relatedEntityId,
        CancellationToken cancellationToken = default)
    {
        // Deduplication must compare the exact representation that the domain
        // entity will persist; otherwise an over-limit title is stored clamped
        // but retried later with the longer source text and is not found.
        var normalizedTitle = Notification.NormalizeTitle(title.Trim());
        var existing = await dbContext.Notifications
            .Where(notification =>
                notification.UserId == userId &&
                notification.NotificationType == type &&
                notification.RelatedEntityType == relatedEntityType &&
                notification.RelatedEntityId == relatedEntityId &&
                notification.Title == normalizedTitle &&
                notification.DeletedAt == null)
            .OrderByDescending(notification => notification.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is not null)
        {
            return existing.Id;
        }

        var now = clock.UtcNow;
        var state = await GetOrCreateUserStateAsync(userId, now, cancellationToken);
        var stateVersion = AdvanceState(state, now);
        var notification = new Notification
        {
            UserId = userId,
            NotificationType = type,
            Title = normalizedTitle,
            Body = string.IsNullOrWhiteSpace(body) ? null : body.Trim(),
            RelatedEntityType = string.IsNullOrWhiteSpace(relatedEntityType) ? null : relatedEntityType.Trim(),
            RelatedEntityId = relatedEntityId,
            CreatedAt = now,
            StateVersion = stateVersion
        };

        await dbContext.Notifications.AddAsync(notification, cancellationToken);
        await EnqueueCreatedAsync(notification, stateVersion, cancellationToken);
        return notification.Id;
    }

    public async Task<IReadOnlyList<Guid>> CreateManyAsync(
        IReadOnlyCollection<Guid> userIds,
        NotificationType type,
        string title,
        string? body,
        string? relatedEntityType,
        Guid? relatedEntityId,
        Guid? actorUserId = null,
        CancellationToken cancellationToken = default)
    {
        var ids = new List<Guid>();
        foreach (var userId in userIds.Distinct().Where(userId => userId != actorUserId))
        {
            ids.Add(await CreateAsync(userId, type, title, body, relatedEntityType, relatedEntityId, cancellationToken));
        }

        return ids;
    }

    public async Task<bool> MarkAsReadAsync(Guid userId, Guid notificationId, CancellationToken cancellationToken = default)
    {
        var notification = await dbContext.Notifications.FirstOrDefaultAsync(item =>
            item.Id == notificationId &&
            item.UserId == userId &&
            item.DeletedAt == null,
            cancellationToken);
        if (notification is null)
        {
            return false;
        }

        if (!await IsCurrentlyVisibleAsync(notification, userId, cancellationToken))
        {
            return false;
        }

        if (!notification.IsRead)
        {
            var resultingUnreadCount = Math.Max(
                0,
                await GetUnreadCountAsync(userId, cancellationToken) - 1);
            var now = clock.UtcNow;
            var stateVersion = AdvanceState(await GetOrCreateUserStateAsync(userId, now, cancellationToken), now);
            notification.IsRead = true;
            notification.ReadAt = now;
            notification.StateVersion = stateVersion;
            await EnqueueReadStateChangeAsync(
                userId,
                notification.Id,
                "read",
                stateVersion,
                now,
                resultingUnreadCount,
                cancellationToken);
        }

        return true;
    }

    public async Task<int> MarkAllAsReadAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var unread = await dbContext.Notifications
            .Where(notification => notification.UserId == userId && !notification.IsRead && notification.DeletedAt == null)
            .ToListAsync(cancellationToken);

        if (unread.Count == 0)
        {
            return 0;
        }

        if (targets is not null)
        {
            var visibleUnread = new List<Notification>(unread.Count);
            for (var offset = 0; offset < unread.Count; offset += AuthorizationScanBatchSize)
            {
                var batch = unread.Skip(offset).Take(AuthorizationScanBatchSize).ToArray();
                var visibleIds = await FilterCurrentlyVisibleIdsAsync(batch, userId, cancellationToken);
                visibleUnread.AddRange(batch.Where(notification => visibleIds.Contains(notification.Id)));
            }

            unread = visibleUnread;
            if (unread.Count == 0)
            {
                return 0;
            }
        }

        var now = clock.UtcNow;
        var stateVersion = AdvanceState(await GetOrCreateUserStateAsync(userId, now, cancellationToken), now);
        foreach (var notification in unread)
        {
            notification.IsRead = true;
            notification.ReadAt = now;
            notification.StateVersion = stateVersion;
        }

        await EnqueueReadStateChangeAsync(userId, null, "allRead", stateVersion, now, 0, cancellationToken);

        return unread.Count;
    }

    public async Task<int> GetUnreadCountAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var query = dbContext.Notifications
            .AsNoTracking()
            .Where(notification => notification.UserId == userId && !notification.IsRead && notification.DeletedAt == null)
            .OrderByDescending(notification => notification.CreatedAt)
            .ThenByDescending(notification => notification.Id);
        if (targets is null)
        {
            return await query.CountAsync(cancellationToken);
        }

        return await CountCurrentlyVisibleAsync(query, userId, cancellationToken);
    }

    public async Task<PagedResponse<NotificationListItemResponse>> ListAsync(Guid userId, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var query = dbContext.Notifications
            .AsNoTracking()
            .Where(notification => notification.UserId == userId && notification.DeletedAt == null)
            .OrderByDescending(notification => notification.CreatedAt)
            .ThenByDescending(notification => notification.Id);

        if (targets is null)
        {
            var total = await query.CountAsync(cancellationToken);
            var notifications = await query
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(cancellationToken);
            var items = notifications.Select(ToListItem).ToList();

            return new PagedResponse<NotificationListItemResponse>(items, page, pageSize, total);
        }

        return await ListCurrentlyVisibleAsync(query, userId, page, pageSize, cancellationToken);
    }

    public async Task<bool> DeleteAsync(Guid userId, Guid notificationId, DateTimeOffset deletedAt, CancellationToken cancellationToken = default)
    {
        var notification = await dbContext.Notifications.FirstOrDefaultAsync(item =>
            item.Id == notificationId &&
            item.UserId == userId &&
            item.DeletedAt == null,
            cancellationToken);
        if (notification is null)
        {
            return false;
        }

        if (!await IsCurrentlyVisibleAsync(notification, userId, cancellationToken))
        {
            return false;
        }

        var resultingUnreadCount = await GetUnreadCountAsync(userId, cancellationToken);
        if (!notification.IsRead)
        {
            resultingUnreadCount = Math.Max(0, resultingUnreadCount - 1);
        }
        var stateVersion = AdvanceState(await GetOrCreateUserStateAsync(userId, deletedAt, cancellationToken), deletedAt);
        notification.DeletedAt = deletedAt;
        notification.StateVersion = stateVersion;
        await EnqueueReadStateChangeAsync(
            userId,
            notification.Id,
            "deleted",
            stateVersion,
            deletedAt,
            resultingUnreadCount,
            cancellationToken);
        return true;
    }

    public Task NotifyAsync(Guid recipientUserId, string title, string? body, string sourceType, Guid sourceId, CancellationToken cancellationToken = default)
    {
        return CreateAsync(recipientUserId, GuessType(title, sourceType), title, body, sourceType, sourceId, cancellationToken);
    }

    private static NotificationType GuessType(string title, string sourceType)
    {
        if (string.Equals(sourceType, "Message", StringComparison.OrdinalIgnoreCase))
        {
            return NotificationType.DirectMessage;
        }

        if (string.Equals(sourceType, "TaskItem", StringComparison.OrdinalIgnoreCase))
        {
            return title.Contains("due", StringComparison.OrdinalIgnoreCase)
                ? NotificationType.TaskDueSoon
                : NotificationType.TaskStatusChanged;
        }

        if (string.Equals(sourceType, "Artifact", StringComparison.OrdinalIgnoreCase))
        {
            return NotificationType.ArtifactUploaded;
        }

        if (string.Equals(sourceType, "ActivityEvent", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(sourceType, "Event", StringComparison.OrdinalIgnoreCase))
        {
            return NotificationType.Event;
        }

        if (string.Equals(sourceType, "Feedback", StringComparison.OrdinalIgnoreCase))
        {
            return NotificationType.FeedbackCreated;
        }

        return NotificationType.System;
    }

    private static string? BuildTargetRoute(string? relatedEntityType, Guid? relatedEntityId)
    {
        if (!relatedEntityId.HasValue || string.IsNullOrWhiteSpace(relatedEntityType))
        {
            return null;
        }

        return relatedEntityType switch
        {
            "Announcement" => $"/announcements/{relatedEntityId}",
            "ActivityEvent" or "Event" => $"/events/{relatedEntityId}",
            "InternalForm" or "Form" => $"/forms/{relatedEntityId}",
            "Project" => $"/projects/{relatedEntityId}",
            // Task and digest routes are resolved only by notification-open
            // against current Task/Project/Workspace authorization. A list
            // response must never preserve the obsolete /tasks/{id} route as
            // navigation authority. Artifact and Message list routes remain
            // usable only because the list itself is current-target filtered.
            "TaskItem" or "Task" or TaskDeadlineDigestPolicy.RelatedEntityType => null,
            "Artifact" => $"/artifacts/{relatedEntityId}",
            "Message" => $"/messages/{relatedEntityId}",
            "Post" => $"/posts/{relatedEntityId}",
            _ => null
        };
    }

    private async Task<PagedResponse<NotificationListItemResponse>> ListCurrentlyVisibleAsync(
        IQueryable<Notification> query,
        Guid userId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var items = new List<NotificationListItemResponse>(pageSize);
        var firstRequestedVisibleIndex = checked((page - 1) * pageSize);
        var visibleCount = 0;
        var rawOffset = 0;

        while (true)
        {
            var candidates = await query
                .Skip(rawOffset)
                .Take(AuthorizationScanBatchSize)
                .ToListAsync(cancellationToken);
            if (candidates.Count == 0)
            {
                break;
            }

            var visibleIds = await FilterCurrentlyVisibleIdsAsync(candidates, userId, cancellationToken);

            foreach (var notification in candidates)
            {
                if (!visibleIds.Contains(notification.Id))
                {
                    continue;
                }

                if (visibleCount >= firstRequestedVisibleIndex && items.Count < pageSize)
                {
                    items.Add(ToListItem(notification));
                }

                visibleCount++;
            }

            rawOffset += candidates.Count;
            if (candidates.Count < AuthorizationScanBatchSize)
            {
                break;
            }
        }

        return new PagedResponse<NotificationListItemResponse>(items, page, pageSize, visibleCount);
    }

    private async Task<int> CountCurrentlyVisibleAsync(
        IQueryable<Notification> query,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var visibleCount = 0;
        var rawOffset = 0;
        while (true)
        {
            var candidates = await query
                .Skip(rawOffset)
                .Take(AuthorizationScanBatchSize)
                .ToListAsync(cancellationToken);
            if (candidates.Count == 0)
            {
                return visibleCount;
            }

            var visibleIds = await FilterCurrentlyVisibleIdsAsync(candidates, userId, cancellationToken);

            visibleCount += candidates.Count(notification => visibleIds.Contains(notification.Id));

            rawOffset += candidates.Count;
            if (candidates.Count < AuthorizationScanBatchSize)
            {
                return visibleCount;
            }
        }
    }

    private async Task<bool> IsCurrentlyVisibleAsync(
        Notification notification,
        Guid userId,
        CancellationToken cancellationToken)
    {
        if (!NotificationCurrentAuthorizationPolicy.RequiresCurrentTargetResolution(notification.RelatedEntityType))
        {
            return true;
        }

        if (targets is null || !currentTenant.IsAvailable)
        {
            return targets is null;
        }

        var resolution = await targets.ResolveAsync(currentTenant.TenantId, userId, notification.Id, cancellationToken);
        return resolution is { IsOwned: true, IsAvailable: true };
    }

    private async Task<IReadOnlySet<Guid>> FilterCurrentlyVisibleIdsAsync(
        IReadOnlyCollection<Notification> notifications,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var visible = notifications
            .Where(notification =>
                !NotificationCurrentAuthorizationPolicy.RequiresCurrentTargetResolution(notification.RelatedEntityType))
            .Select(notification => notification.Id)
            .ToHashSet();
        var protectedIds = notifications
            .Where(notification =>
                NotificationCurrentAuthorizationPolicy.RequiresCurrentTargetResolution(notification.RelatedEntityType))
            .Select(notification => notification.Id)
            .ToArray();
        if (protectedIds.Length == 0)
        {
            return visible;
        }

        if (targets is null || !currentTenant.IsAvailable)
        {
            if (targets is null)
            {
                visible.UnionWith(protectedIds);
            }

            return visible;
        }

        visible.UnionWith(await targets.FilterAvailableNotificationIdsAsync(
            currentTenant.TenantId,
            userId,
            protectedIds,
            cancellationToken));
        return visible;
    }

    private static NotificationListItemResponse ToListItem(Notification notification) => new(
        notification.Id,
        notification.UserId,
        notification.NotificationType,
        notification.Title,
        notification.Body,
        notification.RelatedEntityType,
        notification.RelatedEntityId,
        notification.IsRead,
        notification.CreatedAt,
        notification.ReadAt,
        BuildTargetRoute(notification.RelatedEntityType, notification.RelatedEntityId),
        notification.StateVersion);

    private async Task<Guid?> FindExistingLogicalNotificationIdAsync(
        Guid userId,
        string normalizedLogicalKey,
        CancellationToken cancellationToken)
    {
        var local = FindLocalLogicalNotification(userId, normalizedLogicalKey);
        return local?.Id ??
            await FindLogicalNotificationAsync(userId, normalizedLogicalKey, cancellationToken);
    }

    private async Task<Guid?> FindLogicalNotificationAsync(
        Guid userId,
        string logicalKey,
        CancellationToken cancellationToken)
    {
        return await dbContext.Notifications
            .AsNoTracking()
            .Where(notification =>
                notification.TenantId == currentTenant.TenantId &&
                notification.UserId == userId &&
                notification.LogicalKey == logicalKey)
            .Select(notification => (Guid?)notification.Id)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private Notification? FindLocalLogicalNotification(Guid userId, string logicalKey)
    {
        return dbContext.Notifications.Local.FirstOrDefault(notification =>
            notification.TenantId == currentTenant.TenantId &&
            notification.UserId == userId &&
            string.Equals(notification.LogicalKey, logicalKey, StringComparison.Ordinal));
    }

    private static string NormalizeLogicalKey(string logicalKey)
    {
        if (string.IsNullOrWhiteSpace(logicalKey))
        {
            throw new ArgumentException("A logical notification key is required.", nameof(logicalKey));
        }

        var normalized = logicalKey.Trim();
        if (normalized.Length > NotificationLogicalKeyContract.MaximumLength)
        {
            throw new ArgumentException(
                $"A logical notification key may not exceed {NotificationLogicalKeyContract.MaximumLength} characters.",
                nameof(logicalKey));
        }

        return normalized;
    }

    private static bool TryGetRetriableUniqueConflict(DbUpdateException exception, out bool isLogicalKeyConflict)
    {
        isLogicalKeyConflict = false;
        if (exception.InnerException is not PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation
            } postgres)
        {
            return false;
        }

        isLogicalKeyConflict = string.Equals(
            postgres.ConstraintName,
            NotificationLogicalKeyContract.UniqueIndexName,
            StringComparison.Ordinal);
        return isLogicalKeyConflict || string.Equals(
            postgres.ConstraintName,
            NotificationUserStateIdentityIndex,
            StringComparison.Ordinal);
    }

    private Dictionary<object, TrackedEntrySnapshot> CaptureTrackedEntries()
    {
        return dbContext.ChangeTracker.Entries()
            .ToDictionary(
                entry => entry.Entity,
                entry => new TrackedEntrySnapshot(
                    entry.State,
                    entry.CurrentValues.Clone(),
                    entry.OriginalValues.Clone()));
    }

    private void RestoreTrackedEntries(IReadOnlyDictionary<object, TrackedEntrySnapshot> originalEntries)
    {
        foreach (var entry in dbContext.ChangeTracker.Entries().ToList())
        {
            if (!originalEntries.TryGetValue(entry.Entity, out var snapshot))
            {
                entry.State = EntityState.Detached;
                continue;
            }

            entry.CurrentValues.SetValues(snapshot.CurrentValues);
            entry.OriginalValues.SetValues(snapshot.OriginalValues);
            entry.State = snapshot.State;
        }
    }

    private sealed record TrackedEntrySnapshot(
        EntityState State,
        PropertyValues CurrentValues,
        PropertyValues OriginalValues);

    private async Task<NotificationUserState> GetOrCreateUserStateAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var local = dbContext.NotificationUserStates.Local.FirstOrDefault(item =>
            item.TenantId == currentTenant.TenantId &&
            item.UserId == userId);
        if (local is not null)
        {
            return local;
        }

        var state = await dbContext.NotificationUserStates.SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        if (state is not null)
        {
            return state;
        }

        state = new NotificationUserState
        {
            TenantId = currentTenant.TenantId,
            UserId = userId,
            Version = 0,
            UpdatedAt = now
        };
        await dbContext.NotificationUserStates.AddAsync(state, cancellationToken);
        return state;
    }

    private static long AdvanceState(NotificationUserState state, DateTimeOffset now)
    {
        state.Version = checked(state.Version + 1);
        state.UpdatedAt = now;
        return state.Version;
    }

    private async Task EnqueueTaskCreatedAsync(Notification notification, long stateVersion, CancellationToken cancellationToken)
    {
        if (outbox is null || !currentTenant.IsAvailable)
        {
            return;
        }

        // Task notification signals are intentionally reference-only. The
        // recipient must re-authorize and refetch the current projection; no
        // task title, comment/review content, route, or relationship state is
        // durable in this event.
        var payload = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            notificationId = notification.Id,
            stateVersion,
            requiresRefetch = true
        });
        await EnqueueAsync(
            "Notifications.NotificationCreated.v1",
            notification.Id,
            notification.StateVersion,
            notification.CreatedAt,
            payload,
            notification.UserId,
            cancellationToken);
    }

    private async Task EnqueueCreatedAsync(Notification notification, long stateVersion, CancellationToken cancellationToken)
    {
        if (outbox is null || !currentTenant.IsAvailable)
        {
            return;
        }

        var unreadCount = await GetUnreadCountAsync(notification.UserId, cancellationToken) + 1;
        var payload = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            notification = new
            {
                id = notification.Id,
                type = notification.NotificationType.ToString(),
                title = notification.Title,
                body = notification.Body,
                createdAt = notification.CreatedAt,
                isRead = false,
                target = new
                {
                    targetType = notification.RelatedEntityType,
                    targetId = notification.RelatedEntityId,
                    route = BuildTargetRoute(notification.RelatedEntityType, notification.RelatedEntityId)
                },
                version = notification.StateVersion
            },
            unreadCount,
            stateVersion
        });
        await EnqueueAsync("Notifications.NotificationCreated.v1", notification.Id, notification.StateVersion, notification.CreatedAt, payload, notification.UserId, cancellationToken);
    }

    private async Task EnqueueReadStateChangeAsync(
        Guid userId,
        Guid? notificationId,
        string change,
        long stateVersion,
        DateTimeOffset updatedAt,
        int unreadCount,
        CancellationToken cancellationToken)
    {
        if (outbox is null || !currentTenant.IsAvailable)
        {
            return;
        }

        var payload = System.Text.Json.JsonSerializer.SerializeToElement(new { notificationId, change, unreadCount, stateVersion, updatedAt });
        await EnqueueAsync("Notifications.NotificationReadStateChanged.v1", notificationId ?? userId, stateVersion, updatedAt, payload, userId, cancellationToken);
    }

    private async Task EnqueueAsync(string eventType, Guid aggregateId, long aggregateVersion, DateTimeOffset occurredAt, System.Text.Json.JsonElement payload, Guid recipientUserId, CancellationToken cancellationToken)
    {
        var result = await outbox!.EnqueueAsync(
            new DurableEventEnvelope(Guid.NewGuid(), eventType, RealtimeEventCatalog.PayloadSchemaVersion1, occurredAt, currentTenant.TenantId,
                "Notification", aggregateId, aggregateVersion, RealtimeActor.System(), null, null, payload),
            [new RealtimeRoutingTarget(RealtimeSubscriptionType.User, recipientUserId)],
            cancellationToken);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException("Notification realtime event could not be persisted.");
        }
    }
}