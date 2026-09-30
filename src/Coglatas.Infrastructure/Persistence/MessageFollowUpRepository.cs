using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Infrastructure.Persistence;

public sealed class MessageFollowUpRepository(
    AppDbContext dbContext,
    IMessagingRepository messaging) : IMessageFollowUpRepository
{
    private const int AuthorizationBatchSize = 200;

    public async Task<PagedResponse<MessageFollowUp>> ListVisibleAsync(
        Guid userId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var providerReadableConversationIds = messaging.QueryReadableConversationIds(userId);
        IReadOnlySet<Guid>? fallbackReadableConversationIds = null;
        if (providerReadableConversationIds is null)
        {
            var candidateIds = await dbContext.MessageFollowUps
                .AsNoTracking()
                .Where(item => item.UserId == userId && item.Message != null && item.Message.DeletedAt == null)
                .Select(item => item.Message!.ConversationId)
                .Distinct()
                .ToListAsync(cancellationToken);
            var readable = new HashSet<Guid>();
            foreach (var batch in candidateIds.Chunk(AuthorizationBatchSize))
            {
                readable.UnionWith(await messaging.FilterReadableConversationIdsAsync(userId, batch, cancellationToken));
            }
            fallbackReadableConversationIds = readable;
        }

        var query = dbContext.MessageFollowUps
            .AsNoTracking()
            .Include(item => item.Message)
                .ThenInclude(message => message!.Conversation)
            .Where(item =>
                item.UserId == userId &&
                item.Message != null &&
                item.Message.DeletedAt == null);
        query = providerReadableConversationIds is not null
            ? query.Where(item => providerReadableConversationIds.Contains(item.Message!.ConversationId))
            : query.Where(item => fallbackReadableConversationIds!.Contains(item.Message!.ConversationId));

        var total = await query.CountAsync(cancellationToken);
        var skip = (int)Math.Min((page - 1L) * pageSize, int.MaxValue);
        var items = await query
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .Skip(skip)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        await messaging.HydrateAuthorizedMessageAuthorsAsync(
            items.Where(item => item.Message is not null).Select(item => item.Message!).ToArray(),
            cancellationToken);
        return new PagedResponse<MessageFollowUp>(items, page, pageSize, total);
    }

    public Task<MessageFollowUp?> GetAsync(
        Guid userId,
        Guid messageId,
        CancellationToken cancellationToken = default) =>
        dbContext.MessageFollowUps.FirstOrDefaultAsync(
            item => item.UserId == userId && item.MessageId == messageId,
            cancellationToken);

    public Task AddAsync(MessageFollowUp followUp, CancellationToken cancellationToken = default) =>
        dbContext.MessageFollowUps.AddAsync(followUp, cancellationToken).AsTask();

    public void Remove(MessageFollowUp followUp) => dbContext.MessageFollowUps.Remove(followUp);
}
