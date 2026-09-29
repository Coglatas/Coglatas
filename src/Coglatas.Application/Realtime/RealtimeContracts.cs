using System.Text.Json;
using Coglatas.Application.Common;
using Coglatas.Domain.Entities;

namespace Coglatas.Application.Realtime;

public static class RealtimeEventCatalog
{
    public const int PayloadSchemaVersion1 = 1;

    public static readonly IReadOnlySet<string> EventTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "Messaging.MessageCreated.v1",
        "Messaging.MessageUpdated.v1",
        "Messaging.MessageDeleted.v1",
        "Messaging.ThreadChanged.v1",
        "Messaging.ConversationUnreadChanged.v1",
        "Notifications.NotificationCreated.v1",
        "Notifications.NotificationReadStateChanged.v1",
        "Projects.TaskChanged.v1",
        "Projects.TaskAssignmentChanged.v1",
        "Projects.TaskWorkflowChanged.v1",
        "Projects.TaskCommentChanged.v1",
        "Projects.ProjectChanged.v1",
        "Announcements.AnnouncementChanged.v1",
        "Files.FileChanged.v1",
        "Security.AuthorizationStateChanged.v1"
    };

    public static bool IsSupported(string eventType, int payloadSchemaVersion)
    {
        return payloadSchemaVersion == PayloadSchemaVersion1 && EventTypes.Contains(eventType);
    }
}

public sealed record RealtimeActor(string ActorType, Guid? ActorId)
{
    public static RealtimeActor System() => new("System", null);
}

public sealed record DurableEventEnvelope(
    Guid EventId,
    string EventType,
    int PayloadSchemaVersion,
    DateTimeOffset OccurredAt,
    Guid TenantId,
    string AggregateType,
    Guid AggregateId,
    long? AggregateVersion,
    RealtimeActor Actor,
    string? CorrelationId,
    string? CausationId,
    JsonElement Payload);

public enum RealtimeSubscriptionType
{
    User,
    Tenant,
    Workspace,
    Conversation,
    Project
}

public sealed record RealtimeRoutingTarget(RealtimeSubscriptionType SubscriptionType, Guid ResourceId);

public sealed record RealtimeOutboxDiagnostics(
    int PendingCount,
    int RetryScheduledCount,
    int DeadLetterCount,
    DateTimeOffset? OldestPendingAt,
    int StaleProcessingCount,
    long DispatchSuccessCount,
    long DispatchFailureCount,
    long SubscriptionDenialCount);

public interface ITransactionalOutbox
{
    Task<Result<Guid>> EnqueueAsync(
        DurableEventEnvelope envelope,
        IReadOnlyCollection<RealtimeRoutingTarget> routingTargets,
        CancellationToken cancellationToken = default);
}

public interface IOutboxEventRepository
{
    Task AddAsync(OutboxEvent eventItem, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OutboxEvent>> ClaimDueAsync(
        string lockOwner,
        DateTimeOffset now,
        int batchSize,
        TimeSpan lockTimeout,
        CancellationToken cancellationToken = default);
    Task<bool> MarkDeliveredAsync(Guid eventId, Guid lockToken, DateTimeOffset deliveredAt, string? outcomeCode, CancellationToken cancellationToken = default);
    Task<bool> MarkFailureAsync(
        Guid eventId,
        Guid lockToken,
        DateTimeOffset now,
        bool retryable,
        DateTimeOffset? nextAttemptAt,
        string errorCode,
        string errorSummary,
        int maximumAttempts,
        CancellationToken cancellationToken = default);
    Task<bool> ReleaseAsync(Guid eventId, Guid lockToken, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken = default);
    Task<int> RecoverStaleLocksAsync(DateTimeOffset staleBefore, DateTimeOffset now, int maximumAttempts, CancellationToken cancellationToken = default);
    Task<int> CleanupAsync(DateTimeOffset deliveredBefore, DateTimeOffset deadLetterBefore, DateTimeOffset cancelledBefore, CancellationToken cancellationToken = default);
    Task<RealtimeOutboxDiagnostics> GetDiagnosticsAsync(DateTimeOffset staleBefore, CancellationToken cancellationToken = default);
    Task<OutboxEvent?> GetByIdAsync(Guid eventId, CancellationToken cancellationToken = default);
    Task<bool> ReplayAsync(Guid eventId, DateTimeOffset now, CancellationToken cancellationToken = default);
}

public interface IOutboxReplayService
{
    Task<Result> ReplayAsync(Guid eventId, string reason, CancellationToken cancellationToken = default);
}
