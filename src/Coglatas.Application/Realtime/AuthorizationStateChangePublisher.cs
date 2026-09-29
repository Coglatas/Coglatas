using System.Text.Json;
using Coglatas.Application.Common.Interfaces;

namespace Coglatas.Application.Realtime;

public interface IAuthorizationStateChangePublisher
{
    Task PublishAsync(
        Guid tenantId,
        Guid affectedUserId,
        string scopeType,
        Guid? scopeId,
        string change,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Emits a metadata-only invalidation in the caller's business transaction.
/// The dispatcher re-evaluates authorization before any later protected event.
/// </summary>
public sealed class RequiredOutboxStagingException : Exception
{
    public RequiredOutboxStagingException()
        : base("A required transactional Outbox event could not be staged.")
    {
    }
}

public sealed class AuthorizationStateChangePublisher(
    ITransactionalOutbox outbox,
    ICurrentTenant currentTenant,
    IClock clock) : IAuthorizationStateChangePublisher
{
    public async Task PublishAsync(
        Guid tenantId,
        Guid affectedUserId,
        string scopeType,
        Guid? scopeId,
        string change,
        CancellationToken cancellationToken = default)
    {
        if (!currentTenant.IsAvailable || currentTenant.TenantId != tenantId || affectedUserId == Guid.Empty)
        {
            return;
        }

        var now = clock.UtcNow;
        var authorizationVersion = now.UtcDateTime.Ticks;
        var payload = JsonSerializer.SerializeToElement(new
        {
            affectedUserId,
            scopeType,
            scopeId,
            change,
            authorizationVersion
        });
        var enqueue = await outbox.EnqueueAsync(
            new DurableEventEnvelope(
                Guid.NewGuid(),
                "Security.AuthorizationStateChanged.v1",
                RealtimeEventCatalog.PayloadSchemaVersion1,
                now,
                tenantId,
                "AuthorizationState",
                affectedUserId,
                authorizationVersion,
                RealtimeActor.System(),
                null,
                null,
                payload),
            [new RealtimeRoutingTarget(RealtimeSubscriptionType.User, affectedUserId)],
            cancellationToken);
        if (!enqueue.IsSuccess)
        {
            throw new RequiredOutboxStagingException();
        }
    }
}
