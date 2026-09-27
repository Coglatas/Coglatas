using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Search;
using Coglatas.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Infrastructure.Persistence;

public sealed class DbSearchService(
    AppDbContext dbContext,
    ICurrentUser currentUser,
    IMessagingRepository messaging) : ISearchService
{
    private const int MaxPageSize = 50;

    public async Task<Result<SearchResponse>> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        if (!currentUser.IsAuthenticated || !currentUser.UserId.HasValue)
        {
            return Result<SearchResponse>.Failure("Authentication is required.");
        }

        if (!Enum.IsDefined(request.Type))
        {
            return Result<SearchResponse>.Failure("Search type is invalid.");
        }

        if (!Enum.IsDefined(request.FileKind))
        {
            return Result<SearchResponse>.Failure("File kind is invalid.");
        }

        if (!Enum.IsDefined(request.MessageRead))
        {
            return Result<SearchResponse>.Failure("Message read filter is invalid.");
        }

        if (!Enum.IsDefined(request.MessageAttachment))
        {
            return Result<SearchResponse>.Failure("Message attachment filter is invalid.");
        }

        if (request.FileKind != FileSearchKind.All && request.Type != SearchResultType.File)
        {
            return Result<SearchResponse>.Failure("File kind is only valid for File search.");
        }

        if ((request.MessageRead != MessageReadFilter.All ||
             request.MessageAttachment != MessageAttachmentFilter.All ||
             request.ToDateExclusive.HasValue) &&
            request.Type != SearchResultType.Message)
        {
            return Result<SearchResponse>.Failure("Message filters are only valid for Message search.");
        }

        if (request.AuthorUserId == Guid.Empty)
        {
            return Result<SearchResponse>.Failure("Author is invalid.");
        }

        if (request is { FromDate: { } fromDate, ToDate: { } toDate } && fromDate > toDate)
        {
            return Result<SearchResponse>.Failure("Date range is invalid.");
        }

        if (request is { ToDate: not null, ToDateExclusive: not null })
        {
            return Result<SearchResponse>.Failure("Date range is invalid.");
        }

        if (request is { FromDate: { } rangeStart, ToDateExclusive: { } rangeEnd } &&
            rangeStart >= rangeEnd)
        {
            return Result<SearchResponse>.Failure("Date range is invalid.");
        }

        var hasFilters = request.WorkspaceId.HasValue ||
            request.GroupId.HasValue ||
            request.ProjectId.HasValue ||
            request.AuthorUserId.HasValue ||
            request.FromDate.HasValue ||
            request.ToDate.HasValue ||
            request.ToDateExclusive.HasValue ||
            request.MessageRead != MessageReadFilter.All ||
            request.MessageAttachment != MessageAttachmentFilter.All;
        if (string.IsNullOrWhiteSpace(request.Q) && !hasFilters)
        {
            return Result<SearchResponse>.Failure("Search query or filters are required.");
        }

        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, MaxPageSize);
        var userId = currentUser.UserId.Value;
        var isSystemAdmin = currentUser.SystemRole == SystemRole.SystemAdmin;
        var q = request.Q?.Trim();

        var items = new List<SearchResultItemResponse>();

        if (ShouldInclude(request.Type, SearchResultType.User))
        {
            items.AddRange(await SearchUsersAsync(userId, isSystemAdmin, q, request, cancellationToken));
        }

        if (ShouldInclude(request.Type, SearchResultType.Workspace))
        {
            items.AddRange(await SearchWorkspacesAsync(userId, isSystemAdmin, q, request, cancellationToken));
        }

        if (ShouldInclude(request.Type, SearchResultType.Group))
        {
            items.AddRange(await SearchGroupsAsync(userId, isSystemAdmin, q, request, cancellationToken));
        }

        if (ShouldInclude(request.Type, SearchResultType.Channel))
        {
            items.AddRange(await SearchChannelsAsync(userId, isSystemAdmin, q, request, cancellationToken));
        }

        if (ShouldInclude(request.Type, SearchResultType.Post))
        {
            items.AddRange(await SearchPostsAsync(userId, isSystemAdmin, q, request, cancellationToken));
        }

        if (ShouldInclude(request.Type, SearchResultType.Message))
        {
            items.AddRange(await SearchMessagesAsync(userId, q, request, cancellationToken));
        }

        if (ShouldInclude(request.Type, SearchResultType.Announcement))
        {
            items.AddRange(await SearchAnnouncementsAsync(userId, isSystemAdmin, q, request, cancellationToken));
        }

        if (ShouldInclude(request.Type, SearchResultType.Project))
        {
            items.AddRange(await SearchProjectsAsync(userId, q, request, cancellationToken));
        }

        if (ShouldInclude(request.Type, SearchResultType.File))
        {
            items.AddRange(await SearchFilesAsync(userId, isSystemAdmin, q, request, cancellationToken));
        }

        if (ShouldInclude(request.Type, SearchResultType.Task))
        {
            items.AddRange(await SearchTasksAsync(userId, q, request, cancellationToken));
        }

        if (ShouldInclude(request.Type, SearchResultType.Artifact))
        {
            items.AddRange(await SearchArtifactsAsync(userId, q, request, cancellationToken));
        }

        if (ShouldInclude(request.Type, SearchResultType.ActivityLog))
        {
            items.AddRange(await SearchActivityLogsAsync(userId, q, request, cancellationToken));
        }

        if (ShouldInclude(request.Type, SearchResultType.Comment))
        {
            items.AddRange(await SearchCommentsAsync(userId, q, request, cancellationToken));
        }

        var ordered = items
            .OrderByDescending(item => item.CreatedAt)
            .ThenBy(item => item.Type)
            .ToList();
        var pageItems = ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return Result<SearchResponse>.Success(new SearchResponse(q, page, pageSize, ordered.Count, pageItems));
    }

    public async Task<Result<MessageAuthorOptionsResponse>> SearchMessageAuthorsAsync(
        MessageAuthorOptionsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!currentUser.IsAuthenticated || !currentUser.UserId.HasValue)
        {
            return Result<MessageAuthorOptionsResponse>.Failure("Authentication is required.");
        }

        if (request.SelectedUserId == Guid.Empty)
        {
            return Result<MessageAuthorOptionsResponse>.Failure("Selected author is invalid.");
        }

        var q = request.Q?.Trim();
        if (!request.SelectedUserId.HasValue &&
            (string.IsNullOrWhiteSpace(q) || q.Length is < 2 or > 120))
        {
            return Result<MessageAuthorOptionsResponse>.Failure("Author query must contain between 2 and 120 characters.");
        }

        var userId = currentUser.UserId.Value;
        var limit = Math.Clamp(request.Limit, 1, 20);
        var messages = dbContext.Messages
            .AsNoTracking()
            .Where(message =>
                message.DeletedAt == null &&
                dbContext.Conversations.Any(conversation =>
                    conversation.Id == message.ConversationId &&
                    conversation.TenantId == message.TenantId &&
                    conversation.WorkspaceId == message.WorkspaceId));

        var authorizedConversationIds = await ResolveReadableConversationIdsAsync(
            userId,
            async token => await messages
                .GroupBy(message => message.ConversationId)
                .Select(group => new
                {
                    ConversationId = group.Key,
                    LatestMessageAt = group.Max(message => message.CreatedAt)
                })
                .OrderByDescending(item => item.LatestMessageAt)
                .ThenBy(item => item.ConversationId)
                .Take(100)
                .Select(item => item.ConversationId)
                .ToListAsync(token),
            cancellationToken);
        if (authorizedConversationIds.Count == 0)
        {
            return Result<MessageAuthorOptionsResponse>.Success(new MessageAuthorOptionsResponse([]));
        }

        messages = messages.Where(message => authorizedConversationIds.Contains(message.ConversationId));

        var tenantAuthors = dbContext.TenantUsers
            .AsNoTracking()
            .Join(
                dbContext.Users.AsNoTracking(),
                tenantUser => tenantUser.UserId,
                author => author.Id,
                (tenantUser, author) => new
                {
                    tenantUser.TenantId,
                    UserId = author.Id,
                    author.DisplayName
                });
        var conversationAuthors = dbContext.ConversationMembers
            .AsNoTracking()
            .Join(
                tenantAuthors,
                member => new { member.TenantId, member.UserId },
                author => new { author.TenantId, author.UserId },
                (member, author) => new
                {
                    member.TenantId,
                    member.ConversationId,
                    author.UserId,
                    author.DisplayName
                });
        var candidates = messages.Join(
            conversationAuthors,
            message => new
            {
                message.TenantId,
                message.ConversationId,
                UserId = message.AuthorUserId
            },
            author => new
            {
                author.TenantId,
                author.ConversationId,
                author.UserId
            },
            (message, author) => new { message, author });

        if (request.SelectedUserId.HasValue)
        {
            candidates = candidates.Where(item => item.author.UserId == request.SelectedUserId.Value);
        }
        else
        {
            var normalizedQuery = q!.ToLower();
            candidates = candidates.Where(item => item.author.DisplayName.ToLower().Contains(normalizedQuery));
        }

        var items = await candidates
            .Select(item => new
            {
                item.author.UserId,
                item.author.DisplayName
            })
            .Distinct()
            .OrderBy(author => author.DisplayName)
            .ThenBy(author => author.UserId)
            .Take(limit)
            .Select(author => new MessageAuthorOptionResponse(author.UserId, author.DisplayName))
            .ToListAsync(cancellationToken);

        return Result<MessageAuthorOptionsResponse>.Success(new MessageAuthorOptionsResponse(items));
    }

    private async Task<IReadOnlyList<SearchResultItemResponse>> SearchUsersAsync(Guid userId, bool isSystemAdmin, string? q, SearchRequest request, CancellationToken cancellationToken)
    {
        var query = dbContext.Users.AsNoTracking().Where(user => user.DeletedAt == null);
        if (!isSystemAdmin)
        {
            query = query.Where(user => dbContext.WorkspaceMembers.Any(member =>
                member.UserId == user.Id &&
                member.Status == MembershipStatus.Active &&
                dbContext.WorkspaceMembers.Any(current => current.WorkspaceId == member.WorkspaceId && current.UserId == userId && current.Status == MembershipStatus.Active)));
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            query = query.Where(user => EF.Functions.ILike(user.DisplayName, $"%{q}%") || EF.Functions.ILike(user.Email, $"%{q}%"));
        }

        query = ApplyDateFilters(query, request, user => user.CreatedAt);
        return await query
            .Select(user => new SearchResultItemResponse(SearchResultType.User, user.Id, user.DisplayName, user.Email, $"/users/{user.Id}", null, null, null, user.CreatedAt, user.DisplayName))
            .Take(100)
            .ToListAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<SearchResultItemResponse>> SearchWorkspacesAsync(Guid userId, bool isSystemAdmin, string? q, SearchRequest request, CancellationToken cancellationToken)
    {
        var query = dbContext.Workspaces.AsNoTracking().Where(workspace => workspace.DeletedAt == null);
        if (!isSystemAdmin)
        {
            query = query.Where(workspace => workspace.Members.Any(member => member.UserId == userId && member.Status == MembershipStatus.Active));
        }

        if (request.WorkspaceId.HasValue)
        {
            query = query.Where(workspace => workspace.Id == request.WorkspaceId.Value);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            query = query.Where(workspace => EF.Functions.ILike(workspace.Name, $"%{q}%") || (workspace.Description != null && EF.Functions.ILike(workspace.Description, $"%{q}%")));
        }

        query = ApplyDateFilters(query, request, workspace => workspace.CreatedAt);
        return await query
            .Select(workspace => new SearchResultItemResponse(SearchResultType.Workspace, workspace.Id, workspace.Name, workspace.Description, $"/workspaces/{workspace.Id}", workspace.Id, null, null, workspace.CreatedAt, null))
            .Take(100)
            .ToListAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<SearchResultItemResponse>> SearchGroupsAsync(Guid userId, bool isSystemAdmin, string? q, SearchRequest request, CancellationToken cancellationToken)
    {
        var query = dbContext.Groups.AsNoTracking().Where(group => group.DeletedAt == null);
        if (!isSystemAdmin)
        {
            query = query.Where(group => dbContext.WorkspaceMembers.Any(member => member.WorkspaceId == group.WorkspaceId && member.UserId == userId && member.Status == MembershipStatus.Active));
        }

        query = ApplyScopeFilters(query, request, group => group.WorkspaceId, group => group.Id, null);
        if (!string.IsNullOrWhiteSpace(q))
        {
            query = query.Where(group => EF.Functions.ILike(group.Name, $"%{q}%") || (group.Description != null && EF.Functions.ILike(group.Description, $"%{q}%")));
        }

        query = ApplyDateFilters(query, request, group => group.CreatedAt);
        return await query
            .Select(group => new SearchResultItemResponse(SearchResultType.Group, group.Id, group.Name, group.Description, $"/groups/{group.Id}", group.WorkspaceId, group.Id, null, group.CreatedAt, null))
            .Take(100)
            .ToListAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<SearchResultItemResponse>> SearchChannelsAsync(Guid userId, bool isSystemAdmin, string? q, SearchRequest request, CancellationToken cancellationToken)
    {
        var query = VisibleChannels(userId, isSystemAdmin);
        query = ApplyScopeFilters(query, request, channel => channel.WorkspaceId, channel => channel.GroupId, null);
        if (request.GroupId.HasValue)
        {
            query = query.Where(channel => channel.GroupId == request.GroupId.Value);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            query = query.Where(channel => EF.Functions.ILike(channel.Name, $"%{q}%") || (channel.Description != null && EF.Functions.ILike(channel.Description, $"%{q}%")));
        }

        query = ApplyDateFilters(query, request, channel => channel.CreatedAt);
        return await query
            .Select(channel => new SearchResultItemResponse(SearchResultType.Channel, channel.Id, channel.Name, channel.Description, $"/channels/{channel.Id}", channel.WorkspaceId, channel.GroupId, null, channel.CreatedAt, null))
            .Take(100)
            .ToListAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<SearchResultItemResponse>> SearchPostsAsync(Guid userId, bool isSystemAdmin, string? q, SearchRequest request, CancellationToken cancellationToken)
    {
        var visibleChannelIds = VisibleChannels(userId, isSystemAdmin).Select(channel => channel.Id);
        var query = dbContext.Posts.AsNoTracking()
            .Where(post => post.DeletedAt == null && visibleChannelIds.Contains(post.ChannelId))
            .Join(dbContext.Channels, post => post.ChannelId, channel => channel.Id, (post, channel) => new { post, channel });

        if (request.WorkspaceId.HasValue)
        {
            query = query.Where(item => item.channel.WorkspaceId == request.WorkspaceId);
        }

        if (request.GroupId.HasValue)
        {
            query = query.Where(item => item.channel.GroupId == request.GroupId);
        }

        if (request.AuthorUserId.HasValue)
        {
            query = query.Where(item => item.post.AuthorUserId == request.AuthorUserId);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            query = query.Where(item => EF.Functions.ILike(item.post.Body, $"%{q}%"));
        }

        query = query.Where(item =>
            (!request.FromDate.HasValue || item.post.CreatedAt >= request.FromDate.Value) &&
            (!request.ToDate.HasValue || item.post.CreatedAt <= request.ToDate.Value));

        return await query
            .Select(item => new SearchResultItemResponse(SearchResultType.Post, item.post.Id, "Post", Snippet(item.post.Body), $"/posts/{item.post.Id}", item.channel.WorkspaceId, item.channel.GroupId, null, item.post.CreatedAt, item.post.AuthorUser!.DisplayName))
            .Take(100)
            .ToListAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<SearchResultItemResponse>> SearchMessagesAsync(Guid userId, string? q, SearchRequest request, CancellationToken cancellationToken)
    {
        var query = dbContext.Messages.AsNoTracking()
            .Where(message => message.DeletedAt == null)
            .Join(dbContext.Conversations, message => message.ConversationId, conversation => conversation.Id, (message, conversation) => new { message, conversation })
            .Where(item =>
                item.message.TenantId == item.conversation.TenantId &&
                item.message.WorkspaceId == item.conversation.WorkspaceId);

        if (request.WorkspaceId.HasValue)
        {
            query = query.Where(item => item.conversation.WorkspaceId == request.WorkspaceId);
        }

        if (request.AuthorUserId.HasValue)
        {
            // Validate the opaque author identity once against the current
            // Tenant. Per-row author attribution is intentionally deferred
            // until after the readable Message result is bounded.
            var isTenantAuthor = await dbContext.TenantUsers
                .AsNoTracking()
                .AnyAsync(
                    tenantUser => tenantUser.UserId == request.AuthorUserId.Value,
                    cancellationToken);
            if (!isTenantAuthor)
            {
                return [];
            }

            var historicalAuthorConversationIds = dbContext.ConversationMembers
                .AsNoTracking()
                .Where(member => member.UserId == request.AuthorUserId.Value)
                .Select(member => member.ConversationId);
            query = query.Where(item =>
                item.message.AuthorUserId == request.AuthorUserId &&
                historicalAuthorConversationIds.Contains(item.conversation.Id));
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            query = query.Where(item =>
                EF.Functions.ILike(item.message.Body, $"%{q}%") ||
                (item.conversation.Title != null && EF.Functions.ILike(item.conversation.Title, $"%{q}%")));
        }

        query = query.Where(item =>
            (!request.FromDate.HasValue || item.message.CreatedAt >= request.FromDate.Value) &&
            (!request.ToDate.HasValue || item.message.CreatedAt <= request.ToDate.Value) &&
            (!request.ToDateExclusive.HasValue || item.message.CreatedAt < request.ToDateExclusive.Value));

        // Prefer the persisted cursor Message over LastReadAt, which is the
        // action time and can be later than Messages the actor has not read.
        // Legacy states without a cursor retain the established timestamp
        // fallback. A non-null but invalid/mismatched cursor fails closed.
        if (request.MessageRead == MessageReadFilter.Unread)
        {
            query = query.Where(item =>
                item.message.AuthorUserId != userId &&
                !dbContext.ReadStates.Any(readState =>
                    readState.TenantId == item.message.TenantId &&
                    readState.ConversationId == item.message.ConversationId &&
                    readState.ScopeType == ReadScopeType.Conversation &&
                    readState.ScopeId == item.message.ConversationId &&
                    readState.UserId == userId &&
                    ((readState.LastReadMessageId.HasValue &&
                      dbContext.Messages.Any(cursor =>
                          cursor.Id == readState.LastReadMessageId.Value &&
                          cursor.TenantId == item.message.TenantId &&
                          cursor.WorkspaceId == item.message.WorkspaceId &&
                          cursor.ConversationId == item.message.ConversationId &&
                          (cursor.CreatedAt > item.message.CreatedAt ||
                           (cursor.CreatedAt == item.message.CreatedAt && cursor.Id.CompareTo(item.message.Id) >= 0)))) ||
                     (!readState.LastReadMessageId.HasValue && readState.LastReadAt >= item.message.CreatedAt))));
        }
        else if (request.MessageRead == MessageReadFilter.Read)
        {
            query = query.Where(item =>
                item.message.AuthorUserId == userId ||
                dbContext.ReadStates.Any(readState =>
                    readState.TenantId == item.message.TenantId &&
                    readState.ConversationId == item.message.ConversationId &&
                    readState.ScopeType == ReadScopeType.Conversation &&
                    readState.ScopeId == item.message.ConversationId &&
                    readState.UserId == userId &&
                    ((readState.LastReadMessageId.HasValue &&
                      dbContext.Messages.Any(cursor =>
                          cursor.Id == readState.LastReadMessageId.Value &&
                          cursor.TenantId == item.message.TenantId &&
                          cursor.WorkspaceId == item.message.WorkspaceId &&
                          cursor.ConversationId == item.message.ConversationId &&
                          (cursor.CreatedAt > item.message.CreatedAt ||
                           (cursor.CreatedAt == item.message.CreatedAt && cursor.Id.CompareTo(item.message.Id) >= 0)))) ||
                     (!readState.LastReadMessageId.HasValue && readState.LastReadAt >= item.message.CreatedAt))));
        }

        if (request.MessageAttachment != MessageAttachmentFilter.All)
        {
            var withCanonicalAttachment = query.Where(item =>
                dbContext.MessageAttachments.Any(link =>
                    link.TenantId == item.message.TenantId &&
                    link.MessageId == item.message.Id &&
                    dbContext.Attachments.Any(attachment =>
                        attachment.Id == link.AttachmentId &&
                        attachment.TenantId == item.message.TenantId &&
                        attachment.DeletedAt == null &&
                        attachment.OwnerType == AttachmentOwnerType.Message &&
                        attachment.OwnerId == item.message.Id &&
                        attachment.WorkspaceId == item.conversation.WorkspaceId &&
                        attachment.ScanStatus == FileScanStatus.Clean &&
                        attachment.FileObjectId != Guid.Empty &&
                        dbContext.FileObjects.Any(file =>
                            file.Id == attachment.FileObjectId &&
                            file.TenantId == item.message.TenantId &&
                            file.WorkspaceId == item.conversation.WorkspaceId &&
                            file.ProjectId == null &&
                            file.Classification.HasValue &&
                            file.Classification != DataClassification.UnknownSensitive &&
                            file.Status == FileObjectStatus.Active &&
                            file.DeletedAt == null))));

            query = request.MessageAttachment == MessageAttachmentFilter.With
                ? withCanonicalAttachment
                : query.Where(item =>
                    !dbContext.MessageAttachments.Any(link =>
                        link.TenantId == item.message.TenantId &&
                        link.MessageId == item.message.Id &&
                        dbContext.Attachments.Any(attachment =>
                            attachment.Id == link.AttachmentId &&
                            attachment.TenantId == item.message.TenantId &&
                            attachment.DeletedAt == null &&
                            attachment.OwnerType == AttachmentOwnerType.Message &&
                            attachment.OwnerId == item.message.Id &&
                            attachment.WorkspaceId == item.conversation.WorkspaceId &&
                            attachment.ScanStatus == FileScanStatus.Clean &&
                            attachment.FileObjectId != Guid.Empty &&
                            dbContext.FileObjects.Any(file =>
                                file.Id == attachment.FileObjectId &&
                                file.TenantId == item.message.TenantId &&
                                file.WorkspaceId == item.conversation.WorkspaceId &&
                                file.ProjectId == null &&
                                file.Classification.HasValue &&
                                file.Classification != DataClassification.UnknownSensitive &&
                                file.Status == FileObjectStatus.Active &&
                                file.DeletedAt == null))));
        }

        var authorizedConversationIds = await ResolveReadableConversationIdsAsync(
            userId,
            async token => await query
                .GroupBy(item => item.conversation.Id)
                .Select(group => new
                {
                    ConversationId = group.Key,
                    LatestMessageAt = group.Max(item => item.message.CreatedAt)
                })
                .OrderByDescending(item => item.LatestMessageAt)
                .ThenBy(item => item.ConversationId)
                .Take(100)
                .Select(item => item.ConversationId)
                .ToListAsync(token),
            cancellationToken);
        if (authorizedConversationIds.Count == 0)
        {
            return [];
        }

        query = query.Where(item => authorizedConversationIds.Contains(item.conversation.Id));

        var rows = await query
            .OrderByDescending(item => item.message.CreatedAt)
            .ThenBy(item => item.message.Id)
            .Select(item => new
            {
                MessageId = item.message.Id,
                item.message.Body,
                ConversationId = item.conversation.Id,
                ConversationTitle = item.conversation.Title,
                item.conversation.WorkspaceId,
                item.message.CreatedAt
            })
            .Take(100)
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return [];
        }

        // Names are a separate, bounded attribution projection. A readable
        // historical Message remains a result even when attribution proof is
        // absent, but no cross-Tenant or structurally unrelated User name can
        // be disclosed through that row.
        var rowIds = rows.Select(row => row.MessageId).ToArray();
        var attributedAuthors = await (
                from message in dbContext.Messages.AsNoTracking()
                where Enumerable.Contains(rowIds, message.Id)
                join tenantUser in dbContext.TenantUsers.AsNoTracking()
                    on new { message.TenantId, UserId = message.AuthorUserId }
                    equals new { tenantUser.TenantId, tenantUser.UserId }
                join member in dbContext.ConversationMembers.AsNoTracking()
                    on new
                    {
                        message.TenantId,
                        message.ConversationId,
                        UserId = message.AuthorUserId
                    }
                    equals new
                    {
                        member.TenantId,
                        member.ConversationId,
                        member.UserId
                    }
                join author in dbContext.Users.AsNoTracking()
                    on message.AuthorUserId equals author.Id
                select new
                {
                    MessageId = message.Id,
                    author.DisplayName
                })
            .ToListAsync(cancellationToken);
        var authorNames = attributedAuthors
            .GroupBy(attribution => attribution.MessageId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(attribution => attribution.DisplayName).First());

        return rows
            .Select(row => new SearchResultItemResponse(
                SearchResultType.Message,
                row.MessageId,
                row.ConversationTitle ?? "Conversation",
                Snippet(row.Body),
                $"/conversations/{row.ConversationId}",
                row.WorkspaceId,
                null,
                null,
                row.CreatedAt,
                authorNames.GetValueOrDefault(row.MessageId)))
            .ToList();
    }

    private async Task<IReadOnlyList<SearchResultItemResponse>> SearchAnnouncementsAsync(Guid userId, bool isSystemAdmin, string? q, SearchRequest request, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var query = dbContext.VisibleAnnouncementsFor(userId, isSystemAdmin, now);

        query = ApplyScopeFilters(query, request, announcement => announcement.WorkspaceId, announcement => announcement.GroupId, null);
        if (!string.IsNullOrWhiteSpace(q))
        {
            query = query.Where(announcement => EF.Functions.ILike(announcement.Title, $"%{q}%") || EF.Functions.ILike(announcement.Body, $"%{q}%"));
        }

        query = ApplyDateFilters(query, request, announcement => announcement.PublishedAt);
        return await query
            .Select(announcement => new SearchResultItemResponse(SearchResultType.Announcement, announcement.Id, announcement.Title, Snippet(announcement.Body), $"/announcements/{announcement.Id}", announcement.WorkspaceId, announcement.GroupId, null, announcement.PublishedAt, announcement.AuthorUser!.DisplayName))
            .Take(100)
            .ToListAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<SearchResultItemResponse>> SearchProjectsAsync(Guid userId, string? q, SearchRequest request, CancellationToken cancellationToken)
    {
        var query = dbContext.VisibleProjectsFor(userId);
        query = ApplyScopeFilters(query, request, project => project.WorkspaceId, project => project.GroupId, project => project.Id);
        if (!string.IsNullOrWhiteSpace(q))
        {
            query = query.Where(project => EF.Functions.ILike(project.Name, $"%{q}%") || (project.Description != null && EF.Functions.ILike(project.Description, $"%{q}%")));
        }

        query = ApplyDateFilters(query, request, project => project.CreatedAt);
        return await query
            .Select(project => new SearchResultItemResponse(SearchResultType.Project, project.Id, project.Name, project.Description, $"/projects/{project.Id}", project.WorkspaceId, project.GroupId, project.Id, project.CreatedAt, project.CreatedByUser!.DisplayName))
            .Take(100)
            .ToListAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<SearchResultItemResponse>> SearchFilesAsync(
        Guid userId,
        bool isSystemAdmin,
        string? q,
        SearchRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.WorkspaceId.HasValue || request.GroupId.HasValue || request.ProjectId.HasValue)
        {
            return [];
        }

        var workspaceId = request.WorkspaceId.Value;
        var canViewWorkspace = await dbContext.Workspaces
            .AsNoTracking()
            .AnyAsync(workspace =>
                workspace.Id == workspaceId &&
                workspace.Status == WorkspaceStatus.Active &&
                !workspace.DeletedAt.HasValue &&
                (isSystemAdmin || workspace.Members.Any(member =>
                    member.UserId == userId &&
                    member.Status == MembershipStatus.Active)),
                cancellationToken);
        if (!canViewWorkspace)
        {
            return [];
        }

        var canManageSharing = isSystemAdmin || await dbContext.WorkspaceMembers
            .AsNoTracking()
            .AnyAsync(member =>
                member.WorkspaceId == workspaceId &&
                member.UserId == userId &&
                member.Status == MembershipStatus.Active &&
                (member.Role == WorkspaceRole.Owner || member.Role == WorkspaceRole.Admin),
                cancellationToken);
        var effectiveGrants = EffectiveFileAccessGrantQuery.For(dbContext);
        var effectiveRecipientFileIds = effectiveGrants
            .Where(grant => grant.WorkspaceId == workspaceId && grant.RecipientUserId == userId)
            .Select(grant => grant.FileObjectId);
        var effectiveExternalFileIds = effectiveGrants
            .Where(grant =>
                grant.WorkspaceId == workspaceId &&
                grant.RecipientKind == FileAccessGrantRecipientKind.ExternalProjectMember)
            .Select(grant => grant.FileObjectId);

        var query = FileAttachmentQueryFilters.WorkspaceFiles(dbContext, workspaceId);

        if (!canManageSharing)
        {
            // Search is another File-metadata discovery route. A Private
            // direct Workspace file must be absent here unless its uploader,
            // owner, or a currently effective server-side grant permits it.
            query = query.Where(attachment =>
                attachment.FileObject!.SharingPolicy == FileSharingPolicy.Workspace ||
                attachment.UploadedByUserId == userId ||
                attachment.OwnerUserId == userId ||
                effectiveRecipientFileIds.Contains(attachment.FileObjectId));
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            query = query.Where(attachment => EF.Functions.ILike(attachment.FileObject!.OriginalFileName, $"%{q}%"));
        }

        if (request.AuthorUserId.HasValue)
        {
            query = query.Where(attachment => attachment.FileObject!.UploadedByUserId == request.AuthorUserId.Value);
        }

        query = FileAttachmentQueryFilters.ApplyKind(query, request.FileKind);
        query = query.Where(attachment =>
            (!request.FromDate.HasValue || (attachment.FileObject!.UpdatedAt ?? attachment.FileObject.CreatedAt) >= request.FromDate.Value) &&
            (!request.ToDate.HasValue || (attachment.FileObject!.UpdatedAt ?? attachment.FileObject.CreatedAt) <= request.ToDate.Value));

        return await query
            .OrderByDescending(attachment => attachment.FileObject!.CreatedAt)
            .ThenBy(attachment => attachment.FileObjectId)
            .Select(attachment => new SearchResultItemResponse(
                SearchResultType.File,
                attachment.FileObjectId,
                attachment.FileObject!.OriginalFileName,
                null,
                $"/workspaces/{workspaceId}/files",
                workspaceId,
                null,
                null,
                attachment.FileObject.CreatedAt,
                attachment.FileObject.UploadedByUser!.DisplayName,
                attachment.FileObject.ContentType,
                attachment.FileObject.SizeBytes,
                attachment.FileObject.Status.ToString(),
                attachment.ScanStatus.ToString(),
                attachment.FileObject.UpdatedAt,
                effectiveExternalFileIds.Contains(attachment.FileObjectId)
                    ? "External"
                    : attachment.FileObject.SharingPolicy == FileSharingPolicy.Workspace
                        ? "Workspace"
                        : "Private",
                canManageSharing
                    ? effectiveGrants.Count(grant =>
                        grant.FileObjectId == attachment.FileObjectId &&
                        grant.RecipientKind == FileAccessGrantRecipientKind.ExternalProjectMember)
                    : null,
                canManageSharing,
                attachment.FileObject.SharingVersion))
            .Take(100)
            .ToListAsync(cancellationToken);
    }


    private async Task<IReadOnlyCollection<Guid>> ResolveReadableConversationIdsAsync(
        Guid userId,
        Func<CancellationToken, Task<List<Guid>>> fallbackCandidateIds,
        CancellationToken cancellationToken)
    {
        var readableConversationIds = messaging.QueryReadableConversationIds(userId);
        if (readableConversationIds is not null)
        {
            return await readableConversationIds.ToArrayAsync(cancellationToken);
        }

        var candidateConversationIds = await fallbackCandidateIds(cancellationToken);
        return await messaging.FilterReadableConversationIdsAsync(
            userId,
            candidateConversationIds,
            cancellationToken);
    }

    private IQueryable<Guid> ScopedVisibleProjectIds(Guid userId, SearchRequest request)
    {
        var projects = dbContext.VisibleProjectsFor(userId);
        if (request.WorkspaceId.HasValue)
        {
            projects = projects.Where(project => project.WorkspaceId == request.WorkspaceId);
        }

        if (request.GroupId.HasValue)
        {
            projects = projects.Where(project => project.GroupId == request.GroupId);
        }

        if (request.ProjectId.HasValue)
        {
            projects = projects.Where(project => project.Id == request.ProjectId);
        }

        return projects.Select(project => project.Id);
    }

    private async Task<IReadOnlyList<SearchResultItemResponse>> SearchTasksAsync(Guid userId, string? q, SearchRequest request, CancellationToken cancellationToken)
    {
        var visibleProjectIds = ScopedVisibleProjectIds(userId, request);
        var query = dbContext.TaskItems.AsNoTracking()
            .Where(task => task.DeletedAt == null && visibleProjectIds.Contains(task.ProjectId))
            .Join(dbContext.Projects, task => task.ProjectId, project => project.Id, (task, project) => new { task, project });

        if (!string.IsNullOrWhiteSpace(q))
        {
            query = query.Where(item => EF.Functions.ILike(item.task.Title, $"%{q}%") || (item.task.Description != null && EF.Functions.ILike(item.task.Description, $"%{q}%")));
        }

        query = query.Where(item =>
            (!request.FromDate.HasValue || item.task.CreatedAt >= request.FromDate.Value) &&
            (!request.ToDate.HasValue || item.task.CreatedAt <= request.ToDate.Value));

        return await query
            .Select(item => new SearchResultItemResponse(SearchResultType.Task, item.task.Id, item.task.Title, item.task.Description, $"/tasks/{item.task.Id}", item.project.WorkspaceId, item.project.GroupId, item.project.Id, item.task.CreatedAt, item.task.CreatedByUser!.DisplayName))
            .Take(100)
            .ToListAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<SearchResultItemResponse>> SearchArtifactsAsync(Guid userId, string? q, SearchRequest request, CancellationToken cancellationToken)
    {
        var visibleProjectIds = ScopedVisibleProjectIds(userId, request);
        var query = dbContext.Artifacts.AsNoTracking()
            .Where(artifact => artifact.DeletedAt == null && visibleProjectIds.Contains(artifact.ProjectId))
            .Join(dbContext.Projects, artifact => artifact.ProjectId, project => project.Id, (artifact, project) => new { artifact, project });

        if (!string.IsNullOrWhiteSpace(q))
        {
            query = query.Where(item => EF.Functions.ILike(item.artifact.Name, $"%{q}%") || (item.artifact.Description != null && EF.Functions.ILike(item.artifact.Description, $"%{q}%")));
        }

        query = query.Where(item =>
            (!request.FromDate.HasValue || item.artifact.CreatedAt >= request.FromDate.Value) &&
            (!request.ToDate.HasValue || item.artifact.CreatedAt <= request.ToDate.Value));

        return await query
            .Select(item => new SearchResultItemResponse(SearchResultType.Artifact, item.artifact.Id, item.artifact.Name, item.artifact.Description, $"/artifacts/{item.artifact.Id}", item.project.WorkspaceId, item.project.GroupId, item.project.Id, item.artifact.CreatedAt, item.artifact.CreatedByUser!.DisplayName))
            .Take(100)
            .ToListAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<SearchResultItemResponse>> SearchActivityLogsAsync(Guid userId, string? q, SearchRequest request, CancellationToken cancellationToken)
    {
        var visibleProjectIds = ScopedVisibleProjectIds(userId, request);
        var query = dbContext.ActivityLogs.AsNoTracking()
            .Where(log => visibleProjectIds.Contains(log.ProjectId))
            .Join(dbContext.Projects, log => log.ProjectId, project => project.Id, (log, project) => new { log, project });

        if (request.AuthorUserId.HasValue)
        {
            query = query.Where(item => item.log.AuthorUserId == request.AuthorUserId);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            query = query.Where(item => EF.Functions.ILike(item.log.Body, $"%{q}%"));
        }

        query = query.Where(item =>
            (!request.FromDate.HasValue || item.log.CreatedAt >= request.FromDate.Value) &&
            (!request.ToDate.HasValue || item.log.CreatedAt <= request.ToDate.Value));

        return await query
            .Select(item => new SearchResultItemResponse(SearchResultType.ActivityLog, item.log.Id, item.log.ActivityType.ToString(), Snippet(item.log.Body), $"/projects/{item.project.Id}/activity/{item.log.Id}", item.project.WorkspaceId, item.project.GroupId, item.project.Id, item.log.CreatedAt, item.log.AuthorUser!.DisplayName))
            .Take(100)
            .ToListAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<SearchResultItemResponse>> SearchCommentsAsync(Guid userId, string? q, SearchRequest request, CancellationToken cancellationToken)
    {
        var visibleProjectIds = ScopedVisibleProjectIds(userId, request);
        var query = dbContext.Comments.AsNoTracking()
            .Where(comment => comment.DeletedAt == null &&
                ((comment.TargetType == CommentTargetType.Project && visibleProjectIds.Contains(comment.TargetId)) ||
                 (comment.TargetType == CommentTargetType.TaskItem && dbContext.TaskItems.Any(item =>
                     item.Id == comment.TargetId && visibleProjectIds.Contains(item.ProjectId))) ||
                 (comment.TargetType == CommentTargetType.Milestone && dbContext.Milestones.Any(item =>
                     item.Id == comment.TargetId && visibleProjectIds.Contains(item.ProjectId))) ||
                 (comment.TargetType == CommentTargetType.Artifact && dbContext.Artifacts.Any(item =>
                     item.Id == comment.TargetId && visibleProjectIds.Contains(item.ProjectId))) ||
                 (comment.TargetType == CommentTargetType.ArtifactVersion && dbContext.ArtifactVersions.Any(item =>
                     item.Id == comment.TargetId && item.Artifact != null && visibleProjectIds.Contains(item.Artifact.ProjectId))) ||
                 (comment.TargetType == CommentTargetType.ActivityLog && dbContext.ActivityLogs.Any(item =>
                     item.Id == comment.TargetId && visibleProjectIds.Contains(item.ProjectId)))));

        if (request.WorkspaceId.HasValue)
        {
            query = query.Where(comment => comment.WorkspaceId == request.WorkspaceId.Value);
        }

        if (request.AuthorUserId.HasValue)
        {
            query = query.Where(comment => comment.AuthorUserId == request.AuthorUserId);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            query = query.Where(comment => EF.Functions.ILike(comment.Body, $"%{q}%"));
        }

        query = ApplyDateFilters(query, request, comment => comment.CreatedAt);
        return await query
            .Select(comment => new SearchResultItemResponse(SearchResultType.Comment, comment.Id, comment.TargetType.ToString(), Snippet(comment.Body), $"/comments/{comment.Id}", comment.WorkspaceId, null, null, comment.CreatedAt, comment.AuthorUser!.DisplayName))
            .Take(100)
            .ToListAsync(cancellationToken);
    }

    private IQueryable<Domain.Entities.Channel> VisibleChannels(Guid userId, bool isSystemAdmin)
    {
        var query = dbContext.Channels.AsNoTracking().Where(channel => channel.DeletedAt == null && channel.Status == ChannelStatus.Active);
        if (isSystemAdmin)
        {
            return query;
        }

        return query.Where(channel =>
            ((channel.Type == ChannelType.Public || channel.Type == ChannelType.Announcement) &&
                dbContext.GroupMembers.Any(member => member.GroupId == channel.GroupId && member.UserId == userId)) ||
            dbContext.ChannelMembers.Any(member => member.ChannelId == channel.Id && member.UserId == userId));
    }

    private static bool ShouldInclude(SearchResultType requested, SearchResultType item)
    {
        return requested == SearchResultType.All || requested == item;
    }

    private static string? Snippet(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Length <= 180 ? value : value[..180];
    }

    private static IQueryable<T> ApplyDateFilters<T>(IQueryable<T> query, SearchRequest request, System.Linq.Expressions.Expression<Func<T, DateTimeOffset>> createdAt)
    {
        if (request.FromDate.HasValue)
        {
            query = query.Where(ExpressionGreaterThanOrEqual(createdAt, request.FromDate.Value));
        }

        if (request.ToDate.HasValue)
        {
            query = query.Where(ExpressionLessThanOrEqual(createdAt, request.ToDate.Value));
        }

        return query;
    }

    private static IQueryable<T> ApplyScopeFilters<T>(
        IQueryable<T> query,
        SearchRequest request,
        System.Linq.Expressions.Expression<Func<T, Guid?>> workspaceId,
        System.Linq.Expressions.Expression<Func<T, Guid?>> groupId,
        System.Linq.Expressions.Expression<Func<T, Guid?>>? projectId)
    {
        if (request.WorkspaceId.HasValue)
        {
            query = query.Where(ExpressionEqual(workspaceId, request.WorkspaceId.Value));
        }

        if (request.GroupId.HasValue)
        {
            query = query.Where(ExpressionEqual(groupId, request.GroupId.Value));
        }

        if (request.ProjectId.HasValue && projectId is not null)
        {
            query = query.Where(ExpressionEqual(projectId, request.ProjectId.Value));
        }

        return query;
    }

    private static IQueryable<T> ApplyScopeFilters<T>(
        IQueryable<T> query,
        SearchRequest request,
        System.Linq.Expressions.Expression<Func<T, Guid>> workspaceId,
        System.Linq.Expressions.Expression<Func<T, Guid?>> groupId,
        System.Linq.Expressions.Expression<Func<T, Guid>>? projectId)
    {
        if (request.WorkspaceId.HasValue)
        {
            query = query.Where(ExpressionEqual(workspaceId, request.WorkspaceId.Value));
        }

        if (request.GroupId.HasValue)
        {
            query = query.Where(ExpressionEqual(groupId, request.GroupId.Value));
        }

        if (request.ProjectId.HasValue && projectId is not null)
        {
            query = query.Where(ExpressionEqual(projectId, request.ProjectId.Value));
        }

        return query;
    }

    private static System.Linq.Expressions.Expression<Func<T, bool>> ExpressionEqual<T, TProperty>(System.Linq.Expressions.Expression<Func<T, TProperty>> property, Guid value)
    {
        var converted = System.Linq.Expressions.Expression.Convert(property.Body, typeof(Guid?));
        var constant = System.Linq.Expressions.Expression.Constant((Guid?)value, typeof(Guid?));
        return System.Linq.Expressions.Expression.Lambda<Func<T, bool>>(System.Linq.Expressions.Expression.Equal(converted, constant), property.Parameters);
    }

    private static System.Linq.Expressions.Expression<Func<T, bool>> ExpressionGreaterThanOrEqual<T>(System.Linq.Expressions.Expression<Func<T, DateTimeOffset>> property, DateTimeOffset value)
    {
        return System.Linq.Expressions.Expression.Lambda<Func<T, bool>>(
            System.Linq.Expressions.Expression.GreaterThanOrEqual(property.Body, System.Linq.Expressions.Expression.Constant(value)),
            property.Parameters);
    }

    private static System.Linq.Expressions.Expression<Func<T, bool>> ExpressionLessThanOrEqual<T>(System.Linq.Expressions.Expression<Func<T, DateTimeOffset>> property, DateTimeOffset value)
    {
        return System.Linq.Expressions.Expression.Lambda<Func<T, bool>>(
            System.Linq.Expressions.Expression.LessThanOrEqual(property.Body, System.Linq.Expressions.Expression.Constant(value)),
            property.Parameters);
    }
}
