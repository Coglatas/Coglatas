using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Notifications;
using Coglatas.Application.Realtime;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Infrastructure.Persistence;

/// <summary>
/// Canonical current-state authorization for protected notification and realtime
/// Project/Task targets. Task and Project paths resolve directly through
/// VisibleProjectsFor; the legacy resolver is used only for non-Project target
/// families whose established contracts are unchanged by WPC-Final01.
/// </summary>
public sealed class CanonicalCurrentAuthorizationTargetResolver(
    AppDbContext dbContext,
    ICurrentTenant currentTenant,
    CurrentAuthorizationTargetResolver inner) : INotificationTargetResolver, IRealtimeEventTargetResolver
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

        var notification = await FindNotificationAsync(
            tenantId,
            userId,
            notificationId,
            includeDeleted: false,
            cancellationToken);
        if (notification is null)
        {
            return NotOwned();
        }

        if (notification.RelatedEntityType is not ("TaskItem" or "Task"))
        {
            return await inner.ResolveAsync(tenantId, userId, notificationId, cancellationToken);
        }

        if (!await AuthorizationTargetShared.HasCurrentTenantUserAsync(dbContext, tenantId, userId, cancellationToken) ||
            !notification.RelatedEntityId.HasValue)
        {
            return Unavailable(notification.StateVersion);
        }

        var target = await ResolveTaskTargetAsync(
            tenantId,
            userId,
            notification.RelatedEntityId.Value,
            cancellationToken);
        return target is null
            ? Unavailable(notification.StateVersion)
            : new NotificationTargetResolution(
                true,
                true,
                $"/projects/{target.ProjectId}/tasks/{target.TaskId}",
                notification.StateVersion,
                target.WorkspaceId);
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

        var notification = await FindNotificationAsync(
            tenantId,
            recipientUserId,
            envelope.AggregateId,
            includeDeleted: false,
            cancellationToken);
        if (notification is null)
        {
            return false;
        }

        if (notification.RelatedEntityType is not ("TaskItem" or "Task"))
        {
            return await inner.CanDeliverCreatedAsync(
                tenantId,
                recipientUserId,
                envelope,
                cancellationToken);
        }

        return await AuthorizationTargetShared.HasCurrentTenantUserAsync(dbContext, tenantId, recipientUserId, cancellationToken) &&
               notification.RelatedEntityId.HasValue &&
               AuthorizationTargetShared.TryGetGuid(envelope.Payload, "notificationId", out var payloadNotificationId) &&
               payloadNotificationId == envelope.AggregateId &&
               AuthorizationTargetShared.IsReferenceOnlyNotificationCreatedPayload(
                   envelope.Payload,
                   payloadNotificationId,
                   envelope.AggregateVersion) &&
               await ResolveTaskTargetAsync(
                   tenantId,
                   recipientUserId,
                   notification.RelatedEntityId.Value,
                   cancellationToken) is not null;
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
            !AuthorizationTargetShared.TryGetNullableGuid(envelope.Payload, "notificationId", out var notificationId))
        {
            return false;
        }

        if (!notificationId.HasValue)
        {
            return await inner.CanDeliverReadStateAsync(
                tenantId,
                recipientUserId,
                envelope,
                cancellationToken);
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

        var notification = await FindNotificationAsync(
            tenantId,
            recipientUserId,
            notificationId.Value,
            includeDeleted: change == "deleted",
            cancellationToken,
            requireDeleted: change == "deleted");
        if (notification is null)
        {
            return false;
        }

        if (notification.RelatedEntityType is not ("TaskItem" or "Task"))
        {
            return await inner.CanDeliverReadStateAsync(
                tenantId,
                recipientUserId,
                envelope,
                cancellationToken);
        }

        return await AuthorizationTargetShared.HasCurrentTenantUserAsync(dbContext, tenantId, recipientUserId, cancellationToken) &&
               notification.RelatedEntityId.HasValue &&
               await ResolveTaskTargetAsync(
                   tenantId,
                   recipientUserId,
                   notification.RelatedEntityId.Value,
                   cancellationToken) is not null;
    }

    public async Task<IReadOnlySet<Guid>> FilterAvailableNotificationIdsAsync(
        Guid tenantId,
        Guid userId,
        IReadOnlyCollection<Guid> notificationIds,
        CancellationToken cancellationToken = default)
    {
        if (!IsTenantInScope(tenantId) || userId == Guid.Empty || notificationIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var requested = notificationIds.Distinct().ToArray();
        var notifications = await dbContext.Notifications
            .AsNoTracking()
            .Where(item =>
                Enumerable.Contains(requested, item.Id) &&
                item.TenantId == tenantId &&
                item.UserId == userId &&
                item.DeletedAt == null)
            .Select(item => new NotificationTarget(
                item.Id,
                item.RelatedEntityType,
                item.RelatedEntityId,
                item.StateVersion))
            .ToListAsync(cancellationToken);

        var available = new HashSet<Guid>();
        var nonTaskIds = new List<Guid>();
        foreach (var notification in notifications)
        {
            if (notification.RelatedEntityType is not ("TaskItem" or "Task"))
            {
                nonTaskIds.Add(notification.NotificationId);
                continue;
            }

            if (notification.RelatedEntityId.HasValue &&
                await ResolveTaskTargetAsync(
                    tenantId,
                    userId,
                    notification.RelatedEntityId.Value,
                    cancellationToken) is not null)
            {
                available.Add(notification.NotificationId);
            }
        }

        if (nonTaskIds.Count > 0)
        {
            available.UnionWith(await inner.FilterAvailableNotificationIdsAsync(
                tenantId,
                userId,
                nonTaskIds,
                cancellationToken));
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
        CancellationToken cancellationToken = default) =>
        inner.CanReceiveAuthorizationInvalidationAsync(
            tenantId,
            userId,
            targetType,
            targetResourceId,
            envelope,
            cancellationToken);

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
            .Where(item =>
                item.Id == taskId &&
                item.TenantId == tenantId &&
                item.DeletedAt == null)
            .Select(item => new { item.Id, item.ProjectId, item.WorkspaceId })
            .SingleOrDefaultAsync(cancellationToken);
        if (task is null)
        {
            return null;
        }

        var project = await ResolveProjectTargetAsync(
            tenantId,
            userId,
            task.ProjectId,
            cancellationToken);
        return project is null || project.WorkspaceId != task.WorkspaceId
            ? null
            : new ResolvedTaskTarget(task.Id, project.ProjectId, project.WorkspaceId);
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

        var project = await dbContext.VisibleProjectsFor(userId)
            .Where(item => item.Id == projectId && item.TenantId == tenantId)
            .Select(item => new { item.Id, item.WorkspaceId })
            .SingleOrDefaultAsync(cancellationToken);
        return project is null
            ? null
            : new ResolvedProjectTarget(project.Id, project.WorkspaceId);
    }

    private Task<NotificationTarget?> FindNotificationAsync(
        Guid tenantId,
        Guid userId,
        Guid notificationId,
        bool includeDeleted,
        CancellationToken cancellationToken,
        bool requireDeleted = false) =>
        dbContext.Notifications
            .AsNoTracking()
            .Where(item =>
                item.Id == notificationId &&
                item.TenantId == tenantId &&
                item.UserId == userId &&
                (includeDeleted || item.DeletedAt == null) &&
                (!requireDeleted || item.DeletedAt != null))
            .Select(item => new NotificationTarget(
                item.Id,
                item.RelatedEntityType,
                item.RelatedEntityId,
                item.StateVersion))
            .SingleOrDefaultAsync(cancellationToken);

    private bool IsTenantInScope(Guid tenantId) =>
        tenantId != Guid.Empty &&
        currentTenant is { IsAvailable: true, IsPlatformScope: false } &&
        currentTenant.TenantId == tenantId;

    private static NotificationTargetResolution NotOwned() => new(false, false, null, 0);
    private static NotificationTargetResolution Unavailable(long stateVersion) => new(true, false, null, stateVersion);

    private sealed record NotificationTarget(
        Guid NotificationId,
        string? RelatedEntityType,
        Guid? RelatedEntityId,
        long StateVersion);

    private sealed record ResolvedTaskTarget(Guid TaskId, Guid ProjectId, Guid WorkspaceId);
    private sealed record ResolvedProjectTarget(Guid ProjectId, Guid WorkspaceId);
}

/// <summary>
/// Production notification resolver. Task navigation is already canonical in
/// CanonicalCurrentAuthorizationTargetResolver. Artifact/Message navigation is
/// layered through the established WPC-02F navigation resolver and then
/// reauthorized before being returned.
/// </summary>
public sealed class CanonicalNotificationTargetResolver(
    CanonicalCurrentAuthorizationTargetResolver currentAuthorization,
    NotificationNavigationTargetResolver navigation) : INotificationTargetResolver
{
    public async Task<NotificationTargetResolution> ResolveAsync(
        Guid tenantId,
        Guid userId,
        Guid notificationId,
        CancellationToken cancellationToken = default)
    {
        var initial = await currentAuthorization.ResolveAsync(
            tenantId,
            userId,
            notificationId,
            cancellationToken);
        if (!initial.IsOwned || !initial.IsAvailable || string.IsNullOrWhiteSpace(initial.Route))
        {
            return initial;
        }

        // Task routes are produced directly by the canonical resolver. Calling
        // the legacy navigation wrapper here would reintroduce its historical
        // Project predicate for group-bound WorkspaceVisible Projects.
        if (initial.Route.StartsWith("/projects/", StringComparison.Ordinal))
        {
            return initial;
        }

        var navigated = await navigation.ResolveAsync(
            tenantId,
            userId,
            notificationId,
            cancellationToken);
        if (!navigated.IsOwned || !navigated.IsAvailable)
        {
            return navigated;
        }

        var final = await currentAuthorization.ResolveAsync(
            tenantId,
            userId,
            notificationId,
            cancellationToken);
        if (!final.IsOwned ||
            !final.IsAvailable ||
            final.StateVersion != initial.StateVersion ||
            !string.Equals(final.Route, initial.Route, StringComparison.Ordinal))
        {
            return final.IsOwned
                ? new NotificationTargetResolution(true, false, null, final.StateVersion)
                : final;
        }

        return navigated with { StateVersion = final.StateVersion };
    }

    public Task<bool> CanDeliverCreatedAsync(
        Guid tenantId,
        Guid recipientUserId,
        DurableEventEnvelope envelope,
        CancellationToken cancellationToken = default) =>
        currentAuthorization.CanDeliverCreatedAsync(
            tenantId,
            recipientUserId,
            envelope,
            cancellationToken);

    public Task<bool> CanDeliverReadStateAsync(
        Guid tenantId,
        Guid recipientUserId,
        DurableEventEnvelope envelope,
        CancellationToken cancellationToken = default) =>
        currentAuthorization.CanDeliverReadStateAsync(
            tenantId,
            recipientUserId,
            envelope,
            cancellationToken);

    public Task<IReadOnlySet<Guid>> FilterAvailableNotificationIdsAsync(
        Guid tenantId,
        Guid userId,
        IReadOnlyCollection<Guid> notificationIds,
        CancellationToken cancellationToken = default) =>
        currentAuthorization.FilterAvailableNotificationIdsAsync(
            tenantId,
            userId,
            notificationIds,
            cancellationToken);
}
