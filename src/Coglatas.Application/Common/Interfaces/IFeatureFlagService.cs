
namespace Coglatas.Application.Common.Interfaces;

public static class FeatureKeys
{
    public const string ProductionTracking = nameof(ProductionTracking);
    public const string AdvancedGanttChart = nameof(AdvancedGanttChart);
    public const string ExternalGuestAccess = nameof(ExternalGuestAccess);
    public const string FileSharing = nameof(FileSharing);
    public const string Calendar = nameof(Calendar);
    public const string Attendance = nameof(Attendance);
    public const string Forms = nameof(Forms);
    public const string TenantExport = nameof(TenantExport);
    public const string WebhookIntegration = nameof(WebhookIntegration);
    public const string ApiAccess = nameof(ApiAccess);
    public const string CustomBranding = nameof(CustomBranding);
    public const string AuditLogViewer = nameof(AuditLogViewer);
    public const string RadialMenu = nameof(RadialMenu);
    public const string DockingLayout = nameof(DockingLayout);
    public const string RealtimeSignalR = "realtime.signalR";
    public const string TransactionalOutbox = "communication.transactional_outbox.enabled";
    public const string AuthorizedRealtimeGroups = "communication.authorized_groups.required";
    public const string TasksDomainV1 = "tasks.domainV1";
    public const string MyTasksV1 = "tasks.myTasksV1";
    public const string KanbanV1 = "tasks.kanbanV1";
    public const string GanttV1 = "tasks.ganttV1";
    public const string TasksNotificationsV1 = "tasks.notificationsV1";

    public static readonly IReadOnlyList<string> All =
    [
        ProductionTracking,
        AdvancedGanttChart,
        ExternalGuestAccess,
        FileSharing,
        Calendar,
        Attendance,
        Forms,
        TenantExport,
        WebhookIntegration,
        ApiAccess,
        CustomBranding,
        AuditLogViewer,
        RadialMenu,
        DockingLayout,
        RealtimeSignalR,
        TransactionalOutbox,
        AuthorizedRealtimeGroups,
        TasksDomainV1,
        MyTasksV1,
        KanbanV1,
        GanttV1,
        TasksNotificationsV1
    ];

    /// <summary>
    /// New Task-notification production remains explicitly opt-in. Existing
    /// keys preserve the registry's historical enabled-by-default behavior.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultEnabled =
        All.Where(key => !string.Equals(key, TasksNotificationsV1, StringComparison.Ordinal)).ToArray();

    public static string Normalize(string featureKey)
    {
        return featureKey.Trim() switch
        {
            "RealtimeSignalR" or "realtime.signalr" => RealtimeSignalR,
            "TransactionalOutbox" or "communication.transactionalOutbox.enabled" => TransactionalOutbox,
            "AuthorizedRealtimeGroups" or "communication.authorizedGroups.required" => AuthorizedRealtimeGroups,
            "TasksNotificationsV1" => TasksNotificationsV1,
            _ => featureKey.Trim()
        };
    }
}

public interface IFeatureFlagService
{
    Task<bool> IsEnabledAsync(string featureKey, CancellationToken cancellationToken = default);

    Task<Result> RequireEnabledAsync(string featureKey, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetEnabledFeaturesAsync(Guid tenantId, CancellationToken cancellationToken = default);
}
