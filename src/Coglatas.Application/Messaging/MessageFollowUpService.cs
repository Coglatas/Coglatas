using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;

namespace Coglatas.Application.Messaging;

public sealed class MessageFollowUpService(
    IMessageFollowUpRepository followUps,
    IMessagingRepository messaging,
    IConversationAuthorizationService authorization,
    ICurrentUser currentUser,
    ICurrentTenant currentTenant,
    IClock clock,
    IAuditLogger auditLogger,
    IMessageFollowUpCommitCoordinator commitCoordinator,
    IUnitOfWork unitOfWork) : IMessageFollowUpService
{
    public async Task<Result<PagedResponse<MessageFollowUpListItemResponse>>> ListAsync(
        MessageFollowUpListQuery query,
        CancellationToken cancellationToken = default)
    {
        if (!TryCurrentScope(out var userId))
        {
            return Result<PagedResponse<MessageFollowUpListItemResponse>>.Failure("Authentication is required.");
        }

        // The repository composes the saved rows with the canonical current
        // Conversation readability relation before count/paging. Revoked rows
        // remain private durable state but cannot contribute metadata or counts.
        var page = await followUps.ListVisibleAsync(
            userId,
            query.SafePage,
            query.SafePageSize,
            cancellationToken);
        var items = page.Items
            .Where(item => item.Message is { DeletedAt: null, Conversation: not null })
            .Select(ToListItem)
            .ToList();
        return Result<PagedResponse<MessageFollowUpListItemResponse>>.Success(
            new PagedResponse<MessageFollowUpListItemResponse>(
                items,
                page.Page,
                page.PageSize,
                page.TotalCount));
    }

    public async Task<Result<MessageFollowUpStateResponse>> SaveAsync(
        Guid messageId,
        CancellationToken cancellationToken = default)
    {
        if (!TryCurrentScope(out var userId))
        {
            return Result<MessageFollowUpStateResponse>.Failure("Message not found.");
        }

        var message = await GetReadableMessageOrAuditDenialAsync(
            userId,
            messageId,
            "MessageFollowUpSaveDenied",
            cancellationToken);
        if (message is null)
        {
            return Result<MessageFollowUpStateResponse>.Failure("Message not found.");
        }

        var existing = await followUps.GetAsync(userId, messageId, cancellationToken);
        if (existing is not null)
        {
            return Result<MessageFollowUpStateResponse>.Success(
                new MessageFollowUpStateResponse(messageId, true, existing.CreatedAt));
        }

        var followUp = new MessageFollowUp
        {
            TenantId = currentTenant.TenantId,
            UserId = userId,
            MessageId = messageId,
            CreatedAt = clock.UtcNow
        };
        await followUps.AddAsync(followUp, cancellationToken);
        await AuditAsync(userId, messageId, "MessageFollowUpSaved", "allow", cancellationToken, message.ConversationId);
        var committed = await commitCoordinator.SaveAsync(followUp, cancellationToken);
        return Result<MessageFollowUpStateResponse>.Success(
            new MessageFollowUpStateResponse(messageId, true, committed.FollowUp.CreatedAt));
    }

    public async Task<Result<MessageFollowUpStateResponse>> RemoveAsync(
        Guid messageId,
        CancellationToken cancellationToken = default)
    {
        if (!TryCurrentScope(out var userId))
        {
            return Result<MessageFollowUpStateResponse>.Failure("Message not found.");
        }

        // Reauthorize the Message before looking up the caller's saved row so
        // deletion cannot reveal whether an inaccessible identity was saved.
        var message = await GetReadableMessageOrAuditDenialAsync(
            userId,
            messageId,
            "MessageFollowUpRemoveDenied",
            cancellationToken);
        if (message is null)
        {
            return Result<MessageFollowUpStateResponse>.Failure("Message not found.");
        }

        var existing = await followUps.GetAsync(userId, messageId, cancellationToken);
        if (existing is not null)
        {
            followUps.Remove(existing);
            await AuditAsync(userId, messageId, "MessageFollowUpRemoved", "allow", cancellationToken, message.ConversationId);
            await commitCoordinator.RemoveAsync(existing, cancellationToken);
        }

        return Result<MessageFollowUpStateResponse>.Success(
            new MessageFollowUpStateResponse(messageId, false, null));
    }

    private async Task<Message?> GetReadableMessageOrAuditDenialAsync(
        Guid userId,
        Guid messageId,
        string deniedAction,
        CancellationToken cancellationToken)
    {
        var message = await GetReadableMessageAsync(userId, messageId, cancellationToken);
        if (message is not null)
        {
            return message;
        }

        await AuditAsync(userId, messageId, deniedAction, "deny", cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return null;
    }

    private async Task<Message?> GetReadableMessageAsync(
        Guid userId,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        if (messageId == Guid.Empty)
        {
            return null;
        }
        var message = await messaging.GetMessageAsync(messageId, cancellationToken);
        return message is not null &&
               !message.DeletedAt.HasValue &&
               await authorization.CanViewConversation(userId, message.ConversationId, cancellationToken)
            ? message
            : null;
    }

    private bool TryCurrentScope(out Guid userId)
    {
        userId = currentUser.UserId ?? Guid.Empty;
        return currentUser.IsAuthenticated &&
               userId != Guid.Empty &&
               currentTenant is { IsAvailable: true, IsPlatformScope: false };
    }

    private Task AuditAsync(
        Guid userId,
        Guid messageId,
        string action,
        string decision,
        CancellationToken cancellationToken,
        Guid? conversationId = null) =>
        auditLogger.LogAsync(new AuditLogEntry(
            userId,
            action,
            "Message",
            messageId,
            "Participant-private message follow-up state changed.",
            Metadata: new Dictionary<string, object?>
            {
                ["messageId"] = messageId,
                ["conversationId"] = conversationId,
                ["decision"] = decision,
                ["stateKind"] = "savedMessage"
            }), cancellationToken);

    private static MessageFollowUpListItemResponse ToListItem(MessageFollowUp item)
    {
        var message = item.Message!;
        var conversation = message.Conversation!;
        return new MessageFollowUpListItemResponse(
            message.Id,
            message.ConversationId,
            conversation.WorkspaceId,
            conversation.Type,
            string.IsNullOrWhiteSpace(conversation.Title)
                ? conversation.Type == ConversationType.DirectMessage
                    ? "Direct message"
                    : "Conversation"
                : conversation.Title,
            message.ThreadRootMessageId,
            message.AuthorUser?.DisplayName ?? "Conversation member",
            message.Body,
            message.CreatedAt,
            item.CreatedAt);
    }
}
