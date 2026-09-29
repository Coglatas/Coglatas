using Coglatas.Application.Notifications;
using Coglatas.Domain.Enums;

namespace Coglatas.Application.Common.Interfaces;

public interface INotificationService
{
    /// <summary>
    /// Stages one generic Workspace deadline digest and its recipient-only
    /// reference signal without saving. The digest ledger caller owns the
    /// generation transaction.
    /// </summary>
    Task<Guid> StageTaskDeadlineDigestByLogicalKeyAsync(
        Guid userId,
        Guid digestJobId,
        string logicalKey,
        CancellationToken cancellationToken = default)
    {
        return Task.FromException<Guid>(new NotSupportedException("Staged Task deadline digest creation is not implemented."));
    }

    /// <summary>
    /// Stages a task notification and its recipient-only realtime refetch
    /// signal without saving the current unit of work. The caller must commit
    /// the task mutation, notification, audit, and Outbox rows together.
    /// </summary>
    Task<Guid> StageTaskByLogicalKeyAsync(
        Guid userId,
        NotificationType type,
        string title,
        Guid taskId,
        string logicalKey,
        CancellationToken cancellationToken = default)
    {
        return Task.FromException<Guid>(new NotSupportedException("Staged task notification creation is not implemented."));
    }

    /// <summary>
    /// Persists a recipient-specific logical notification and returns the
    /// pre-existing row when PostgreSQL reports the same logical identity.
    /// Callers that combine this operation with other writes must establish
    /// their transaction before invoking it.
    /// </summary>
    Task<Guid> CreateOrGetByLogicalKeyAsync(
        Guid userId,
        NotificationType type,
        string title,
        string? body,
        string? relatedEntityType,
        Guid? relatedEntityId,
        string logicalKey,
        CancellationToken cancellationToken = default)
    {
        return Task.FromException<Guid>(new NotSupportedException("Logical notification creation is not implemented."));
    }

    Task<Guid> CreateAsync(
        Guid userId,
        NotificationType type,
        string title,
        string? body,
        string? relatedEntityType,
        Guid? relatedEntityId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Guid.Empty);
    }

    Task<IReadOnlyList<Guid>> CreateManyAsync(
        IReadOnlyCollection<Guid> userIds,
        NotificationType type,
        string title,
        string? body,
        string? relatedEntityType,
        Guid? relatedEntityId,
        Guid? actorUserId = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<Guid>>([]);
    }

    Task<bool> MarkAsReadAsync(Guid userId, Guid notificationId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(false);
    }

    Task<int> MarkAllAsReadAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(0);
    }

    Task<int> GetUnreadCountAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(0);
    }

    Task<PagedResponse<NotificationListItemResponse>> ListAsync(Guid userId, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new PagedResponse<NotificationListItemResponse>([], page, pageSize, 0));
    }

    Task<bool> DeleteAsync(Guid userId, Guid notificationId, DateTimeOffset deletedAt, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(false);
    }

    Task NotifyAsync(Guid recipientUserId, string title, string? body, string sourceType, Guid sourceId, CancellationToken cancellationToken = default);
}
