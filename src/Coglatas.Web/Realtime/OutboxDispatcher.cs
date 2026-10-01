using System.Text.Json;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Realtime;
using Coglatas.Domain.Entities;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Coglatas.Web.Realtime;

public sealed class OutboxDispatcher(
    IServiceScopeFactory scopeFactory,
    IHubContext<AppHub> hubContext,
    HubSubscriptionRegistry subscriptions,
    RealtimeDiagnostics diagnostics,
    IOptions<RealtimeOptions> options,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _lockOwner = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                diagnostics.RecordDispatcherFailure();
                logger.LogError(exception, "Realtime outbox dispatcher batch failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.Value.DispatcherPollSeconds)), stoppingToken);
        }
    }

    private async Task DispatchBatchAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<OutboxEvent> events;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var tenant = scope.ServiceProvider.GetRequiredService<ICurrentTenantAccessor>();
            tenant.SetPlatformScope();
            var repository = scope.ServiceProvider.GetRequiredService<IOutboxEventRepository>();
            var now = DateTimeOffset.UtcNow;
            var configured = options.Value;
            events = await repository.ClaimDueAsync(
                _lockOwner,
                now,
                configured.DispatcherBatchSize,
                TimeSpan.FromSeconds(Math.Max(1, configured.ProcessingLockSeconds)),
                cancellationToken);
        }

        foreach (var eventItem in events)
        {
            await DispatchEventAsync(eventItem, cancellationToken);
        }
    }

    private async Task DispatchEventAsync(OutboxEvent eventItem, CancellationToken cancellationToken)
    {
        if (!eventItem.LockToken.HasValue)
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var tenant = scope.ServiceProvider.GetRequiredService<ICurrentTenantAccessor>();
        tenant.SetTenant(eventItem.TenantId, "outbox");
        var repository = scope.ServiceProvider.GetRequiredService<IOutboxEventRepository>();
        var featureFlags = scope.ServiceProvider.GetRequiredService<IFeatureFlagService>();
        var configured = options.Value;
        var now = DateTimeOffset.UtcNow;

        if (!await featureFlags.IsEnabledAsync(FeatureKeys.TransactionalOutbox, cancellationToken))
        {
            await repository.ReleaseAsync(eventItem.Id, eventItem.LockToken.Value, now.AddSeconds(Math.Max(1, configured.DispatcherPollSeconds)), cancellationToken);
            return;
        }

        if (!TryRead(eventItem, out var envelope, out var routingTargets))
        {
            await repository.MarkFailureAsync(eventItem.Id, eventItem.LockToken.Value, now, false, null, "InvalidOutboxContract", "The durable event envelope or routing contract was invalid.", configured.MaximumAutomaticAttempts, cancellationToken);
            diagnostics.RecordDispatchFailure();
            return;
        }

        try
        {
            var delivered = await DeliverAuthorizedAsync(eventItem, envelope!, routingTargets!, scope.ServiceProvider, cancellationToken);
            await repository.MarkDeliveredAsync(eventItem.Id, eventItem.LockToken.Value, DateTimeOffset.UtcNow, delivered ? null : "NoAuthorizedRecipient", cancellationToken);
            diagnostics.RecordDispatchSuccess();
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            var retryable = exception is not JsonException && exception is not InvalidOperationException;
            var attempt = eventItem.AttemptCount + 1;
            DateTimeOffset? nextAttempt = retryable ? CalculateNextAttempt(DateTimeOffset.UtcNow, attempt, configured) : null;
            await repository.MarkFailureAsync(
                eventItem.Id,
                eventItem.LockToken.Value,
                DateTimeOffset.UtcNow,
                retryable,
                nextAttempt,
                retryable ? "DispatchTransientFailure" : "DispatchContractFailure",
                retryable ? "Realtime delivery could not complete." : "Realtime delivery contract validation failed.",
                configured.MaximumAutomaticAttempts,
                cancellationToken);
            diagnostics.RecordDispatchFailure();
            logger.LogWarning(exception, "Realtime outbox dispatch failed for event {EventId} ({EventType}).", eventItem.Id, eventItem.EventType);
        }
    }

    private async Task<bool> DeliverAuthorizedAsync(
        OutboxEvent eventItem,
        DurableEventEnvelope envelope,
        IReadOnlyList<RealtimeRoutingTarget> routingTargets,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        var authorizer = serviceProvider.GetRequiredService<IRealtimeDispatchAuthorizer>();
        var deliveredConnectionIds = new HashSet<string>(StringComparer.Ordinal);
        var authorizationInvalidation = envelope.EventType == "Security.AuthorizationStateChanged.v1";
        foreach (var target in routingTargets)
        {
            foreach (var subscription in subscriptions.GetForTarget(eventItem.TenantId, target.SubscriptionType, target.ResourceId))
            {
                if (deliveredConnectionIds.Contains(subscription.ConnectionId) ||
                    !await authorizer.CanReceiveAsync(subscription, target.SubscriptionType, target.ResourceId, envelope, cancellationToken))
                {
                    continue;
                }

                deliveredConnectionIds.Add(subscription.ConnectionId);
                await hubContext.Clients.Client(subscription.ConnectionId).SendAsync("DurableEvent", envelope, cancellationToken);
            }
        }

        // Account/membership invalidation is metadata-only. It is intentionally
        // sent before removing the old groups, so a revoked browser can clear
        // protected state; subsequent protected delivery must reauthorize.
        if (authorizationInvalidation)
        {
            foreach (var target in routingTargets.Where(target => target.SubscriptionType == RealtimeSubscriptionType.User))
            {
                foreach (var subscription in subscriptions.RemoveForUser(eventItem.TenantId, target.ResourceId))
                {
                    await hubContext.Groups.RemoveFromGroupAsync(subscription.ConnectionId, subscription.CanonicalGroupName, cancellationToken);
                }
            }
        }

        return deliveredConnectionIds.Count > 0;
    }

    private static bool TryRead(OutboxEvent eventItem, out DurableEventEnvelope? envelope, out IReadOnlyList<RealtimeRoutingTarget>? routingTargets)
    {
        envelope = null;
        routingTargets = null;
        try
        {
            envelope = JsonSerializer.Deserialize<DurableEventEnvelope>(eventItem.PayloadJson, JsonOptions);
            routingTargets = JsonSerializer.Deserialize<List<RealtimeRoutingTarget>>(eventItem.RoutingJson, JsonOptions);
            return envelope is not null &&
                routingTargets is { Count: > 0 } &&
                envelope.EventId == eventItem.Id &&
                envelope.TenantId == eventItem.TenantId &&
                envelope.EventType == eventItem.EventType &&
                envelope.PayloadSchemaVersion == eventItem.PayloadSchemaVersion &&
                envelope.AggregateId == eventItem.AggregateId &&
                envelope.AggregateType == eventItem.AggregateType &&
                RealtimeEventCatalog.IsSupported(envelope.EventType, envelope.PayloadSchemaVersion);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static DateTimeOffset CalculateNextAttempt(DateTimeOffset now, int attempt, RealtimeOptions options)
    {
        var initialSeconds = Math.Max(1, options.InitialRetrySeconds);
        var maximumSeconds = TimeSpan.FromMinutes(Math.Max(1, options.MaximumRetryMinutes)).TotalSeconds;
        var exponentialSeconds = Math.Min(maximumSeconds, initialSeconds * Math.Pow(2, Math.Max(0, attempt - 1)));
        var jitter = 0.8 + Random.Shared.NextDouble() * 0.4;
        return now.AddSeconds(Math.Min(maximumSeconds, exponentialSeconds * jitter));
    }
}
