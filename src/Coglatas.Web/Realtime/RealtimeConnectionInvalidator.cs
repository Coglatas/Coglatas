using Microsoft.AspNetCore.SignalR;

namespace Coglatas.Web.Realtime;

public interface IRealtimeConnectionInvalidator
{
    Task InvalidateUserAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken = default);
}

public sealed class RealtimeConnectionInvalidator(
    HubSubscriptionRegistry registry,
    IHubContext<AppHub> hubContext) : IRealtimeConnectionInvalidator
{
    public async Task InvalidateUserAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken = default)
    {
        var subscriptions = registry.RemoveForUser(tenantId, userId);
        foreach (var subscription in subscriptions)
        {
            await hubContext.Groups.RemoveFromGroupAsync(subscription.ConnectionId, subscription.CanonicalGroupName, cancellationToken);
            await hubContext.Clients.Client(subscription.ConnectionId).SendAsync("AuthorizationInvalidated", cancellationToken);
        }
    }
}
