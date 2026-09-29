using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Notifications;
using Coglatas.Domain.Enums;

namespace Coglatas.Tests.Notifications;

public sealed class NotificationApplicationServiceTests
{
    [Fact]
    public async Task UserCannotReadAnotherUsersNotification()
    {
        var fixture = NotificationFixture.Create();
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        fixture.Current.UserIdValue = other;
        var notificationId = fixture.Notifications.Add(owner, NotificationType.System, "Private notice", false);

        var result = await fixture.Service.MarkAsReadAsync(notificationId);

        Assert.False(result.IsSuccess);
        Assert.False(fixture.Notifications.Items.Single().IsRead);
    }

    [Fact]
    public async Task UnreadCountExcludesReadNotifications()
    {
        var fixture = NotificationFixture.Create();
        var user = Guid.NewGuid();
        fixture.Current.UserIdValue = user;
        fixture.Notifications.Add(user, NotificationType.System, "Unread", false);
        fixture.Notifications.Add(user, NotificationType.System, "Read", true);
        fixture.Notifications.Add(Guid.NewGuid(), NotificationType.System, "Someone else", false);

        var result = await fixture.Service.GetUnreadCountAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value!.UnreadCount);
    }

    [Fact]
    public async Task OpenFailsClosedWhenOwnedTargetIsNoLongerAuthorized()
    {
        var fixture = NotificationFixture.Create();
        var user = Guid.NewGuid();
        fixture.Current.UserIdValue = user;
        fixture.OpenService.Resolution = new NotificationTargetResolution(
            IsOwned: true,
            IsAvailable: false,
            Route: null,
            StateVersion: 7);

        var result = await fixture.Service.OpenAsync(Guid.NewGuid());

        Assert.False(result.IsSuccess);
    }

    private sealed class NotificationFixture
    {
        private NotificationFixture()
        {
            Service = new NotificationApplicationService(Current, Tenant, Clock, Notifications, OpenService, Audit, UnitOfWork);
        }

        public FakeCurrentUser Current { get; } = new();
        private FakeClock Clock { get; } = new();
        private FakeCurrentTenant Tenant { get; } = new();
        public FakeNotifications Notifications { get; } = new();
        public FakeNotificationOpenService OpenService { get; } = new();
        private FakeAuditLogger Audit { get; } = new();
        private FakeUnitOfWork UnitOfWork { get; } = new();
        public NotificationApplicationService Service { get; }

        public static NotificationFixture Create() => new();
    }

    private sealed class FakeNotifications : INotificationService
    {
        public List<Item> Items { get; } = [];

        public Guid Add(Guid userId, NotificationType type, string title, bool isRead)
        {
            var item = new Item(Guid.NewGuid(), userId, type, title, isRead);
            Items.Add(item);
            return item.Id;
        }

        public Task<bool> MarkAsReadAsync(Guid userId, Guid notificationId, CancellationToken cancellationToken = default)
        {
            var item = Items.FirstOrDefault(candidate => candidate.Id == notificationId && candidate.UserId == userId);
            if (item is null)
            {
                return Task.FromResult(false);
            }

            item.IsRead = true;
            return Task.FromResult(true);
        }

        public Task<int> GetUnreadCountAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Items.Count(item => item.UserId == userId && !item.IsRead));
        }

        public Task NotifyAsync(Guid recipientUserId, string title, string? body, string sourceType, Guid sourceId, CancellationToken cancellationToken = default)
        {
            Add(recipientUserId, NotificationType.System, title, false);
            return Task.CompletedTask;
        }

        public sealed class Item(Guid id, Guid userId, NotificationType type, string title, bool isRead)
        {
            public Guid Id { get; } = id;
            public Guid UserId { get; } = userId;
            public NotificationType Type { get; } = type;
            public string Title { get; } = title;
            public bool IsRead { get; set; } = isRead;
        }
    }

    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid? UserIdValue { get; set; }
        public Guid? UserId => UserIdValue;
        public Guid? SessionId => null;
        public string? Email => null;
        public SystemRole? SystemRole => null;
        public bool IsAuthenticated => UserIdValue.HasValue;
    }

    private sealed class FakeCurrentTenant : ICurrentTenant
    {
        public Guid TenantId => Guid.NewGuid();
        public bool IsAvailable => true;
        public string? TenantSlug => "test";
        public bool IsPlatformScope => false;
    }

    private sealed class FakeNotificationOpenService : INotificationOpenService
    {
        public NotificationTargetResolution Resolution { get; set; } = new(false, false, null, 0);

        public Task<NotificationTargetResolution> OpenAsync(Guid tenantId, Guid userId, Guid notificationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Resolution);
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 6, 6, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class FakeAuditLogger : IAuditLogger
    {
        public Task LogAsync(AuditLogEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeUnitOfWork : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(1);
    }
}
