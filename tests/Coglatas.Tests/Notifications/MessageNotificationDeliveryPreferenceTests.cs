using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Notifications;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Tests.Notifications;

public sealed class MessageNotificationDeliveryPreferenceTests
{
    [Fact]
    public async Task MutedConversationSuppressesMessageNotificationCreation()
    {
        await using var fixture = CreateFixture(globalEnabled: true, conversationMuted: true);

        await fixture.Service.NotifyAsync(
            fixture.RecipientUserId,
            "New direct message",
            "You have a new message.",
            "Message",
            fixture.Message.Id);

        Assert.Empty(fixture.Db.Notifications.Local);
    }

    [Fact]
    public async Task GlobalMessageNotificationOffSuppressesUnmutedConversationNotification()
    {
        await using var fixture = CreateFixture(globalEnabled: false, conversationMuted: false);

        await fixture.Service.NotifyAsync(
            fixture.RecipientUserId,
            "New direct message",
            "You have a new message.",
            "Message",
            fixture.Message.Id);

        Assert.Empty(fixture.Db.Notifications.Local);
    }

    [Fact]
    public async Task GlobalOnAndUnmutedConversationCreatesRecipientNotification()
    {
        await using var fixture = CreateFixture(globalEnabled: true, conversationMuted: false);

        await fixture.Service.NotifyAsync(
            fixture.RecipientUserId,
            "New direct message",
            "You have a new message.",
            "Message",
            fixture.Message.Id);

        var notification = Assert.Single(fixture.Db.Notifications.Local);
        Assert.Equal(fixture.RecipientUserId, notification.UserId);
        Assert.Equal("Message", notification.RelatedEntityType);
        Assert.Equal(fixture.Message.Id, notification.RelatedEntityId);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task MessageMentionRespectsGlobalAndConversationNotificationSuppression(
        bool globalEnabled,
        bool conversationMuted)
    {
        await using var fixture = CreateFixture(globalEnabled, conversationMuted);

        var notificationId = await fixture.Service.CreateAsync(
            fixture.RecipientUserId,
            NotificationType.Mention,
            "You were mentioned in a message",
            "Open the conversation to review the mention.",
            "Message",
            fixture.Message.Id);

        Assert.Equal(Guid.Empty, notificationId);
        Assert.Empty(fixture.Db.Notifications.Local);
    }

    [Fact]
    public async Task MessageMentionIsCreatedWhenGlobalAndConversationNotificationsAllowIt()
    {
        await using var fixture = CreateFixture(globalEnabled: true, conversationMuted: false);

        var notificationId = await fixture.Service.CreateAsync(
            fixture.RecipientUserId,
            NotificationType.Mention,
            "You were mentioned in a message",
            "Open the conversation to review the mention.",
            "Message",
            fixture.Message.Id);

        var notification = Assert.Single(fixture.Db.Notifications.Local);
        Assert.Equal(notification.Id, notificationId);
        Assert.Equal(NotificationType.Mention, notification.NotificationType);
        Assert.Equal(fixture.Message.Id, notification.RelatedEntityId);
    }

    private static Fixture CreateFixture(bool globalEnabled, bool conversationMuted)
    {
        var tenantId = Guid.NewGuid();
        var currentTenant = new TestCurrentTenant(tenantId);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"message-notification-preferences-{Guid.NewGuid():N}")
            .Options;
        var db = new AppDbContext(options, currentTenant);
        var recipientUserId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var message = new Message
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            WorkspaceId = Guid.NewGuid(),
            ConversationId = conversationId,
            AuthorUserId = Guid.NewGuid(),
            Body = "hello"
        };
        var member = new ConversationMember
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ConversationId = conversationId,
            UserId = recipientUserId,
            CanRead = true,
            IsMuted = conversationMuted,
            JoinedAt = FixedClock.Instance.UtcNow
        };
        db.Messages.Add(message);
        db.ConversationMembers.Add(member);

        var preferenceStore = new StubPreferenceStore(globalEnabled);
        var inner = new DbNotificationService(db, FixedClock.Instance, currentTenant);
        var service = new PreferenceAwareNotificationService(inner, db, preferenceStore, currentTenant);
        return new Fixture(db, service, message, recipientUserId);
    }

    private sealed record Fixture(
        AppDbContext Db,
        PreferenceAwareNotificationService Service,
        Message Message,
        Guid RecipientUserId) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class StubPreferenceStore(bool enabled) : IMessageNotificationPreferenceStore
    {
        public Task<bool?> GetEnabledAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<bool?>(enabled);

        public Task<bool> SetEnabledAsync(
            Guid tenantId,
            Guid userId,
            bool value,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class TestCurrentTenant(Guid tenantId) : ICurrentTenant
    {
        public Guid TenantId { get; } = tenantId;
        public bool IsAvailable => true;
        public string TenantSlug => "test";
        public bool IsPlatformScope => false;
    }

    private sealed class FixedClock : IClock
    {
        public static FixedClock Instance { get; } = new();
        public DateTimeOffset UtcNow => new(2026, 8, 23, 5, 45, 0, TimeSpan.Zero);
    }
}
