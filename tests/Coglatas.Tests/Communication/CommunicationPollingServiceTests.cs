using System.Text.Json;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Communication;
using Coglatas.Application.Messaging;
using Coglatas.Application.Notifications;
using Coglatas.Application.Projects;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;

namespace Coglatas.Tests.Communication;

public sealed class CommunicationPollingServiceTests
{
    [Fact]
    public async Task ActiveParticipantReceivesOwnUnreadCountWithoutMessageBodyOrPrivateState()
    {
        var fixture = PollingFixture.Create();
        var conversation = fixture.AddDirectConversation(fixture.UserId, fixture.OtherUserId);
        fixture.AddMessage(conversation, fixture.OtherUserId, "DM body token storageKey StudentRecordRestricted", fixture.Clock.UtcNow.AddMinutes(-1));

        var result = await fixture.Service.GetUnreadCountsAsync(new CommunicationPollingQuery(PageSize: 10_000));

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Value!.Items);
        Assert.Equal(conversation.Id, item.ConversationId);
        Assert.Equal(1, item.UnreadCount);
        Assert.True(item.HasUnread);
        Assert.Equal(100, result.Value.PageSize);

        var json = JsonSerializer.Serialize(result.Value);
        Assert.DoesNotContain("DM body", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("storageKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("StudentRecordRestricted", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LastRead", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.OtherUserId.ToString(), json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RemovedParticipantReceivesNoUnreadCountEvenIfRepositoryReturnsConversation()
    {
        var fixture = PollingFixture.Create();
        var conversation = fixture.AddDirectConversation(fixture.UserId, fixture.OtherUserId);
        fixture.Members.Single(member => member.UserId == fixture.UserId).RemovedAt = fixture.Clock.UtcNow;
        fixture.IncludeRemovedConversationsInList = true;
        fixture.AddMessage(conversation, fixture.OtherUserId, "Removed user should not see this.", fixture.Clock.UtcNow);

        var result = await fixture.Service.GetUnreadCountsAsync(new CommunicationPollingQuery());

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!.Items);
        Assert.Contains(fixture.Audit.Entries, entry => JsonSerializer.Serialize(entry.Metadata).Contains("participant_missing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AdminNonParticipantGetsSafePlaceholderForDmNotification()
    {
        var fixture = PollingFixture.Create(SystemRole.Admin);
        var conversation = fixture.AddDirectConversation(fixture.OtherUserId, Guid.NewGuid());
        var message = fixture.AddMessage(conversation, fixture.OtherUserId, "Private DM body signedUrl storage path", fixture.Clock.UtcNow);
        fixture.Notifications.Add(new NotificationListItemResponse(
            Guid.NewGuid(),
            fixture.UserId,
            NotificationType.DirectMessage,
            "Private DM title",
            "Private DM body",
            "Message",
            message.Id,
            false,
            fixture.Clock.UtcNow,
            null,
            "/messages/private"));

        var result = await fixture.Service.GetNotificationsAsync(new CommunicationPollingQuery());

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Value!.Items);
        Assert.Equal("Inaccessible", item.TargetType);
        Assert.Null(item.TargetId);
        Assert.Null(item.ConversationId);
        Assert.Equal("Notification unavailable", item.Title);

        var json = JsonSerializer.Serialize(result.Value);
        Assert.DoesNotContain("Private DM body", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("signedUrl", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("storage path", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DmParticipantGetsSafeNotificationMetadataOnly()
    {
        var fixture = PollingFixture.Create();
        var conversation = fixture.AddDirectConversation(fixture.UserId, fixture.OtherUserId);
        var message = fixture.AddMessage(conversation, fixture.OtherUserId, "Do not return this message body", fixture.Clock.UtcNow);
        fixture.Notifications.Add(new NotificationListItemResponse(
            Guid.NewGuid(),
            fixture.UserId,
            NotificationType.DirectMessage,
            "Leaky direct-message title",
            "Leaky direct-message body",
            "Message",
            message.Id,
            false,
            fixture.Clock.UtcNow,
            null,
            "/messages/private"));

        var result = await fixture.Service.GetNotificationsAsync(new CommunicationPollingQuery());

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Value!.Items);
        Assert.Equal("Message", item.TargetType);
        Assert.Equal(message.Id, item.TargetId);
        Assert.Equal(conversation.Id, item.ConversationId);
        Assert.Equal("New message", item.Title);
        Assert.Equal("A conversation has new activity.", item.Summary);

        var json = JsonSerializer.Serialize(result.Value);
        Assert.DoesNotContain("Leaky direct-message", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Do not return", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdatesRejectCursorForAnotherActorWithoutLeakingData()
    {
        var fixture = PollingFixture.Create();
        fixture.AddDirectConversation(fixture.UserId, fixture.OtherUserId);
        var cursor = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            ActorUserId = Guid.NewGuid(),
            fixture.TenantId,
            WorkspaceId = (Guid?)null,
            Since = fixture.Clock.UtcNow.AddMinutes(-1)
        })));

        var result = await fixture.Service.GetUpdatesAsync(new CommunicationUpdatesPollingQuery(Cursor: cursor));

        Assert.False(result.IsSuccess);
        Assert.Equal("Invalid polling cursor.", result.Error);
        var auditJson = JsonSerializer.Serialize(fixture.Audit.Entries);
        Assert.Contains("cursor_scope_mismatch", auditJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(cursor, auditJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StudentRecordAndFileNotificationTargetsFailClosed()
    {
        var fixture = PollingFixture.Create();
        fixture.Notifications.Add(new NotificationListItemResponse(
            Guid.NewGuid(),
            fixture.UserId,
            NotificationType.System,
            "Restricted student record title",
            "restricted value",
            "StudentRecordRestricted",
            Guid.NewGuid(),
            false,
            fixture.Clock.UtcNow,
            null,
            null));
        fixture.Notifications.Add(new NotificationListItemResponse(
            Guid.NewGuid(),
            fixture.UserId,
            NotificationType.ArtifactUploaded,
            "File token title",
            "storage path",
            "FileObject",
            Guid.NewGuid(),
            false,
            fixture.Clock.UtcNow,
            null,
            null));

        var result = await fixture.Service.GetNotificationsAsync(new CommunicationPollingQuery());

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value!.Items.Count);
        Assert.All(result.Value.Items, item =>
        {
            Assert.Equal("Inaccessible", item.TargetType);
            Assert.Null(item.TargetId);
            Assert.Equal("Notification unavailable", item.Title);
        });

        var json = JsonSerializer.Serialize(result.Value);
        Assert.DoesNotContain("restricted value", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("storage path", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("File token", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnknownWorkspaceScopeDoesNotBecomeAnAuditForeignKey()
    {
        var fixture = PollingFixture.Create();
        var requestedWorkspaceId = Guid.NewGuid();
        fixture.Notifications.Add(new NotificationListItemResponse(
            Guid.NewGuid(),
            fixture.UserId,
            NotificationType.ArtifactUploaded,
            "File notification",
            "A file was uploaded.",
            "FileObject",
            Guid.NewGuid(),
            false,
            fixture.Clock.UtcNow,
            null,
            null));

        var result = await fixture.Service.GetNotificationsAsync(new CommunicationPollingQuery(
            WorkspaceId: requestedWorkspaceId));

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Value!.Items);
        Assert.Equal("Inaccessible", item.TargetType);
        var entry = Assert.Single(fixture.Audit.Entries);
        Assert.Null(entry.WorkspaceId);
        Assert.Contains(
            requestedWorkspaceId.ToString(),
            JsonSerializer.Serialize(entry.Metadata),
            StringComparison.OrdinalIgnoreCase);
    }

    private sealed class PollingFixture
    {
        private PollingFixture(SystemRole role)
        {
            Current.UserIdValue = UserId;
            Current.SystemRoleValue = role;
            CurrentTenant.TenantIdValue = TenantId;
            Authorization.Messaging = Messaging;
            Service = new CommunicationPollingService(
                Messaging,
                Notifications,
                Authorization,
                Projects,
                Current,
                CurrentTenant,
                Clock,
                Audit,
                UnitOfWork);
        }

        public Guid TenantId { get; } = Guid.NewGuid();
        private Guid WorkspaceId { get; } = Guid.NewGuid();
        public Guid UserId { get; } = Guid.NewGuid();
        public Guid OtherUserId { get; } = Guid.NewGuid();
        private FakeMessagingRepository Messaging { get; } = new();
        public FakeNotificationService Notifications { get; } = new();
        private FakeConversationAuthorization Authorization { get; } = new();
        private FakeProjectAuthorization Projects { get; } = new();
        private FakeCurrentUser Current { get; } = new();
        private FakeCurrentTenant CurrentTenant { get; } = new();
        public FakeClock Clock { get; } = new();
        public CapturingAuditLogger Audit { get; } = new();
        private FakeUnitOfWork UnitOfWork { get; } = new();
        public CommunicationPollingService Service { get; }
        public bool IncludeRemovedConversationsInList
        {
            set => Messaging.IncludeRemovedConversationsInList = value;
        }

        public List<ConversationMember> Members => Messaging.Members;

        public static PollingFixture Create(SystemRole role = SystemRole.User) => new(role);

        public Conversation AddDirectConversation(params Guid[] memberUserIds)
        {
            var conversation = new Conversation
            {
                TenantId = TenantId,
                WorkspaceId = WorkspaceId,
                Type = ConversationType.DirectMessage,
                CreatedByUserId = memberUserIds[0],
                CreatedAt = Clock.UtcNow.AddMinutes(-5)
            };
            Messaging.Conversations.Add(conversation);

            foreach (var memberUserId in memberUserIds)
            {
                Messaging.Members.Add(new ConversationMember
                {
                    TenantId = TenantId,
                    ConversationId = conversation.Id,
                    UserId = memberUserId,
                    JoinedAt = Clock.UtcNow.AddMinutes(-5)
                });
            }

            return conversation;
        }

        public Message AddMessage(Conversation conversation, Guid authorUserId, string body, DateTimeOffset createdAt)
        {
            var message = new Message
            {
                TenantId = TenantId,
                WorkspaceId = conversation.WorkspaceId,
                ConversationId = conversation.Id,
                AuthorUserId = authorUserId,
                Body = body,
                CreatedAt = createdAt
            };
            Messaging.Messages.Add(message);
            conversation.UpdatedAt = createdAt;
            return message;
        }
    }

    private sealed class FakeMessagingRepository : IMessagingRepository
    {
        public List<Conversation> Conversations { get; } = [];
        public List<ConversationMember> Members { get; } = [];
        public List<Message> Messages { get; } = [];
        public bool IncludeRemovedConversationsInList { get; set; }

        public Task<PagedResponse<Conversation>> ListForUserAsync(Guid userId, int page, int pageSize, CancellationToken cancellationToken = default)
        {
            var query = Conversations
                .Where(conversation => Members.Any(member =>
                    member.ConversationId == conversation.Id &&
                    member.UserId == userId &&
                    member.CanRead &&
                    (IncludeRemovedConversationsInList || member is { LeftAt: null, RemovedAt: null })))
                .OrderByDescending(conversation => conversation.UpdatedAt ?? conversation.CreatedAt)
                .ToList();

            var items = query.Skip((page - 1) * pageSize).Take(pageSize).ToList();
            return Task.FromResult(new PagedResponse<Conversation>(items, page, pageSize, query.Count));
        }

        public Task<ConversationInboxRepositoryResult> ListInboxForUserAsync(Guid userId, ConversationInboxView view, int page, int pageSize, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IQueryable<Guid>? QueryReadableConversationIds(Guid userId) => null;

        public Task<IReadOnlySet<Guid>> FilterReadableConversationIdsAsync(
            Guid userId,
            IReadOnlyCollection<Guid> conversationIds,
            CancellationToken cancellationToken = default)
        {
            var readable = new HashSet<Guid>();
            foreach (var conversationId in conversationIds.Distinct())
            {
                var currentId = conversationId;
                var visited = new HashSet<Guid>();
                for (var depth = 0; depth <= 32 && visited.Add(currentId); depth++)
                {
                    var conversation = Conversations.FirstOrDefault(item => item.Id == currentId);
                    var member = Members.FirstOrDefault(item =>
                        item.ConversationId == currentId &&
                        item.UserId == userId);
                    if (conversation is null ||
                        member is not { CanRead: true, LeftAt: null, RemovedAt: null })
                    {
                        break;
                    }

                    if (conversation.Type != ConversationType.Thread)
                    {
                        readable.Add(conversationId);
                        break;
                    }

                    if (!conversation.ParentConversationId.HasValue)
                    {
                        break;
                    }

                    currentId = conversation.ParentConversationId.Value;
                }
            }

            return Task.FromResult<IReadOnlySet<Guid>>(readable);
        }

        public Task<IReadOnlyList<User>> SearchDirectRecipientsAsync(Guid userId, string? query, int limit, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<User>>([]);
        }

        public Task<Conversation?> GetConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Conversations.FirstOrDefault(conversation => conversation.Id == conversationId));
        }

        public Task<Conversation?> FindDirectAsync(Guid workspaceId, Guid? projectId, Guid userAId, Guid userBId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<Conversation?>(null);
        }

        public Task<Conversation?> FindDirectForUsersAsync(Guid userAId, Guid userBId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<Conversation?>(null);
        }

        public Task<Workspace?> FindSharedActiveWorkspaceAsync(Guid userAId, Guid userBId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<Workspace?>(null);
        }

        public Task<ConversationMember?> GetMemberAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Members.FirstOrDefault(member => member.ConversationId == conversationId && member.UserId == userId));
        }

        public Task<IReadOnlyList<ConversationMember>> ListMembersAsync(Guid conversationId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ConversationMember>>(Members.Where(member => member.ConversationId == conversationId).ToList());
        }

        public Task<PagedResponse<Message>> ListMessagesAsync(Guid conversationId, int limit, DateTimeOffset? before, CancellationToken cancellationToken = default)
        {
            var query = Messages
                .Where(message => message.ConversationId == conversationId && message.ThreadRootMessageId == null && message.DeletedAt == null && (!before.HasValue || message.CreatedAt < before.Value))
                .OrderByDescending(message => message.CreatedAt)
                .ToList();
            return Task.FromResult(new PagedResponse<Message>(query.Take(limit).ToList(), 1, limit, query.Count));
        }

        public Task<PagedResponse<Message>> ListThreadRepliesAsync(Guid conversationId, Guid threadRootMessageId, int limit, DateTimeOffset? before, CancellationToken cancellationToken = default)
        {
            var query = Messages
                .Where(message => message.ConversationId == conversationId && message.ThreadRootMessageId == threadRootMessageId && (!before.HasValue || message.CreatedAt < before.Value))
                .OrderByDescending(message => message.CreatedAt)
                .ToList();
            return Task.FromResult(new PagedResponse<Message>(query.Take(limit).ToList(), 1, limit, query.Count));
        }

        public Task<IReadOnlyDictionary<Guid, MessageThreadSummaryResponse>> GetThreadSummariesAsync(Guid conversationId, IReadOnlyCollection<Guid> threadRootMessageIds, int participantLimit, CancellationToken cancellationToken = default)
        {
            var result = threadRootMessageIds.ToDictionary(
                rootId => rootId,
                rootId => BuildThreadSummary(conversationId, rootId, participantLimit));
            return Task.FromResult<IReadOnlyDictionary<Guid, MessageThreadSummaryResponse>>(result);
        }

        public Task<MessageThreadSummaryResponse> GetThreadSummaryAsync(Guid conversationId, Guid threadRootMessageId, int participantLimit, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(BuildThreadSummary(conversationId, threadRootMessageId, participantLimit));
        }

        private MessageThreadSummaryResponse BuildThreadSummary(Guid conversationId, Guid threadRootMessageId, int participantLimit)
        {
            var replies = Messages.Where(message =>
                message.ConversationId == conversationId &&
                message.ThreadRootMessageId == threadRootMessageId).ToList();
            return new MessageThreadSummaryResponse(
                threadRootMessageId,
                replies.Count,
                replies.OrderByDescending(message => message.CreatedAt).Select(message => (DateTimeOffset?)message.CreatedAt).FirstOrDefault(),
                []);
        }

        public Task<int> CountUnreadMessagesAsync(Guid conversationId, Guid userId, DateTimeOffset? lastReadAt, CancellationToken cancellationToken = default)
        {
            var count = Messages.Count(message =>
                message.ConversationId == conversationId &&
                message.AuthorUserId != userId &&
                message.DeletedAt == null &&
                (!lastReadAt.HasValue || message.CreatedAt > lastReadAt.Value));
            return Task.FromResult(count);
        }

        public Task<Message?> GetMessageAsync(Guid messageId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Messages.FirstOrDefault(message => message.Id == messageId));
        }

        public Task<Message?> FindMessageByClientRequestIdAsync(Guid conversationId, Guid authorUserId, Guid clientRequestId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Messages.FirstOrDefault(message =>
                message.ConversationId == conversationId &&
                message.AuthorUserId == authorUserId &&
                message.ClientRequestId == clientRequestId));
        }

        public Task<ReadState?> GetReadStateAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<ReadState?>(null);
        }

        public Task AddConversationAsync(Conversation conversation, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AddMemberAsync(ConversationMember member, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AddMessageAsync(Message message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AddReadStateAsync(ReadState readState, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AddAttachmentAsync(Attachment attachment, MessageAttachment link, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeNotificationService : INotificationService
    {
        private List<NotificationListItemResponse> Notifications { get; } = [];

        public void Add(NotificationListItemResponse notification) => Notifications.Add(notification);

        public Task<PagedResponse<NotificationListItemResponse>> ListAsync(Guid userId, int page, int pageSize, CancellationToken cancellationToken = default)
        {
            var query = Notifications
                .Where(notification => notification.UserId == userId)
                .OrderByDescending(notification => notification.CreatedAt)
                .ToList();
            var items = query.Skip((page - 1) * pageSize).Take(pageSize).ToList();
            return Task.FromResult(new PagedResponse<NotificationListItemResponse>(items, page, pageSize, query.Count));
        }

        public Task NotifyAsync(Guid recipientUserId, string title, string? body, string sourceType, Guid sourceId, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeConversationAuthorization : IConversationAuthorizationService
    {
        public FakeMessagingRepository Messaging { get; set; } = null!;

        public Task<bool> CanViewConversation(Guid userId, Guid conversationId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Messaging.Members.Any(member =>
                member.ConversationId == conversationId &&
                member.UserId == userId &&
                member is { LeftAt: null, RemovedAt: null, CanRead: true }));
        }

        public Task<bool> CanSendMessage(Guid userId, Guid conversationId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> CanManageConversation(Guid userId, Guid conversationId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> CanModerateConversation(Guid userId, Guid conversationId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> CanCreateThread(Guid userId, Guid parentConversationId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> CanEditMessage(Guid userId, Guid messageId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> CanDeleteMessage(Guid userId, Guid messageId, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class FakeProjectAuthorization : IProjectAuthorizationService
    {
        public Task<bool> CanViewProject(Guid userId, Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> CanManageProject(Guid userId, Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> CanCreateProject(Guid userId, Guid workspaceId, Guid groupId, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid? UserIdValue { get; set; }
        public SystemRole? SystemRoleValue { get; set; }
        public Guid? UserId => UserIdValue;
        public Guid? SessionId => null;
        public string? Email => null;
        public SystemRole? SystemRole => SystemRoleValue;
        public bool IsAuthenticated => UserIdValue.HasValue;
    }

    private sealed class FakeCurrentTenant : ICurrentTenant
    {
        public Guid TenantIdValue { get; set; }
        public Guid TenantId => TenantIdValue;
        public bool IsAvailable => TenantIdValue != Guid.Empty;
        public string TenantSlug => "tenant-test";
        public bool IsPlatformScope => false;
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class CapturingAuditLogger : IAuditLogger
    {
        public List<AuditLogEntry> Entries { get; } = [];

        public Task LogAsync(AuditLogEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeUnitOfWork : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(1);
        }
    }
}
