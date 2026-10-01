using System.Collections.Concurrent;
using Coglatas.Application.Realtime;

namespace Coglatas.Web.Realtime;

public sealed record HubSubscription(
    string ConnectionId,
    Guid UserId,
    Guid SessionId,
    Guid TenantId,
    RealtimeSubscriptionType SubscriptionType,
    Guid ResourceId,
    string CanonicalGroupName,
    DateTimeOffset JoinedAt);

public sealed class HubSubscriptionRegistry
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, HubSubscription>> _subscriptions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<DateTimeOffset>> _attempts = new(StringComparer.Ordinal);

    public bool TryRecordAttempt(string connectionId, DateTimeOffset now, int perMinuteLimit)
    {
        var queue = _attempts.GetOrAdd(connectionId, _ => new ConcurrentQueue<DateTimeOffset>());
        while (queue.TryPeek(out var oldest) && oldest <= now.AddMinutes(-1))
        {
            queue.TryDequeue(out _);
        }

        if (queue.Count >= perMinuteLimit)
        {
            return false;
        }

        queue.Enqueue(now);
        return true;
    }

    public bool TryAdd(HubSubscription subscription, int maximumSubscriptions)
    {
        var connectionSubscriptions = _subscriptions.GetOrAdd(subscription.ConnectionId, _ => new ConcurrentDictionary<string, HubSubscription>(StringComparer.Ordinal));
        var key = Key(subscription.SubscriptionType, subscription.ResourceId);
        if (connectionSubscriptions.ContainsKey(key))
        {
            return true;
        }

        return connectionSubscriptions.Count < maximumSubscriptions && connectionSubscriptions.TryAdd(key, subscription);
    }

    public bool TryRemove(string connectionId, RealtimeSubscriptionType subscriptionType, Guid resourceId, out HubSubscription? subscription)
    {
        subscription = null;
        return _subscriptions.TryGetValue(connectionId, out var connectionSubscriptions) &&
            connectionSubscriptions.TryRemove(Key(subscriptionType, resourceId), out subscription);
    }

    public IReadOnlyList<HubSubscription> GetForTarget(Guid tenantId, RealtimeSubscriptionType subscriptionType, Guid resourceId)
    {
        return _subscriptions.Values
            .SelectMany(connectionSubscriptions => connectionSubscriptions.Values)
            .Where(subscription =>
                subscription.TenantId == tenantId &&
                subscription.SubscriptionType == subscriptionType &&
                subscription.ResourceId == resourceId)
            .ToList();
    }

    public IReadOnlyList<HubSubscription> RemoveConnection(string connectionId)
    {
        _attempts.TryRemove(connectionId, out _);
        return _subscriptions.TryRemove(connectionId, out var connectionSubscriptions)
            ? connectionSubscriptions.Values.ToList()
            : [];
    }

    public IReadOnlyList<HubSubscription> RemoveForUser(Guid tenantId, Guid userId)
    {
        var removed = new List<HubSubscription>();
        foreach (var (connectionId, connectionSubscriptions) in _subscriptions)
        {
            foreach (var (key, subscription) in connectionSubscriptions)
            {
                if (subscription.TenantId == tenantId && subscription.UserId == userId && connectionSubscriptions.TryRemove(key, out var item))
                {
                    removed.Add(item);
                }
            }

            if (connectionSubscriptions.IsEmpty)
            {
                _subscriptions.TryRemove(connectionId, out _);
            }
        }

        return removed;
    }

    private static string Key(RealtimeSubscriptionType type, Guid resourceId) => $"{type}:{resourceId:D}";
}
