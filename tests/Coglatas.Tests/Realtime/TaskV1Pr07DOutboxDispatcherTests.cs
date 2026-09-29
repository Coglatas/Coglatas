using System.Reflection;
using System.Text.Json;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Realtime;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Web.Realtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Coglatas.Tests.Realtime;

[Trait("Scope", "TaskV1PR07D")]
public sealed class TaskV1Pr07DOutboxDispatcherTests
{
    [Fact]
    public async Task AuthorizationSuppressionDoesNotRetryToDeadLetter()
    {
        await using var fixture = DispatcherFixture.Create();

        await fixture.DispatchAsync();

        Assert.Equal(1, fixture.Authorizer.Calls);
        Assert.Equal(["NoAuthorizedRecipient"], fixture.Repository.DeliveredOutcomes);
        Assert.Equal(0, fixture.Repository.MarkFailureCalls);
        Assert.Equal(OutboxEventStatus.Delivered, fixture.Repository.Event.Status);
        Assert.Null(fixture.Repository.Event.DeadLetteredAt);
    }

    [Fact]
    public async Task OutboxReplayDoesNotBypassCurrentAuthorization()
    {
        await using var fixture = DispatcherFixture.Create();

        await fixture.DispatchAsync();
        Assert.True(await fixture.Repository.ReplayAsync(fixture.Repository.Event.Id, DateTimeOffset.UtcNow));
        await fixture.DispatchAsync();

        Assert.Equal(2, fixture.Authorizer.Calls);
        Assert.Equal(["NoAuthorizedRecipient", "NoAuthorizedRecipient"], fixture.Repository.DeliveredOutcomes);
        Assert.Equal(0, fixture.Repository.MarkFailureCalls);
        Assert.Equal(OutboxEventStatus.Delivered, fixture.Repository.Event.Status);
        Assert.Null(fixture.Repository.Event.DeadLetteredAt);
    }

    private sealed class DispatcherFixture : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly OutboxDispatcher _dispatcher;

        private DispatcherFixture(
            ServiceProvider services,
            OutboxDispatcher dispatcher,
            RecordingOutboxRepository repository,
            DenyingDispatchAuthorizer authorizer)
        {
            _services = services;
            _dispatcher = dispatcher;
            Repository = repository;
            Authorizer = authorizer;
        }

        public RecordingOutboxRepository Repository { get; }

        public DenyingDispatchAuthorizer Authorizer { get; }

        public static DispatcherFixture Create()
        {
            var tenantId = Guid.NewGuid();
            var recipientId = Guid.NewGuid();
            var envelope = new DurableEventEnvelope(
                Guid.NewGuid(),
                "Notifications.NotificationCreated.v1",
                RealtimeEventCatalog.PayloadSchemaVersion1,
                DateTimeOffset.UtcNow,
                tenantId,
                "Notification",
                Guid.NewGuid(),
                1,
                RealtimeActor.System(),
                null,
                null,
                JsonSerializer.SerializeToElement(new
                {
                    notificationId = Guid.NewGuid(),
                    stateVersion = 1,
                    requiresRefetch = true
                }));
            // A valid envelope binds the reference-only payload to its aggregate.
            envelope = envelope with
            {
                Payload = JsonSerializer.SerializeToElement(new
                {
                    notificationId = envelope.AggregateId,
                    stateVersion = 1,
                    requiresRefetch = true
                })
            };
            var routing = new[] { new RealtimeRoutingTarget(RealtimeSubscriptionType.User, recipientId) };
            var eventItem = new OutboxEvent(envelope.EventId)
            {
                TenantId = tenantId,
                EventType = envelope.EventType,
                PayloadSchemaVersion = envelope.PayloadSchemaVersion,
                AggregateType = envelope.AggregateType,
                AggregateId = envelope.AggregateId,
                AggregateVersion = envelope.AggregateVersion,
                OccurredAt = envelope.OccurredAt,
                PayloadJson = JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                RoutingJson = JsonSerializer.Serialize(routing, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                Status = OutboxEventStatus.Pending,
                NextAttemptAt = DateTimeOffset.UtcNow
            };
            var repository = new RecordingOutboxRepository(eventItem);
            var authorizer = new DenyingDispatchAuthorizer();
            var tenant = new CurrentTenantService();
            var services = new ServiceCollection()
                .AddScoped<ICurrentTenantAccessor>(_ => tenant)
                .AddScoped<IOutboxEventRepository>(_ => repository)
                .AddScoped<IFeatureFlagService, EnabledOutboxFeatureFlags>()
                .AddScoped<IRealtimeDispatchAuthorizer>(_ => authorizer)
                .BuildServiceProvider();
            var registry = new HubSubscriptionRegistry();
            Assert.True(registry.TryAdd(
                new HubSubscription(
                    "connection-1",
                    recipientId,
                    Guid.NewGuid(),
                    tenantId,
                    RealtimeSubscriptionType.User,
                    recipientId,
                    $"user:{recipientId:D}",
                    DateTimeOffset.UtcNow),
                4));

            var dispatcher = new OutboxDispatcher(
                services.GetRequiredService<IServiceScopeFactory>(),
                null!,
                registry,
                new RealtimeDiagnostics(),
                Options.Create(new RealtimeOptions { DispatcherBatchSize = 1 }),
                NullLogger<OutboxDispatcher>.Instance);
            return new DispatcherFixture(services, dispatcher, repository, authorizer);
        }

        public async Task DispatchAsync()
        {
            var method = typeof(OutboxDispatcher).GetMethod(
                "DispatchBatchAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Outbox dispatcher batch method is unavailable.");
            var task = method.Invoke(_dispatcher, [CancellationToken.None]) as Task
                ?? throw new InvalidOperationException("Outbox dispatcher batch invocation did not return a task.");
            await task;
        }

        public ValueTask DisposeAsync() => _services.DisposeAsync();
    }

    private sealed class DenyingDispatchAuthorizer : IRealtimeDispatchAuthorizer
    {
        public int Calls { get; private set; }

        public Task<bool> CanReceiveAsync(
            HubSubscription subscription,
            RealtimeSubscriptionType targetType,
            Guid targetResourceId,
            DurableEventEnvelope envelope,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(false);
        }
    }

    private sealed class EnabledOutboxFeatureFlags : IFeatureFlagService
    {
        public Task<bool> IsEnabledAsync(string featureKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Equals(featureKey, FeatureKeys.TransactionalOutbox, StringComparison.Ordinal));

        public Task<Result> RequireEnabledAsync(string featureKey, CancellationToken cancellationToken = default) =>
            IsEnabledAsync(featureKey, cancellationToken).ContinueWith(
                task => task.Result ? Result.Success() : Result.Failure("Feature is disabled."),
                cancellationToken);

        public Task<IReadOnlyList<string>> GetEnabledFeaturesAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([FeatureKeys.TransactionalOutbox]);
    }

    private sealed class RecordingOutboxRepository(OutboxEvent eventItem) : IOutboxEventRepository
    {
        public OutboxEvent Event { get; } = eventItem;

        public List<string?> DeliveredOutcomes { get; } = [];

        public int MarkFailureCalls { get; private set; }

        public Task AddAsync(OutboxEvent eventItem, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<OutboxEvent>> ClaimDueAsync(
            string lockOwner,
            DateTimeOffset now,
            int batchSize,
            TimeSpan lockTimeout,
            CancellationToken cancellationToken = default)
        {
            if (Event.Status is not (OutboxEventStatus.Pending or OutboxEventStatus.RetryScheduled))
            {
                return Task.FromResult<IReadOnlyList<OutboxEvent>>([]);
            }

            Event.Status = OutboxEventStatus.Processing;
            Event.LockOwner = lockOwner;
            Event.LockToken = Guid.NewGuid();
            Event.LockedAt = now;
            return Task.FromResult<IReadOnlyList<OutboxEvent>>([Event]);
        }

        public Task<bool> MarkDeliveredAsync(
            Guid eventId,
            Guid lockToken,
            DateTimeOffset deliveredAt,
            string? outcomeCode,
            CancellationToken cancellationToken = default)
        {
            if (eventId != Event.Id || Event.LockToken != lockToken)
            {
                return Task.FromResult(false);
            }

            Event.Status = OutboxEventStatus.Delivered;
            Event.DeliveredAt = deliveredAt;
            Event.LastErrorCode = outcomeCode;
            Event.LockedAt = null;
            Event.LockOwner = null;
            Event.LockToken = null;
            DeliveredOutcomes.Add(outcomeCode);
            return Task.FromResult(true);
        }

        public Task<bool> MarkFailureAsync(
            Guid eventId,
            Guid lockToken,
            DateTimeOffset now,
            bool retryable,
            DateTimeOffset? nextAttemptAt,
            string errorCode,
            string errorSummary,
            int maximumAttempts,
            CancellationToken cancellationToken = default)
        {
            MarkFailureCalls++;
            Event.Status = OutboxEventStatus.DeadLetter;
            Event.DeadLetteredAt = now;
            return Task.FromResult(true);
        }

        public Task<bool> ReleaseAsync(Guid eventId, Guid lockToken, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<int> RecoverStaleLocksAsync(DateTimeOffset staleBefore, DateTimeOffset now, int maximumAttempts, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<int> CleanupAsync(DateTimeOffset deliveredBefore, DateTimeOffset deadLetterBefore, DateTimeOffset cancelledBefore, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<RealtimeOutboxDiagnostics> GetDiagnosticsAsync(DateTimeOffset staleBefore, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RealtimeOutboxDiagnostics(0, 0, 0, null, 0, 0, 0, 0));

        public Task<OutboxEvent?> GetByIdAsync(Guid eventId, CancellationToken cancellationToken = default) =>
            Task.FromResult(eventId == Event.Id ? Event : null);

        public Task<bool> ReplayAsync(Guid eventId, DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            if (eventId != Event.Id || Event.Status is OutboxEventStatus.Processing or OutboxEventStatus.Cancelled)
            {
                return Task.FromResult(false);
            }

            Event.Status = OutboxEventStatus.Pending;
            Event.NextAttemptAt = now;
            Event.DeadLetteredAt = null;
            Event.LastErrorCode = null;
            Event.LockedAt = null;
            Event.LockOwner = null;
            Event.LockToken = null;
            return Task.FromResult(true);
        }
    }
}
