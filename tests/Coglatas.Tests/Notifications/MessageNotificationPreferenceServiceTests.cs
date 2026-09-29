using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Notifications;
using Coglatas.Domain.Enums;

namespace Coglatas.Tests.Notifications;

public sealed class MessageNotificationPreferenceServiceTests
{
    [Fact]
    public async Task AuthenticatedTenantUserReadsAndUpdatesOnlyTheirScopedPreference()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var store = new RecordingStore(enabled: true);
        var service = new MessageNotificationPreferenceService(
            new TestCurrentUser(userId),
            new TestCurrentTenant(tenantId),
            FixedClock.Instance,
            store);

        var current = await service.GetAsync();
        var updated = await service.UpdateAsync(new UpdateMessageNotificationPreferenceRequest(false));

        Assert.True(current.IsSuccess, current.Error);
        Assert.True(current.Value!.MessageNotificationsEnabled);
        Assert.True(updated.IsSuccess, updated.Error);
        Assert.False(updated.Value!.MessageNotificationsEnabled);
        Assert.All(store.Calls, call =>
        {
            Assert.Equal(tenantId, call.TenantId);
            Assert.Equal(userId, call.UserId);
        });
        Assert.Equal(FixedClock.Instance.UtcNow, store.Calls.Single(call => call.Operation == "set").UpdatedAt);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task UnauthenticatedOrPlatformScopeFailsClosedWithoutReadingTheStore(
        bool authenticated,
        bool platformScope)
    {
        var userId = authenticated ? Guid.NewGuid() : (Guid?)null;
        var store = new RecordingStore(enabled: true);
        var service = new MessageNotificationPreferenceService(
            new TestCurrentUser(userId),
            new TestCurrentTenant(Guid.NewGuid(), platformScope),
            FixedClock.Instance,
            store);

        var result = await service.GetAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal("Message notification preferences are unavailable.", result.Error);
        Assert.Empty(store.Calls);
    }

    private sealed class RecordingStore(bool enabled) : IMessageNotificationPreferenceStore
    {
        private bool _enabled = enabled;

        public List<Call> Calls { get; } = [];

        public Task<bool?> GetEnabledAsync(
            Guid tenantId,
            Guid userId,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new Call("get", tenantId, userId, null));
            return Task.FromResult<bool?>(_enabled);
        }

        public Task<bool> SetEnabledAsync(
            Guid tenantId,
            Guid userId,
            bool value,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new Call("set", tenantId, userId, updatedAt));
            _enabled = value;
            return Task.FromResult(true);
        }
    }

    private sealed record Call(string Operation, Guid TenantId, Guid UserId, DateTimeOffset? UpdatedAt);

    private sealed class TestCurrentUser(Guid? userId) : ICurrentUser
    {
        public Guid? UserId { get; } = userId;
        public Guid? SessionId => null;
        public string? Email => null;
        public SystemRole? SystemRole => null;
        public bool IsAuthenticated => UserId.HasValue;
    }

    private sealed class TestCurrentTenant(Guid tenantId, bool platformScope = false) : ICurrentTenant
    {
        public Guid TenantId { get; } = tenantId;
        public bool IsAvailable => true;
        public string TenantSlug => "test";
        public bool IsPlatformScope { get; } = platformScope;
    }

    private sealed class FixedClock : IClock
    {
        public static FixedClock Instance { get; } = new();
        public DateTimeOffset UtcNow => new(2026, 8, 23, 5, 45, 0, TimeSpan.Zero);
    }
}
