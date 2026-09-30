using Coglatas.Application.Messaging;
using Coglatas.Domain.Entities;

namespace Coglatas.Application.Common.Interfaces;

public interface IMessagingRepository
{
    Task<PagedResponse<Conversation>> ListForUserAsync(Guid userId, int page, int pageSize, CancellationToken cancellationToken = default);
    Task<ConversationInboxRepositoryResult> ListInboxForUserAsync(Guid userId, ConversationInboxView view, int page, int pageSize, CancellationToken cancellationToken = default);
    /// <summary>
    /// Returns the provider-composable authoritative Conversation readability
    /// relation, or <see langword="null"/> when the provider requires the
    /// bounded asynchronous fallback.
    /// </summary>
    IQueryable<Guid>? QueryReadableConversationIds(Guid userId);
    Task<IReadOnlySet<Guid>> FilterReadableConversationIdsAsync(Guid userId, IReadOnlyCollection<Guid> conversationIds, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<User>> SearchDirectRecipientsAsync(Guid userId, string? query, int limit, CancellationToken cancellationToken = default);
    Task<Conversation?> GetConversationAsync(Guid conversationId, CancellationToken cancellationToken = default);
    Task<Conversation?> FindDirectAsync(Guid workspaceId, Guid? projectId, Guid userAId, Guid userBId, CancellationToken cancellationToken = default);
    Task<Conversation?> FindDirectForUsersAsync(Guid userAId, Guid userBId, CancellationToken cancellationToken = default);
    Task<Workspace?> FindSharedActiveWorkspaceAsync(Guid userAId, Guid userBId, CancellationToken cancellationToken = default);
    Task<ConversationMember?> GetMemberAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ConversationMember>> ListMembersAsync(Guid conversationId, CancellationToken cancellationToken = default);
    Task<PagedResponse<Message>> ListMessagesAsync(Guid conversationId, int limit, DateTimeOffset? before, CancellationToken cancellationToken = default);
    Task<PagedResponse<Message>> ListMessageContextAsync(Guid conversationId, Guid anchorMessageId, int limit, CancellationToken cancellationToken = default) =>
        ListMessagesAsync(conversationId, limit, before: null, cancellationToken);
    Task HydrateAuthorizedMessageAuthorsAsync(IReadOnlyCollection<Message> messages, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
    Task<PagedResponse<Message>> ListThreadRepliesAsync(Guid conversationId, Guid threadRootMessageId, int limit, DateTimeOffset? before, CancellationToken cancellationToken = default);
    async Task<PagedResponse<Message>> ListThreadReplyContextAsync(
        Guid conversationId,
        Guid threadRootMessageId,
        Guid anchorReplyMessageId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var page = await ListThreadRepliesAsync(
            conversationId,
            threadRootMessageId,
            limit,
            before: null,
            cancellationToken);
        if (page.Items.Any(message => message.Id == anchorReplyMessageId))
        {
            return page;
        }

        var anchor = await GetMessageAsync(anchorReplyMessageId, cancellationToken);
        if (anchor is null ||
            anchor.DeletedAt.HasValue ||
            anchor.ConversationId != conversationId ||
            anchor.ThreadRootMessageId != threadRootMessageId)
        {
            return new PagedResponse<Message>([], 1, limit, 0);
        }

        var items = page.Items
            .Take(Math.Max(0, limit - 1))
            .Append(anchor)
            .OrderBy(message => message.CreatedAt)
            .ThenBy(message => message.Id)
            .ToArray();
        await HydrateAuthorizedMessageAuthorsAsync(items, cancellationToken);
        return new PagedResponse<Message>(items, 1, limit, page.TotalCount);
    }
    Task<IReadOnlyDictionary<Guid, MessageThreadSummaryResponse>> GetThreadSummariesAsync(Guid conversationId, IReadOnlyCollection<Guid> threadRootMessageIds, int participantLimit, CancellationToken cancellationToken = default);
    Task<MessageThreadSummaryResponse> GetThreadSummaryAsync(Guid conversationId, Guid threadRootMessageId, int participantLimit, CancellationToken cancellationToken = default);
    Task<int> CountUnreadMessagesAsync(Guid conversationId, Guid userId, DateTimeOffset? lastReadAt, CancellationToken cancellationToken = default);
    Task<Message?> GetMessageAsync(Guid messageId, CancellationToken cancellationToken = default);
    Task<Message?> FindMessageByClientRequestIdAsync(Guid conversationId, Guid authorUserId, Guid clientRequestId, CancellationToken cancellationToken = default);
    Task<ReadState?> GetReadStateAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken = default);
    Task AddConversationAsync(Conversation conversation, CancellationToken cancellationToken = default);
    Task AddMemberAsync(ConversationMember member, CancellationToken cancellationToken = default);
    Task AddMessageAsync(Message message, CancellationToken cancellationToken = default);
    Task AddReadStateAsync(ReadState readState, CancellationToken cancellationToken = default);
    Task AddAttachmentAsync(Attachment attachment, MessageAttachment link, CancellationToken cancellationToken = default);
}

public sealed record ConversationInboxRepositoryResult(
    PagedResponse<Conversation> Page,
    ConversationInboxCountsResponse Counts);
