using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Notifications;
using Coglatas.Application.Realtime;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Infrastructure.Persistence;

/// <summary>
/// The single current-state resolver used by notification open and by durable
/// dispatch.  It deliberately returns only authorization decisions and safe
/// navigation metadata; callers never receive Task, Project, or Workspace
/// display data from this boundary.
/// </summary>
public sealed class CurrentAuthorizationTargetResolver(
    AppDbContext dbContext,
    ICurrentTenant currentTenant,
    IMessagingRepository? messaging = null) : INotificationTargetResolver, IRealtimeEventTargetResolver
{
    private static readonly HashSet<string> TaskEventTypes = new(StringComparer.Ordinal)
    {
        "Projects.TaskChanged.v1",
        "Projects.TaskAssignmentChanged.v1",
        "Projects.TaskWorkflowChanged.v1",
        "Projects.TaskCommentChanged.v1"
    };

    public async Task<NotificationTargetResolution> ResolveAsync(
        Guid tenantId,
        Guid userId,
        Guid notificationId,
        CancellationToken cancellationToken = default)
    {
        if (!IsTenantInScope(tenantId) || userId == Guid.Empty || notificationId == Guid.Empty)
        {
            return NotOwned();
        }

        var notification = await FindRecipientNotificationAsync(tenantId, userId, notificationId, cancellationToken);
        if (notification is null)
        {
            return NotOwned();
        }

        return await ResolveNotificationTargetAsync(notification, tenantId, userId, cancellationToken);
    }

    private async Task<NotificationTargetResolution> ResolveNotificationTargetAsync(
        Notification notification,
        Guid tenantId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        if (!await AuthorizationTargetShared.HasCurrentTenantUserAsync(dbContext, tenantId, userId, cancellationToken))
        {
            return Unavailable(notification.StateVersion);
        }

        if (notification.RelatedEntityType is "TaskItem" or "Task")
        {
            if (!notification.RelatedEntityId.HasValue)
            {
                return Unavailable(notification.StateVersion);
            }

            var target = await ResolveTaskTargetAsync(tenantId, userId, notification.RelatedEntityId.Value, cancellationToken);
            return target is null
                ? Unavailable(notification.StateVersion)
                : new NotificationTargetResolution(
                    true,
                    true,
                    $"/projects/{target.ProjectId}/tasks/{target.TaskId}",
                    notification.StateVersion,
                    target.WorkspaceId);
        }

        if (string.Equals(notification.RelatedEntityType, TaskDeadlineDigestPolicy.RelatedEntityType, StringComparison.Ordinal))
        {
            if (!notification.RelatedEntityId.HasValue)
            {
                return Unavailable(notification.StateVersion);
            }

            var target = await ResolveDigestTargetAsync(
                tenantId,
                userId,
                notification.Id,
                notification.RelatedEntityId.Value,
                cancellationToken);
            return target is null
                ? Unavailable(notification.StateVersion)
                : new NotificationTargetResolution(true, true, "/tasks", notification.StateVersion, target.WorkspaceId);
        }

        if (string.Equals(notification.RelatedEntityType, "Artifact", StringComparison.Ordinal))
        {
            if (!notification.RelatedEntityId.HasValue)
            {
                return Unavailable(notification.StateVersion);
            }

            var target = await ResolveArtifactTargetAsync(
                tenantId,
                userId,
                notification.RelatedEntityId.Value,
                cancellationToken);
            return target is null
                ? Unavailable(notification.StateVersion)
                : new NotificationTargetResolution(
                    true,
                    true,
                    $"/artifacts/{target.ArtifactId}",
                    notification.StateVersion,
                    target.WorkspaceId);
        }

        if (string.Equals(notification.RelatedEntityType, "Message", StringComparison.Ordinal))
        {
            if (!notification.RelatedEntityId.HasValue)
            {
                return Unavailable(notification.StateVersion);
            }

            var target = await ResolveMessageTargetAsync(
                tenantId,
                userId,
                notification.RelatedEntityId.Value,
                cancellationToken);
            return target is null
                ? Unavailable(notification.StateVersion)
                : new NotificationTargetResolution(
                    true,
                    true,
                    $"/messages/{target.MessageId}",
                    notification.StateVersion,
                    target.WorkspaceId);
        }

        // This endpoint is intentionally narrow.  Legacy and unknown targets
        // cannot turn a persisted historical route into navigation authority.
        return Unavailable(notification.StateVersion);
    }

    public async Task<bool> CanDeliverCreatedAsync(
        Guid tenantId,
        Guid recipientUserId,
        DurableEventEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        if (!IsTenantInScope(tenantId) ||
            envelope.EventType != "Notifications.NotificationCreated.v1" ||
            envelope.TenantId != tenantId ||
            envelope.AggregateId == Guid.Empty)
        {
            return false;
        }

        var notification = await FindRecipientNotificationAsync(
            tenantId,
            recipientUserId,
            envelope.AggregateId,
            cancellationToken);
        if (notification is null || !await AuthorizationTargetShared.HasCurrentTenantUserAsync(dbContext, tenantId, recipientUserId, cancellationToken))
        {
            return false;
        }

        // Existing generic recipient notifications retain their established
        // embedded-event contract. Task and digest use a strict reference-only
        // schema. Artifact and Message retain their legacy embedded schema but
        // still require current target authorization before delivery.
        if (!NotificationCurrentAuthorizationPolicy.RequiresCurrentTargetResolution(notification.RelatedEntityType))
        {
            return true;
        }

        if (NotificationCurrentAuthorizationPolicy.RequiresReferenceOnlyCreatedPayload(notification.RelatedEntityType) &&
            (!AuthorizationTargetShared.TryGetGuid(envelope.Payload, "notificationId", out var payloadNotificationId) ||
             payloadNotificationId != envelope.AggregateId ||
             !AuthorizationTargetShared.IsReferenceOnlyNotificationCreatedPayload(
                 envelope.Payload,
                 payloadNotificationId,
                 envelope.AggregateVersion)))
        {
            return false;
        }

        var resolution = await ResolveAsync(
            tenantId,
            recipientUserId,
            envelope.AggregateId,
            cancellationToken);
        return resolution is { IsOwned: true, IsAvailable: true };
    }

    public async Task<bool> CanDeliverReadStateAsync(
        Guid tenantId,
        Guid recipientUserId,
        DurableEventEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        if (!IsTenantInScope(tenantId) ||
            envelope.EventType != "Notifications.NotificationReadStateChanged.v1" ||
            envelope.TenantId != tenantId ||
            !await AuthorizationTargetShared.HasCurrentTenantUserAsync(dbContext, tenantId, recipientUserId, cancellationToken))
        {
            return false;
        }

        var hasNotificationId = AuthorizationTargetShared.TryGetNullableGuid(envelope.Payload, "notificationId", out var notificationId);
        if (!hasNotificationId)
        {
            return false;
        }

        if (!notificationId.HasValue)
        {
            return envelope.AggregateId == recipientUserId &&
                string.Equals(AuthorizationTargetShared.GetString(envelope.Payload, "change"), "allRead", StringComparison.Ordinal);
        }

        if (envelope.AggregateId != notificationId.Value)
        {
            return false;
        }

        var change = AuthorizationTargetShared.GetString(envelope.Payload, "change");
        if (change is not ("read" or "deleted"))
        {
            return false;
        }

        var notification = change == "deleted"
            ? await FindDeletedRecipientNotificationAsync(
                tenantId,
                recipientUserId,
                notificationId.Value,
                cancellationToken)
            : await FindRecipientNotificationAsync(
                tenantId,
                recipientUserId,
                notificationId.Value,
                cancellationToken);
        if (notification is null ||
            (change == "deleted" && !notification.DeletedAt.HasValue))
        {
            return false;
        }

        // Generic recipient notifications retain their existing read-state
        // event contract. Protected target state shares notification open's
        // current-target fence, so a delayed/replayed signal cannot outlive a
        // revocation.
        if (!NotificationCurrentAuthorizationPolicy.RequiresCurrentTargetResolution(notification.RelatedEntityType))
        {
            return true;
        }

        var resolution = change == "deleted"
            ? await ResolveNotificationTargetAsync(notification, tenantId, recipientUserId, cancellationToken)
            : await ResolveAsync(tenantId, recipientUserId, notificationId.Value, cancellationToken);
        return resolution is { IsOwned: true, IsAvailable: true };
    }

    public async Task<IReadOnlySet<Guid>> FilterAvailableNotificationIdsAsync(
        Guid tenantId,
        Guid userId,
        IReadOnlyCollection<Guid> notificationIds,
        CancellationToken cancellationToken = default)
    {
        if (!IsTenantInScope(tenantId) ||
            userId == Guid.Empty ||
            notificationIds.Count == 0 ||
            !await AuthorizationTargetShared.HasCurrentTenantUserAsync(dbContext, tenantId, userId, cancellationToken))
        {
            return new HashSet<Guid>();
        }

        var requestedIds = notificationIds.Distinct().ToArray();
        var notifications = await dbContext.Notifications
            .AsNoTracking()
            .Where(item =>
                Enumerable.Contains(requestedIds, item.Id) &&
                item.TenantId == tenantId &&
                item.UserId == userId &&
                item.DeletedAt == null)
            .Select(item => new
            {
                item.Id,
                item.RelatedEntityType,
                item.RelatedEntityId
            })
            .ToListAsync(cancellationToken);
        var available = new HashSet<Guid>();

        var artifactNotifications = notifications
            .Where(item => item.RelatedEntityType == "Artifact" && item.RelatedEntityId.HasValue)
            .ToArray();
        if (artifactNotifications.Length > 0)
        {
            var artifactIds = artifactNotifications.Select(item => item.RelatedEntityId!.Value).Distinct().ToArray();
            var visibleProjectIds = dbContext.VisibleProjectsFor(userId).Select(project => project.Id);
            var visibleArtifactIds = await dbContext.Artifacts
                .AsNoTracking()
                .Where(item => Enumerable.Contains(artifactIds, item.Id) && item.TenantId == tenantId && item.DeletedAt == null)
                .Where(item => visibleProjectIds.Contains(item.ProjectId))
                .Select(item => item.Id)
                .ToListAsync(cancellationToken);
            var visibleArtifacts = visibleArtifactIds.ToHashSet();
            foreach (var notification in artifactNotifications)
            {
                if (visibleArtifacts.Contains(notification.RelatedEntityId!.Value))
                {
                    available.Add(notification.Id);
                }
            }
        }

        var messageNotifications = notifications
            .Where(item => item.RelatedEntityType == "Message" && item.RelatedEntityId.HasValue)
            .ToArray();
        if (messageNotifications.Length > 0 && messaging is not null)
        {
            var messageIds = messageNotifications.Select(item => item.RelatedEntityId!.Value).Distinct().ToArray();
            var messages = await dbContext.Messages
                .AsNoTracking()
                .Where(item => Enumerable.Contains(messageIds, item.Id) && item.TenantId == tenantId && item.DeletedAt == null)
                .Where(item => item.WorkspaceId == item.Conversation!.WorkspaceId)
                .Select(item => new { item.Id, item.ConversationId })
                .ToListAsync(cancellationToken);
            var readableConversationIds = await messaging.FilterReadableConversationIdsAsync(
                userId,
                messages.Select(item => item.ConversationId).Distinct().ToArray(),
                cancellationToken);
            var readableMessageIds = messages
                .Where(item => readableConversationIds.Contains(item.ConversationId))
                .Select(item => item.Id)
                .ToHashSet();
            foreach (var notification in messageNotifications)
            {
                if (readableMessageIds.Contains(notification.RelatedEntityId!.Value))
                {
                    available.Add(notification.Id);
                }
            }
        }

        foreach (var notification in notifications.Where(item =>
                     item.RelatedEntityType != "Artifact" && item.RelatedEntityType != "Message"))
        {
            var resolution = await ResolveAsync(tenantId, userId, notification.Id, cancellationToken);
            if (resolution is { IsOwned: true, IsAvailable: true })
            {
                available.Add(notification.Id);
            }
        }

        return available;
    }

    public async Task<bool> CanReceiveTaskEventAsync(
        Guid tenantId,
        Guid userId,
        RealtimeSubscriptionType targetType,
        Guid targetResourceId,
        DurableEventEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        if (!IsTenantInScope(tenantId) ||
            !TaskEventTypes.Contains(envelope.EventType) ||
            envelope.TenantId != tenantId ||
            envelope.AggregateType != "Task" ||
            !AuthorizationTargetShared.TryGetGuid(envelope.Payload, "taskId", out var taskId) ||
            taskId != envelope.AggregateId ||
            !AuthorizationTargetShared.TryGetGuid(envelope.Payload, "projectId", out var projectId))
        {
            return false;
        }

        var target = await ResolveTaskTargetAsync(tenantId, userId, taskId, cancellationToken);
        if (target is null || target.ProjectId != projectId)
        {
            return false;
        }

        return targetType switch
        {
            RealtimeSubscriptionType.User => targetResourceId == userId,
            RealtimeSubscriptionType.Project => targetResourceId == target.ProjectId,
            RealtimeSubscriptionType.Workspace => targetResourceId == target.WorkspaceId,
            _ => false
        };
    }

    public async Task<bool> CanReceiveProjectEventAsync(
        Guid tenantId,
        Guid userId,
        RealtimeSubscriptionType targetType,
        Guid targetResourceId,
        DurableEventEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        if (!IsTenantInScope(tenantId) ||
            envelope.EventType != "Projects.ProjectChanged.v1" ||
            envelope.TenantId != tenantId ||
            envelope.AggregateType != "Project" ||
            !AuthorizationTargetShared.TryGetGuid(envelope.Payload, "projectId", out var projectId) ||
            projectId != envelope.AggregateId ||
            !AuthorizationTargetShared.TryGetGuid(envelope.Payload, "workspaceId", out var workspaceId))
        {
            return false;
        }

        var project = await ResolveProjectTargetAsync(tenantId, userId, projectId, cancellationToken);
        if (project is null || project.WorkspaceId != workspaceId)
        {
            return false;
        }

        return targetType switch
        {
            RealtimeSubscriptionType.Project => targetResourceId == project.ProjectId,
            RealtimeSubscriptionType.Workspace => targetResourceId == project.WorkspaceId,
            RealtimeSubscriptionType.User => targetResourceId == userId,
            _ => false
        };
    }

    public Task<bool> CanReceiveAuthorizationInvalidationAsync(
        Guid tenantId,
        Guid userId,
        RealtimeSubscriptionType targetType,
        Guid targetResourceId,
        DurableEventEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        var allowed = IsTenantInScope(tenantId) &&
            envelope.EventType == "Security.AuthorizationStateChanged.v1" &&
            envelope.TenantId == tenantId &&
            targetType == RealtimeSubscriptionType.User &&
            targetResourceId == userId &&
            envelope.AggregateId == userId &&
            AuthorizationTargetShared.TryGetGuid(envelope.Payload, "affectedUserId", out var affectedUserId) &&
            affectedUserId == userId;
        return Task.FromResult(allowed);
    }

    private async Task<ResolvedTaskTarget?> ResolveTaskTargetAsync(
        Guid tenantId,
        Guid userId,
        Guid taskId,
        CancellationToken cancellationToken)
    {
        if (!await AuthorizationTargetShared.HasCurrentTenantUserAsync(dbContext, tenantId, userId, cancellationToken))
        {
            return null;
        }

        var task = await dbContext.TaskItems
            .AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.Id == taskId &&
                item.TenantId == tenantId &&
                item.DeletedAt == null,
                cancellationToken);
        if (task is null)
        {
            return null;
        }

        var project = await ResolveProjectTargetAsync(tenantId, userId, task.ProjectId, cancellationToken);
        return project is null || project.WorkspaceId != task.WorkspaceId
            ? null
            : new ResolvedTaskTarget(task.Id, project.ProjectId, project.WorkspaceId);
    }

    private async Task<ResolvedArtifactTarget?> ResolveArtifactTargetAsync(
        Guid tenantId,
        Guid userId,
        Guid artifactId,
        CancellationToken cancellationToken)
    {
        var artifact = await dbContext.Artifacts
            .AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.Id == artifactId &&
                item.TenantId == tenantId &&
                item.DeletedAt == null,
                cancellationToken);
        if (artifact is null)
        {
            return null;
        }

        var project = await dbContext.VisibleProjectsFor(userId)
            .Where(item => item.Id == artifact.ProjectId)
            .Select(item => new { item.Id, item.WorkspaceId })
            .SingleOrDefaultAsync(cancellationToken);
        return project is null
            ? null
            : new ResolvedArtifactTarget(artifact.Id, project.WorkspaceId);
    }

    private async Task<ResolvedMessageTarget?> ResolveMessageTargetAsync(
        Guid tenantId,
        Guid userId,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        if (messaging is null)
        {
            return null;
        }

        var message = await dbContext.Messages
            .AsNoTracking()
            .Where(item =>
                item.Id == messageId &&
                item.TenantId == tenantId &&
                item.DeletedAt == null)
            .Select(item => new
            {
                item.Id,
                item.ConversationId,
                item.WorkspaceId,
                ConversationWorkspaceId = item.Conversation!.WorkspaceId
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (message is null || message.WorkspaceId != message.ConversationWorkspaceId)
        {
            return null;
        }

        var readable = await messaging.FilterReadableConversationIdsAsync(
            userId,
            [message.ConversationId],
            cancellationToken);
        return readable.Contains(message.ConversationId)
            ? new ResolvedMessageTarget(message.Id, message.WorkspaceId)
            : null;
    }

    private async Task<ResolvedProjectTarget?> ResolveProjectTargetAsync(
        Guid tenantId,
        Guid userId,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        if (!await AuthorizationTargetShared.HasCurrentTenantUserAsync(dbContext, tenantId, userId, cancellationToken))
        {
            return null;
        }

        var project = await dbContext.Projects
            .AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.Id == projectId &&
                item.TenantId == tenantId &&
                item.DeletedAt == null &&
                item.Status != ProjectStatus.Archived &&
                item.Status != ProjectStatus.Deleted,
                cancellationToken);
        if (project is null)
        {
            return null;
        }

        var member = await dbContext.WorkspaceMembers
            .AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.TenantId == tenantId &&
                item.WorkspaceId == project.WorkspaceId &&
                item.UserId == userId &&
                item.Status == MembershipStatus.Active,
                cancellationToken);
        if (member is null ||
            !await dbContext.Workspaces.AsNoTracking().AnyAsync(item =>
                item.Id == project.WorkspaceId &&
                item.TenantId == tenantId &&
                item.DeletedAt == null &&
                item.Status == WorkspaceStatus.Active,
                cancellationToken))
        {
            return null;
        }

        var isProjectMember = await dbContext.ProjectMembers
            .AsNoTracking()
            .AnyAsync(item => item.TenantId == tenantId && item.ProjectId == projectId && item.UserId == userId, cancellationToken);
        if ((project.Status is ProjectStatus.Planning or ProjectStatus.Suspended) && !isProjectMember)
        {
            return null;
        }
        if (!isProjectMember && project.GroupId.HasValue)
        {
            var canManageWorkspace = member.Role is WorkspaceRole.Owner or WorkspaceRole.Admin;
            var isActiveGroupMember = await dbContext.Groups
                .AsNoTracking()
                .Where(group =>
                    group.Id == project.GroupId.Value &&
                    group.TenantId == tenantId &&
                    group.WorkspaceId == project.WorkspaceId &&
                    group.DeletedAt == null &&
                    group.Status == GroupStatus.Active)
                .AnyAsync(group => group.Members.Any(groupMember => groupMember.UserId == userId), cancellationToken);
            if (!canManageWorkspace && !isActiveGroupMember)
            {
                return null;
            }
        }

        return new ResolvedProjectTarget(project.Id, project.WorkspaceId);
    }

    private async Task<ResolvedDigestTarget?> ResolveDigestTargetAsync(
        Guid tenantId,
        Guid userId,
        Guid notificationId,
        Guid digestJobId,
        CancellationToken cancellationToken)
    {
        var digest = await dbContext.TaskDeadlineDigestJobs
            .AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.Id == digestJobId &&
                item.TenantId == tenantId &&
                item.UserId == userId &&
                item.NotificationId == notificationId,
                cancellationToken);
        if (digest is null || !await HasCurrentWorkspaceMembershipAsync(tenantId, userId, digest.WorkspaceId, cancellationToken))
        {
            return null;
        }

        return new ResolvedDigestTarget(digest.WorkspaceId);
    }

    private Task<Notification?> FindRecipientNotificationAsync(
        Guid tenantId,
        Guid userId,
        Guid notificationId,
        CancellationToken cancellationToken) =>
        dbContext.Notifications
            .AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.Id == notificationId &&
                item.TenantId == tenantId &&
                item.UserId == userId &&
                item.DeletedAt == null,
                cancellationToken);

    private Task<Notification?> FindDeletedRecipientNotificationAsync(
        Guid tenantId,
        Guid userId,
        Guid notificationId,
        CancellationToken cancellationToken) =>
        dbContext.Notifications
            .AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.Id == notificationId &&
                item.TenantId == tenantId &&
                item.UserId == userId &&
                item.DeletedAt != null,
                cancellationToken);

    private async Task<bool> HasCurrentWorkspaceMembershipAsync(
        Guid tenantId,
        Guid userId,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        return await dbContext.Workspaces.AsNoTracking().AnyAsync(workspace =>
            workspace.Id == workspaceId &&
            workspace.TenantId == tenantId &&
            workspace.DeletedAt == null &&
            workspace.Status == WorkspaceStatus.Active,
            cancellationToken) &&
            await dbContext.WorkspaceMembers.AsNoTracking().AnyAsync(member =>
                member.TenantId == tenantId &&
                member.WorkspaceId == workspaceId &&
                member.UserId == userId &&
                member.Status == MembershipStatus.Active,
                cancellationToken);
    }

    private bool IsTenantInScope(Guid tenantId) =>
        tenantId != Guid.Empty && currentTenant.IsAvailable && currentTenant.TenantId == tenantId;

    private static NotificationTargetResolution NotOwned() => new(false, false, null, 0);

    private static NotificationTargetResolution Unavailable(long stateVersion) => new(true, false, null, stateVersion);

    private sealed record ResolvedTaskTarget(Guid TaskId, Guid ProjectId, Guid WorkspaceId);

    private sealed record ResolvedArtifactTarget(Guid ArtifactId, Guid WorkspaceId);

    private sealed record ResolvedMessageTarget(Guid MessageId, Guid WorkspaceId);

    private sealed record ResolvedProjectTarget(Guid ProjectId, Guid WorkspaceId);

    private sealed record ResolvedDigestTarget(Guid WorkspaceId);
}
