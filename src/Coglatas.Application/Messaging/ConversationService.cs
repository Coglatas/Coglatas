using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Files;
using Coglatas.Application.Projects;
using Coglatas.Application.Realtime;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using System.Text.Json;

namespace Coglatas.Application.Messaging;

public sealed class ConversationService(
    IMessagingRepository messaging,
    IUserRepository users,
    IWorkspaceRepository workspaces,
    IProjectRepository projects,
    IProjectAuthorizationService projectAuthorization,
    IConversationAuthorizationService authorization,
    IFileRepository files,
    IFileService fileService,
    ICurrentUser currentUser,
    IClock clock,
    CommunicationSafetyOptions safetyOptions,
    ICommunicationSafetyGuard safetyGuard,
    IAuditLogger auditLogger,
    INotificationService notifications,
    ITransactionalOutbox outbox,
    ICurrentTenant currentTenant,
    IMessageIdempotencyCommitCoordinator messageIdempotency,
    IUnitOfWork unitOfWork) : IConversationService
{
    private const long MaxAttachmentBytes = 25 * 1024 * 1024;
    private const int MaximumThreadReplies = 100;
    private const int MaximumThreadParticipantNames = 3;
    private static readonly HashSet<string> AllowedExtensions = [".pdf", ".png", ".jpg", ".jpeg", ".gif", ".txt", ".docx", ".xlsx", ".pptx", ".zip"];

    public async Task<Result<ConversationInboxResponse>> ListAsync(ConversationListQuery query, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId)) return Result<ConversationInboxResponse>.Failure("Authentication is required.");
        if (!Enum.IsDefined(query.View)) return Result<ConversationInboxResponse>.Failure("Inbox view is invalid.");
        var inbox = await messaging.ListInboxForUserAsync(
            userId,
            query.View,
            query.SafePage,
            query.SafePageSize,
            cancellationToken);
        var conversations = inbox.Page;
        var readableIds = await messaging.FilterReadableConversationIdsAsync(
            userId,
            conversations.Items.Select(conversation => conversation.Id).ToArray(),
            cancellationToken);
        var mentionConversationIds = await MentionAttentionResolver.ListUnreadMentionConversationIdsAsync(
            notifications,
            messaging,
            userId,
            readableIds,
            cancellationToken);
        var result = new List<ConversationListItemResponse>();
        foreach (var conversation in conversations.Items)
        {
            // Repository filtering recursively scopes paging/counts. This
            // batched check is the final current-authorization boundary
            // before any title or last-message content is mapped.
            if (!readableIds.Contains(conversation.Id))
            {
                continue;
            }

            var page = await messaging.ListMessagesAsync(conversation.Id, 1, null, cancellationToken);
            var last = page.Items.FirstOrDefault();
            var read = await messaging.GetReadStateAsync(conversation.Id, userId, cancellationToken);
            var unread = await messaging.CountUnreadMessagesAsync(conversation.Id, userId, read?.LastReadAt, cancellationToken);
            var member = await messaging.GetMemberAsync(conversation.Id, userId, cancellationToken);
            IReadOnlyList<ConversationMember> members = conversation.Type == ConversationType.DirectMessage
                ? await messaging.ListMembersAsync(conversation.Id, cancellationToken)
                : [];
            result.Add(new ConversationListItemResponse(
                conversation.Id,
                conversation.WorkspaceId,
                conversation.ProjectId,
                conversation.Type,
                ConversationTitleFor(conversation, members, userId),
                conversation.ParentConversationId,
                conversation.RootConversationId,
                last is null ? null : ToMessage(last),
                unread,
                mentionConversationIds.Contains(conversation.Id),
                member?.IsMuted ?? false,
                member?.IsArchived ?? false,
                member?.IsLater ?? false,
                conversation.CreatedAt,
                conversation.UpdatedAt));
        }
        return Result<ConversationInboxResponse>.Success(new ConversationInboxResponse(
            result,
            conversations.Page,
            conversations.PageSize,
            conversations.TotalCount,
            query.View,
            inbox.Counts));
    }

    public async Task<Result<IReadOnlyList<ConversationRecipientResponse>>> ListRecipientsAsync(string? query, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId))
        {
            return Result<IReadOnlyList<ConversationRecipientResponse>>.Failure("Authentication is required.");
        }

        var recipients = await messaging.SearchDirectRecipientsAsync(userId, query, 20, cancellationToken);
        return Result<IReadOnlyList<ConversationRecipientResponse>>.Success(
            recipients
                .Select(user => new ConversationRecipientResponse(user.Id, user.DisplayName))
                .ToList());
    }

    public async Task<Result<ConversationDetailResponse>> CreateDirectAsync(CreateDirectConversationRequest request, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId))
        {
            return Result<ConversationDetailResponse>.Failure("Authentication is required.");
        }

        if (request.RecipientUserId == Guid.Empty || request.RecipientUserId == userId)
        {
            return Result<ConversationDetailResponse>.Failure("Recipient user is not allowed.");
        }

        var existing = await messaging.FindDirectForUsersAsync(userId, request.RecipientUserId, cancellationToken);
        if (existing is not null)
        {
            if (!await authorization.CanViewConversation(userId, existing.Id, cancellationToken))
            {
                return Result<ConversationDetailResponse>.Failure("Conversation not found.");
            }

            return Result<ConversationDetailResponse>.Success(await ToDetailAsync(existing, cancellationToken, userId));
        }

        var sharedWorkspace = await messaging.FindSharedActiveWorkspaceAsync(userId, request.RecipientUserId, cancellationToken);
        var recipient = await users.GetByIdAsync(request.RecipientUserId, cancellationToken);
        if (recipient is null ||
            recipient.DeletedAt.HasValue ||
            recipient.Status != UserStatus.Active ||
            sharedWorkspace is null)
        {
            return Result<ConversationDetailResponse>.Failure("Recipient user not found.");
        }

        return await CreateAsync(new CreateConversationRequest(
            ConversationType.DirectMessage,
            null,
            [request.RecipientUserId],
            sharedWorkspace.Id), cancellationToken);
    }

    public async Task<Result<ConversationDetailResponse>> CreateAsync(CreateConversationRequest request, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId)) return Result<ConversationDetailResponse>.Failure("Authentication is required.");

        if (!IsSupportedMvpType(request.Type))
        {
            return Result<ConversationDetailResponse>.Failure("Conversation type is not supported in MVP.");
        }

        if (request.Type == ConversationType.Thread)
        {
            return await CreateThreadAsync(request, userId, cancellationToken);
        }

        if (!request.WorkspaceId.HasValue || request.WorkspaceId.Value == Guid.Empty)
        {
            return Result<ConversationDetailResponse>.Failure("WorkspaceId is required.");
        }

        var workspace = await workspaces.GetByIdAsync(request.WorkspaceId.Value, cancellationToken);
        if (workspace is null || workspace.DeletedAt.HasValue || workspace.Status != WorkspaceStatus.Active)
        {
            return Result<ConversationDetailResponse>.Failure("Workspace not found.");
        }

        Project? project = null;
        if (request.Type == ConversationType.ProjectChannel)
        {
            if (!request.ProjectId.HasValue || request.ProjectId.Value == Guid.Empty)
            {
                return Result<ConversationDetailResponse>.Failure("ProjectChannel requires ProjectId.");
            }

            project = await projects.GetProjectAsync(request.ProjectId.Value, cancellationToken);
            if (project is null || project.DeletedAt.HasValue || project.WorkspaceId != request.WorkspaceId.Value)
            {
                return Result<ConversationDetailResponse>.Failure("Project must belong to the selected workspace.");
            }

            if (!await CanBindConversationToProjectAsync(project, userId, cancellationToken))
            {
                return Result<ConversationDetailResponse>.Failure("Project not found.");
            }
        }
        else if (request.ProjectId.HasValue)
        {
            project = await projects.GetProjectAsync(request.ProjectId.Value, cancellationToken);
            if (project is null || project.DeletedAt.HasValue || project.WorkspaceId != request.WorkspaceId.Value)
            {
                return Result<ConversationDetailResponse>.Failure("Project must belong to the selected workspace.");
            }

            if (!await CanBindConversationToProjectAsync(project, userId, cancellationToken))
            {
                return Result<ConversationDetailResponse>.Failure("Project not found.");
            }
        }

        var memberIds = request.MemberUserIds.Append(userId).Distinct().ToList();
        if (request.Type == ConversationType.DirectMessage)
        {
            if (memberIds.Count != 2) return Result<ConversationDetailResponse>.Failure("Direct conversations require exactly two members.");
            var existing = await messaging.FindDirectAsync(
                request.WorkspaceId.Value,
                project?.Id,
                memberIds[0],
                memberIds[1],
                cancellationToken);
            if (existing is not null)
            {
                if (!await authorization.CanViewConversation(userId, existing.Id, cancellationToken))
                {
                    return Result<ConversationDetailResponse>.Failure("Conversation not found.");
                }

                return Result<ConversationDetailResponse>.Success(await ToDetailAsync(existing, cancellationToken, userId));
            }
        }

        foreach (var memberId in memberIds)
        {
            if (await users.GetByIdAsync(memberId, cancellationToken) is null)
            {
                return Result<ConversationDetailResponse>.Failure("Conversation member not found.");
            }
            if (project is not null &&
                !await projectAuthorization.CanViewProject(memberId, project.Id, cancellationToken))
            {
                return Result<ConversationDetailResponse>.Failure("Conversation member not found.");
            }
        }

        var conversation = new Conversation
        {
            WorkspaceId = request.WorkspaceId.Value,
            ProjectId = project?.Id,
            Type = request.Type,
            Title = request.Type == ConversationType.DirectMessage ? null : request.Title?.Trim(),
            CreatedByUserId = userId
        };
        await messaging.AddConversationAsync(conversation, cancellationToken);
        foreach (var memberId in memberIds)
        {
            await messaging.AddMemberAsync(new ConversationMember
            {
                ConversationId = conversation.Id,
                UserId = memberId,
                Role = memberId == userId ? ConversationMemberRole.Admin : ConversationMemberRole.Member,
                CanRead = true,
                CanPost = true,
                CanManageMembers = memberId == userId,
                CanCreateThread = true,
                JoinedAt = clock.UtcNow
            }, cancellationToken);
        }
        await AuditAsync(userId, "ConversationCreated", conversation.Id, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result<ConversationDetailResponse>.Success(await ToDetailAsync(conversation, cancellationToken, userId));
    }

    public async Task<Result<ConversationDetailResponse>> GetAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId)) return Result<ConversationDetailResponse>.Failure("Conversation not found.");
        if (!await authorization.CanViewConversation(userId, conversationId, cancellationToken))
        {
            return await DenyAsync<ConversationDetailResponse>(userId, "ConversationAccessDenied", "Conversation", conversationId, "Conversation not found.", cancellationToken);
        }

        var conversation = await messaging.GetConversationAsync(conversationId, cancellationToken);
        return conversation is null ? Result<ConversationDetailResponse>.Failure("Conversation not found.") : Result<ConversationDetailResponse>.Success(await ToDetailAsync(conversation, cancellationToken, userId));
    }

    public async Task<Result<ConversationDetailResponse>> UpdateAsync(Guid conversationId, UpdateConversationRequest request, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId)) return Result<ConversationDetailResponse>.Failure("You are not allowed to manage this conversation.");
        if (!await authorization.CanManageConversation(userId, conversationId, cancellationToken))
        {
            return await DenyAsync<ConversationDetailResponse>(userId, "ConversationManageDenied", "Conversation", conversationId, "You are not allowed to manage this conversation.", cancellationToken);
        }

        var conversation = await messaging.GetConversationAsync(conversationId, cancellationToken);
        if (conversation is null || conversation.Type == ConversationType.DirectMessage) return Result<ConversationDetailResponse>.Failure("Conversation not found.");
        conversation.Title = request.Title?.Trim();
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result<ConversationDetailResponse>.Success(await ToDetailAsync(conversation, cancellationToken));
    }

    public async Task<Result> LeaveAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId)) return Result.Failure("Authentication is required.");
        var member = await messaging.GetMemberAsync(conversationId, userId, cancellationToken);
        if (member is null) return Result.Failure("Conversation not found.");
        member.LeftAt = clock.UtcNow;
        await AuditAsync(userId, "ConversationMemberLeft", conversationId, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result<IReadOnlyList<ConversationMemberResponse>>> ListMembersAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId)) return Result<IReadOnlyList<ConversationMemberResponse>>.Failure("Conversation not found.");
        if (!await authorization.CanViewConversation(userId, conversationId, cancellationToken))
        {
            return await DenyAsync<IReadOnlyList<ConversationMemberResponse>>(userId, "ConversationAccessDenied", "Conversation", conversationId, "Conversation not found.", cancellationToken);
        }

        var members = await messaging.ListMembersAsync(conversationId, cancellationToken);
        return Result<IReadOnlyList<ConversationMemberResponse>>.Success(members.Select(ToMember).ToList());
    }

    public async Task<Result<ConversationMemberResponse>> AddMemberAsync(Guid conversationId, AddConversationMemberRequest request, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId)) return Result<ConversationMemberResponse>.Failure("You are not allowed to manage this conversation.");
        if (!await authorization.CanManageConversation(userId, conversationId, cancellationToken))
        {
            return await DenyAsync<ConversationMemberResponse>(userId, "ConversationManageDenied", "Conversation", conversationId, "You are not allowed to manage this conversation.", cancellationToken);
        }

        var conversation = await messaging.GetConversationAsync(conversationId, cancellationToken);
        if (conversation is null || conversation.Type == ConversationType.DirectMessage) return Result<ConversationMemberResponse>.Failure("Direct conversations cannot add extra members.");
        if (conversation.Type == ConversationType.Thread) return Result<ConversationMemberResponse>.Failure("Thread membership is inherited from the parent conversation.");
        var existingMember = await messaging.GetMemberAsync(conversationId, request.UserId, cancellationToken);
        if (IsActiveParticipant(existingMember)) return Result<ConversationMemberResponse>.Failure("User is already a conversation member.");
        var user = await users.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null) return Result<ConversationMemberResponse>.Failure("User not found.");
        var member = existingMember ?? new ConversationMember { ConversationId = conversationId, UserId = request.UserId };
        member.User = user;
        member.Role = ConversationMemberRole.Member;
        member.CanRead = true;
        member.CanPost = true;
        member.CanManageMembers = false;
        member.CanCreateThread = true;
        member.JoinedAt = clock.UtcNow;
        member.LeftAt = null;
        member.RemovedAt = null;
        member.RemovedByUserId = null;
        if (existingMember is null)
        {
            await messaging.AddMemberAsync(member, cancellationToken);
        }
        await AuditAsync(userId, "ConversationMemberAdded", conversationId, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result<ConversationMemberResponse>.Success(ToMember(member));
    }

    public async Task<Result> RemoveMemberAsync(Guid conversationId, Guid removeUserId, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId)) return Result.Failure("You are not allowed to manage this conversation.");
        if (!await authorization.CanManageConversation(userId, conversationId, cancellationToken))
        {
            return await DenyAsync(userId, "ConversationManageDenied", "Conversation", conversationId, "You are not allowed to manage this conversation.", cancellationToken);
        }

        var member = await messaging.GetMemberAsync(conversationId, removeUserId, cancellationToken);
        if (member is null) return Result.Failure("Conversation member not found.");
        member.LeftAt = clock.UtcNow;
        member.RemovedAt = member.LeftAt;
        member.RemovedByUserId = userId;
        await AuditAsync(userId, "ConversationMemberRemoved", conversationId, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result<PagedResponse<MessageResponse>>> ListMessagesAsync(Guid conversationId, MessageListQuery query, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId)) return Result<PagedResponse<MessageResponse>>.Failure("Conversation not found.");
        if (!await authorization.CanViewConversation(userId, conversationId, cancellationToken))
        {
            return await DenyAsync<PagedResponse<MessageResponse>>(userId, "ConversationAccessDenied", "Conversation", conversationId, "Conversation not found.", cancellationToken);
        }

        PagedResponse<Message> page;
        if (query.AnchorMessageId.HasValue)
        {
            var focusMessage = await messaging.GetMessageAsync(query.AnchorMessageId.Value, cancellationToken);
            if (focusMessage is null ||
                focusMessage.DeletedAt.HasValue ||
                focusMessage.ConversationId != conversationId)
            {
                return await DenyAsync<PagedResponse<MessageResponse>>(
                    userId,
                    "ConversationAccessDenied",
                    "Message",
                    query.AnchorMessageId.Value,
                    "Message not found.",
                    cancellationToken,
                    "anchor_message_denied");
            }

            // Replies are rendered inside their root's thread panel. Anchor
            // the Conversation timeline on that root while preserving the
            // exact reply identity in the route so the client can open/focus it.
            var timelineAnchorId = focusMessage.ThreadRootMessageId ?? focusMessage.Id;
            page = await messaging.ListMessageContextAsync(
                conversationId,
                timelineAnchorId,
                query.SafeLimit,
                cancellationToken);
        }
        else
        {
            page = await messaging.ListMessagesAsync(conversationId, query.SafeLimit, query.Before, cancellationToken);
        }
        var threadSummaries = await messaging.GetThreadSummariesAsync(
            conversationId,
            page.Items.Select(message => message.Id).ToArray(),
            MaximumThreadParticipantNames,
            cancellationToken);
        return Result<PagedResponse<MessageResponse>>.Success(new PagedResponse<MessageResponse>(
            page.Items
                .Select(message => ToMessage(message, threadSummaries.GetValueOrDefault(message.Id)))
                .ToList(),
            1,
            query.SafeLimit,
            page.TotalCount));
    }

    public Task<Result<MessageResponse>> SendMessageAsync(Guid conversationId, SendMessageRequest request, CancellationToken cancellationToken = default)
    {
        return SendMessageCoreAsync(conversationId, request, threadRootMessageId: null, cancellationToken);
    }

    private async Task<Result<MessageResponse>> SendMessageCoreAsync(
        Guid conversationId,
        SendMessageRequest request,
        Guid? threadRootMessageId,
        CancellationToken cancellationToken)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId)) return Result<MessageResponse>.Failure("You are not allowed to send messages.");
        if (!await authorization.CanSendMessage(userId, conversationId, cancellationToken))
        {
            return await DenyAsync<MessageResponse>(userId, "communication.message_post_denied", "Conversation", conversationId, "You are not allowed to send messages.", cancellationToken, "post_permission_denied");
        }

        var attachments = request.Attachments ?? [];
        var conversation = await messaging.GetConversationAsync(conversationId, cancellationToken);
        if (conversation is null) return Result<MessageResponse>.Failure("Conversation not found.");

        if (request.ClientRequestId.HasValue)
        {
            var existing = await messaging.FindMessageByClientRequestIdAsync(conversationId, userId, request.ClientRequestId.Value, cancellationToken);
            if (existing is not null)
            {
                if (existing.ThreadRootMessageId != threadRootMessageId)
                {
                    return Result<MessageResponse>.Failure("Client request identity is already used for another message target.");
                }
                return Result<MessageResponse>.Success(ToMessage(existing));
            }
        }
        var normalizedBody = request.Body?.Trim() ?? string.Empty;
        if (!IsSupportedMvpType(conversation.Type))
        {
            return await DenyAsync<MessageResponse>(userId, "communication.message_post_denied", "Conversation", conversationId, "You are not allowed to send messages.", cancellationToken, "disabled_conversation_type");
        }

        if (conversation.IsArchived)
        {
            return await DenyAsync<MessageResponse>(userId, "communication.message_post_denied", "Conversation", conversationId, "You are not allowed to send messages.", cancellationToken, "archived_conversation");
        }

        if (conversation.IsLocked)
        {
            return await DenyAsync<MessageResponse>(userId, "communication.message_post_denied", "Conversation", conversationId, "You are not allowed to send messages.", cancellationToken, conversation.Type == ConversationType.Thread ? "locked_thread" : "locked_conversation");
        }

        if (string.IsNullOrWhiteSpace(normalizedBody) && attachments.Count == 0)
        {
            await LogCommunicationAuditAsync(userId, "communication.message_post_denied", "Conversation", conversation.Id, conversation, null, conversation.Type == ConversationType.Thread ? conversation.Id : null, "deny", "body_empty", cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Result<MessageResponse>.Failure("Message body or attachment is required.");
        }

        if (normalizedBody.Length > safetyOptions.MaxMessageLength)
        {
            await LogCommunicationAuditAsync(userId, "communication.message_post_denied", "Conversation", conversation.Id, conversation, null, conversation.Type == ConversationType.Thread ? conversation.Id : null, "deny", "body_too_large", cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Result<MessageResponse>.Failure("Message body is too large.");
        }

        if (attachments.Count > safetyOptions.MaxAttachmentsPerMessage)
        {
            await LogCommunicationAuditAsync(userId, "communication.message_post_denied", "Conversation", conversation.Id, conversation, null, conversation.Type == ConversationType.Thread ? conversation.Id : null, "deny", "attachment_count_too_large", cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Result<MessageResponse>.Failure("Too many attachments.");
        }

        var safetyDecision = safetyGuard.CheckMessagePost(new CommunicationSafetyScope(userId, conversation.TenantId, conversation.WorkspaceId, conversation.Id), normalizedBody, clock.UtcNow);
        if (!safetyDecision.IsAllowed)
        {
            var action = safetyDecision.ReasonCode == "duplicate_post" ? "communication.spam_guard_triggered" : "communication.rate_limited";
            await LogCommunicationAuditAsync(userId, action, "Conversation", conversation.Id, conversation, null, conversation.Type == ConversationType.Thread ? conversation.Id : null, "deny", safetyDecision.ReasonCode, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Result<MessageResponse>.Failure("Message cannot be posted right now.");
        }

        var canonicalAttachments = new List<Attachment>(attachments.Count);
        var canonicalFileObjectIds = new HashSet<Guid>();
        foreach (var reference in attachments)
        {
            var source = await ResolveMessageAttachmentSourceAsync(conversation, reference.AttachmentId, cancellationToken);
            if (source is null)
            {
                await LogCommunicationAuditAsync(userId, "communication.message_post_denied", "Conversation", conversation.Id, conversation, null, conversation.Type == ConversationType.Thread ? conversation.Id : null, "deny", "attachment_unavailable", cancellationToken);
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return Result<MessageResponse>.Failure("Attachment is unavailable or not authorized.");
            }

            if (canonicalFileObjectIds.Add(source.FileObjectId))
            {
                canonicalAttachments.Add(source);
            }
        }

        var message = new Message
        {
            // Notification policy is evaluated before the unit-of-work save that
            // normally stamps tenant-owned entities. Carry the already-authorized
            // conversation tenant so preference checks can fail closed without
            // suppressing every newly posted Message notification.
            TenantId = conversation.TenantId,
            WorkspaceId = conversation.WorkspaceId,
            ConversationId = conversationId,
            AuthorUserId = userId,
            Body = normalizedBody,
            ClientRequestId = request.ClientRequestId,
            ThreadRootMessageId = threadRootMessageId,
            Version = 1,
            CreatedAt = clock.UtcNow
        };
        await messaging.AddMessageAsync(message, cancellationToken);
        foreach (var source in canonicalAttachments)
        {
            var fileObject = source.FileObject!;
            var attachment = new Attachment
            {
                TenantId = conversation.TenantId,
                FileObjectId = fileObject.Id,
                WorkspaceId = conversation.WorkspaceId,
                OwnerType = AttachmentOwnerType.Message,
                OwnerId = message.Id,
                OwnerUserId = userId,
                UploadedByUserId = fileObject.UploadedByUserId,
                FileName = fileObject.OriginalFileName,
                StoredFileName = fileObject.Id.ToString("N"),
                FilePath = fileObject.StorageKey,
                ContentType = fileObject.ContentType,
                Extension = Path.GetExtension(fileObject.OriginalFileName),
                SizeBytes = fileObject.SizeBytes,
                StorageProvider = source.StorageProvider,
                StorageKey = fileObject.StorageKey,
                ScanStatus = source.ScanStatus,
                FileObject = fileObject
            };
            await messaging.AddAttachmentAsync(attachment, new MessageAttachment
            {
                TenantId = conversation.TenantId,
                MessageId = message.Id,
                AttachmentId = attachment.Id
            }, cancellationToken);
            await AuditAsync(userId, "MessageAttachmentAdded", message.Id, cancellationToken);
        }
        var requestedMentionUserIds = (request.MentionedUserIds ?? [])
            .Where(mentionedUserId => mentionedUserId != Guid.Empty && mentionedUserId != userId)
            .ToHashSet();
        var members = await messaging.ListMembersAsync(conversationId, cancellationToken);
        foreach (var member in members.Where(m => m.UserId != userId && IsActiveParticipant(m)))
        {
            if (requestedMentionUserIds.Contains(member.UserId))
            {
                await notifications.CreateAsync(
                    member.UserId,
                    NotificationType.Mention,
                    "You were mentioned in a message",
                    "Open the conversation to review the mention.",
                    "Message",
                    message.Id,
                    cancellationToken);
            }
            else
            {
                await notifications.NotifyAsync(member.UserId, "New direct message", "You have a new message.", "Message", message.Id, cancellationToken);
            }
        }
        await LogCommunicationAuditAsync(
            userId,
            threadRootMessageId.HasValue ? "communication.thread_reply_posted" : "communication.message_posted",
            "Message",
            message.Id,
            conversation,
            message.Id,
            threadRootMessageId ?? (conversation.Type == ConversationType.Thread ? conversation.Id : null),
            "allow",
            threadRootMessageId.HasValue ? "thread_reply_posted" : "posted",
            cancellationToken);
        message.AuthorUser = await users.GetByIdAsync(userId, cancellationToken);
        var createdEvent = await EnqueueMessageCreatedAsync(conversation, message, userId, cancellationToken);
        if (!createdEvent.IsSuccess)
        {
            return Result<MessageResponse>.Failure(createdEvent.Error!);
        }
        if (threadRootMessageId.HasValue)
        {
            var existingSummary = await messaging.GetThreadSummaryAsync(
                conversationId,
                threadRootMessageId.Value,
                MaximumThreadParticipantNames,
                cancellationToken: cancellationToken);
            var threadChanged = await EnqueueThreadChangedAsync(
                conversation,
                threadRootMessageId.Value,
                existingSummary.ReplyCount + 1,
                message.CreatedAt,
                "replyCreated",
                userId,
                cancellationToken);
            if (!threadChanged.IsSuccess)
            {
                return Result<MessageResponse>.Failure(threadChanged.Error!);
            }
        }
        var committedMessage = message;
        if (request.ClientRequestId.HasValue)
        {
            var commitResult = await messageIdempotency.CommitAsync(message, cancellationToken);
            committedMessage = commitResult.Message;
            if (committedMessage.ThreadRootMessageId != threadRootMessageId)
            {
                return Result<MessageResponse>.Failure("Client request identity is already used for another message target.");
            }
        }
        else
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        return Result<MessageResponse>.Success(ToMessage(committedMessage));
    }

    private async Task<Attachment?> ResolveMessageAttachmentSourceAsync(
        Conversation conversation,
        Guid attachmentId,
        CancellationToken cancellationToken)
    {
        if (attachmentId == Guid.Empty ||
            !currentTenant.IsAvailable ||
            currentTenant.IsPlatformScope ||
            currentTenant.TenantId != conversation.TenantId)
        {
            return null;
        }

        // Reuse the canonical Files open boundary first. This revalidates the
        // current actor, tenant, owner scope, scan/classification state and
        // current file authorization without trusting any browser metadata.
        var authorized = await fileService.GetAsync(attachmentId, cancellationToken);
        if (!authorized.IsSuccess)
        {
            return null;
        }

        var source = await files.GetAttachmentAsync(attachmentId, cancellationToken);
        if (source is null ||
            source.TenantId != conversation.TenantId ||
            source.WorkspaceId != conversation.WorkspaceId ||
            source.DeletedAt.HasValue ||
            !source.OwnerType.HasValue ||
            !source.OwnerId.HasValue ||
            source.FileObject is null ||
            source.FileObjectId == Guid.Empty)
        {
            return null;
        }

        var fileObject = source.FileObject;
        // Message search currently treats only workspace-scoped FileObjects as
        // canonical Message attachments. Keep send-side promotion inside that
        // same fail-closed contract until project-scoped search is specified.
        if (fileObject.TenantId != conversation.TenantId ||
            fileObject.WorkspaceId != conversation.WorkspaceId ||
            fileObject.ProjectId.HasValue ||
            fileObject.DeletedAt.HasValue ||
            fileObject.Status != FileObjectStatus.Active ||
            source.ScanStatus != FileScanStatus.Clean ||
            !fileObject.Classification.HasValue ||
            fileObject.Classification == DataClassification.UnknownSensitive ||
            string.IsNullOrWhiteSpace(fileObject.OriginalFileName) ||
            string.IsNullOrWhiteSpace(fileObject.ContentType) ||
            string.IsNullOrWhiteSpace(fileObject.StorageKey) ||
            string.IsNullOrWhiteSpace(source.StorageProvider))
        {
            return null;
        }

        var owner = await files.ResolveOwnerAsync(source.OwnerType.Value, source.OwnerId.Value, cancellationToken);
        if (owner is null ||
            owner.WorkspaceId != conversation.WorkspaceId ||
            (owner.ConversationId.HasValue && owner.ConversationId.Value != conversation.Id) ||
            (owner.ProjectId.HasValue && owner.ProjectId != conversation.ProjectId) ||
            (fileObject.ProjectId.HasValue && fileObject.ProjectId != conversation.ProjectId))
        {
            return null;
        }

        var extension = Path.GetExtension(fileObject.OriginalFileName).ToLowerInvariant();
        if (fileObject.SizeBytes <= 0 ||
            fileObject.SizeBytes > MaxAttachmentBytes ||
            !AllowedExtensions.Contains(extension))
        {
            return null;
        }

        return source;
    }

    public async Task<Result<MessageThreadResponse>> GetMessageThreadAsync(
        Guid messageId,
        Guid? anchorReplyMessageId = null,
        CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId))
        {
            return Result<MessageThreadResponse>.Failure("Message thread not found.");
        }

        var rootMessage = await messaging.GetMessageAsync(messageId, cancellationToken);
        if (rootMessage is null || rootMessage.ThreadRootMessageId.HasValue)
        {
            return await DenyAsync<MessageThreadResponse>(
                userId,
                "MessageThreadAccessDenied",
                "Message",
                messageId,
                "Message thread not found.",
                cancellationToken,
                "thread_root_invalid");
        }

        var conversation = await messaging.GetConversationAsync(rootMessage.ConversationId, cancellationToken);
        if (conversation is null ||
            conversation.Type == ConversationType.Thread ||
            !await authorization.CanViewConversation(userId, rootMessage.ConversationId, cancellationToken))
        {
            return await DenyAsync<MessageThreadResponse>(
                userId,
                "MessageThreadAccessDenied",
                "Message",
                messageId,
                "Message thread not found.",
                cancellationToken,
                "conversation_read_denied");
        }

        if (anchorReplyMessageId.HasValue)
        {
            var anchorReply = await messaging.GetMessageAsync(anchorReplyMessageId.Value, cancellationToken);
            if (anchorReply is null ||
                anchorReply.DeletedAt.HasValue ||
                anchorReply.ConversationId != rootMessage.ConversationId ||
                anchorReply.ThreadRootMessageId != rootMessage.Id)
            {
                return await DenyAsync<MessageThreadResponse>(
                    userId,
                    "MessageThreadAccessDenied",
                    "Message",
                    messageId,
                    "Message thread not found.",
                    cancellationToken,
                    "thread_reply_anchor_invalid");
            }
        }

        var replyPage = anchorReplyMessageId.HasValue
            ? await messaging.ListThreadReplyContextAsync(
                rootMessage.ConversationId,
                rootMessage.Id,
                anchorReplyMessageId.Value,
                MaximumThreadReplies,
                cancellationToken)
            : await messaging.ListThreadRepliesAsync(
                rootMessage.ConversationId,
                rootMessage.Id,
                MaximumThreadReplies,
                before: null,
                cancellationToken: cancellationToken);
        if (anchorReplyMessageId.HasValue &&
            !replyPage.Items.Any(message =>
                message.Id == anchorReplyMessageId.Value &&
                !message.DeletedAt.HasValue))
        {
            return await DenyAsync<MessageThreadResponse>(
                userId,
                "MessageThreadAccessDenied",
                "Message",
                messageId,
                "Message thread not found.",
                cancellationToken,
                "thread_reply_anchor_invalid");
        }
        var summary = await messaging.GetThreadSummaryAsync(
            rootMessage.ConversationId,
            rootMessage.Id,
            MaximumThreadParticipantNames,
            cancellationToken: cancellationToken);
        var replies = replyPage.Items
            .OrderBy(message => message.CreatedAt)
            .ThenBy(message => message.Id)
            .Select(message => ToMessage(message))
            .ToList();
        return Result<MessageThreadResponse>.Success(new MessageThreadResponse(
            ToMessage(rootMessage, summary),
            replies,
            summary,
            replyPage.TotalCount > replies.Count,
            MaximumThreadReplies));
    }

    public async Task<Result<ThreadMessageCreatedResponse>> SendThreadMessageAsync(
        Guid messageId,
        SendThreadMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId))
        {
            return Result<ThreadMessageCreatedResponse>.Failure("Message thread not found.");
        }

        var rootMessage = await messaging.GetMessageAsync(messageId, cancellationToken);
        if (rootMessage is null || rootMessage.ThreadRootMessageId.HasValue || rootMessage.DeletedAt.HasValue)
        {
            return await DenyAsync<ThreadMessageCreatedResponse>(
                userId,
                "MessageThreadReplyDenied",
                "Message",
                messageId,
                "Message thread not found.",
                cancellationToken,
                rootMessage?.DeletedAt.HasValue == true ? "thread_root_deleted" : "thread_root_invalid");
        }

        var conversation = await messaging.GetConversationAsync(rootMessage.ConversationId, cancellationToken);
        if (conversation is null ||
            conversation.Type == ConversationType.Thread ||
            !await authorization.CanSendMessage(userId, rootMessage.ConversationId, cancellationToken))
        {
            return await DenyAsync<ThreadMessageCreatedResponse>(
                userId,
                "MessageThreadReplyDenied",
                "Message",
                messageId,
                "Message thread not found.",
                cancellationToken,
                "conversation_post_denied");
        }

        var existingSummary = await messaging.GetThreadSummaryAsync(
            rootMessage.ConversationId,
            rootMessage.Id,
            MaximumThreadParticipantNames,
            cancellationToken: cancellationToken);
        if (existingSummary.ReplyCount == 0 &&
            !await authorization.CanCreateThread(userId, rootMessage.ConversationId, cancellationToken))
        {
            return await DenyAsync<ThreadMessageCreatedResponse>(
                userId,
                "MessageThreadReplyDenied",
                "Message",
                messageId,
                "Message thread not found.",
                cancellationToken,
                "thread_create_denied");
        }

        var sendResult = await SendMessageCoreAsync(
            rootMessage.ConversationId,
            new SendMessageRequest(
                request.Body,
                Attachments: null,
                ClientRequestId: request.ClientRequestId,
                MentionedUserIds: request.MentionedUserIds),
            rootMessage.Id,
            cancellationToken);
        if (!sendResult.IsSuccess)
        {
            return Result<ThreadMessageCreatedResponse>.Failure(sendResult.Error!);
        }

        var summary = await messaging.GetThreadSummaryAsync(
            rootMessage.ConversationId,
            rootMessage.Id,
            MaximumThreadParticipantNames,
            cancellationToken: cancellationToken);
        return Result<ThreadMessageCreatedResponse>.Success(new ThreadMessageCreatedResponse(
            sendResult.Value!,
            summary));
    }

    public async Task<Result<MessageResponse>> UpdateMessageAsync(Guid messageId, UpdateMessageRequest request, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId)) return Result<MessageResponse>.Failure("You are not allowed to edit this message.");
        if (!await authorization.CanEditMessage(userId, messageId, cancellationToken))
        {
            return await DenyAsync<MessageResponse>(userId, "communication.message_edit_denied", "Message", messageId, "You are not allowed to edit this message.", cancellationToken, "author_required");
        }

        if (string.IsNullOrWhiteSpace(request.Body)) return Result<MessageResponse>.Failure("Message body is required.");
        var normalizedBody = request.Body.Trim();
        if (normalizedBody.Length > safetyOptions.MaxMessageLength) return Result<MessageResponse>.Failure("Message body is too large.");
        var message = await messaging.GetMessageAsync(messageId, cancellationToken);
        var conversation = await messaging.GetConversationAsync(message!.ConversationId, cancellationToken);
        if (conversation is null) return Result<MessageResponse>.Failure("Message not found.");
        message.Body = normalizedBody;
        message.EditedAt = clock.UtcNow;
        message.Version++;
        await LogCommunicationAuditAsync(userId, "communication.message_edited", "Message", message.Id, conversation, message.Id, conversation.Type == ConversationType.Thread ? conversation.Id : null, "allow", "author", cancellationToken);
        var updatedEvent = await EnqueueMessageUpdatedAsync(conversation, message, userId, cancellationToken);
        if (!updatedEvent.IsSuccess)
        {
            return Result<MessageResponse>.Failure(updatedEvent.Error!);
        }
        if (message.ThreadRootMessageId.HasValue)
        {
            var summary = await messaging.GetThreadSummaryAsync(
                message.ConversationId,
                message.ThreadRootMessageId.Value,
                MaximumThreadParticipantNames,
                cancellationToken: cancellationToken);
            var threadChanged = await EnqueueThreadChangedAsync(
                conversation,
                message.ThreadRootMessageId.Value,
                summary.ReplyCount,
                summary.LatestReplyAt,
                "replyUpdated",
                userId,
                cancellationToken);
            if (!threadChanged.IsSuccess)
            {
                return Result<MessageResponse>.Failure(threadChanged.Error!);
            }
        }
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result<MessageResponse>.Success(ToMessage(message));
    }

    public async Task<Result> DeleteMessageAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId)) return Result.Failure("You are not allowed to delete this message.");
        if (!await authorization.CanDeleteMessage(userId, messageId, cancellationToken))
        {
            return await DenyAsync(userId, "communication.message_delete_denied", "Message", messageId, "You are not allowed to delete this message.", cancellationToken, "moderation_permission_denied");
        }

        var message = await messaging.GetMessageAsync(messageId, cancellationToken);
        var conversation = await messaging.GetConversationAsync(message!.ConversationId, cancellationToken);
        if (conversation is null) return Result.Failure("Message not found.");
        var reasonCode = message.AuthorUserId == userId ? "author_delete" : "moderation_delete";
        message.MarkDeleted(clock.UtcNow, userId, reasonCode);
        message.Body = string.Empty;
        message.Version++;
        await LogCommunicationAuditAsync(userId, "communication.message_deleted", "Message", message.Id, conversation, message.Id, conversation.Type == ConversationType.Thread ? conversation.Id : null, "allow", reasonCode, cancellationToken);
        var deletedEvent = await EnqueueMessageDeletedAsync(conversation, message, userId, cancellationToken);
        if (!deletedEvent.IsSuccess)
        {
            return Result.Failure(deletedEvent.Error!);
        }
        if (message.ThreadRootMessageId.HasValue)
        {
            var summary = await messaging.GetThreadSummaryAsync(
                message.ConversationId,
                message.ThreadRootMessageId.Value,
                MaximumThreadParticipantNames,
                cancellationToken: cancellationToken);
            var threadChanged = await EnqueueThreadChangedAsync(
                conversation,
                message.ThreadRootMessageId.Value,
                summary.ReplyCount,
                summary.LatestReplyAt,
                "replyDeleted",
                userId,
                cancellationToken);
            if (!threadChanged.IsSuccess)
            {
                return Result.Failure(threadChanged.Error!);
            }
        }
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result> ReportMessageAsync(Guid messageId, MessageReportRequest request, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId)) return Result.Failure("You are not allowed to report this message.");
        var message = await messaging.GetMessageAsync(messageId, cancellationToken);
        if (message is null || message.DeletedAt.HasValue || !await authorization.CanViewConversation(userId, message.ConversationId, cancellationToken))
        {
            return await DenyAsync(userId, "communication.message_report_denied", "Message", messageId, "Message not found.", cancellationToken, "report_target_not_visible");
        }

        var conversation = await messaging.GetConversationAsync(message.ConversationId, cancellationToken);
        if (conversation is null) return Result.Failure("Message not found.");
        var safetyDecision = safetyGuard.CheckReport(new CommunicationSafetyScope(userId, conversation.TenantId, conversation.WorkspaceId, conversation.Id), clock.UtcNow);
        if (!safetyDecision.IsAllowed)
        {
            await LogCommunicationAuditAsync(userId, "communication.rate_limited", "Message", message.Id, conversation, message.Id, conversation.Type == ConversationType.Thread ? conversation.Id : null, "deny", safetyDecision.ReasonCode, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Failure("Report cannot be created right now.");
        }

        await LogCommunicationAuditAsync(userId, "communication.message_reported", "Message", message.Id, conversation, message.Id, conversation.Type == ConversationType.Thread ? conversation.Id : null, "allow", NormalizeReasonCode(request.ReasonCode, "reported"), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result> ReportConversationAsync(Guid conversationId, ConversationReportRequest request, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId)) return Result.Failure("You are not allowed to report this conversation.");
        if (!await authorization.CanViewConversation(userId, conversationId, cancellationToken))
        {
            return await DenyAsync(userId, "communication.message_report_denied", "Conversation", conversationId, "Conversation not found.", cancellationToken, "report_target_not_visible");
        }

        var conversation = await messaging.GetConversationAsync(conversationId, cancellationToken);
        if (conversation is null) return Result.Failure("Conversation not found.");
        var safetyDecision = safetyGuard.CheckReport(new CommunicationSafetyScope(userId, conversation.TenantId, conversation.WorkspaceId, conversation.Id), clock.UtcNow);
        if (!safetyDecision.IsAllowed)
        {
            await LogCommunicationAuditAsync(userId, "communication.rate_limited", "Conversation", conversation.Id, conversation, null, conversation.Type == ConversationType.Thread ? conversation.Id : null, "deny", safetyDecision.ReasonCode, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Failure("Report cannot be created right now.");
        }

        await LogCommunicationAuditAsync(userId, "communication.message_reported", "Conversation", conversation.Id, conversation, null, conversation.Type == ConversationType.Thread ? conversation.Id : null, "allow", NormalizeReasonCode(request.ReasonCode, "reported"), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result> MarkReadAsync(Guid conversationId, MarkConversationReadRequest request, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId)) return Result.Failure("Conversation not found.");
        if (!await authorization.CanViewConversation(userId, conversationId, cancellationToken))
        {
            return await DenyAsync(userId, "ConversationReadDenied", "Conversation", conversationId, "Conversation not found.", cancellationToken, "participant_missing");
        }

        if (!await ValidateReadableConversationMessageAsync(userId, conversationId, request.LastReadMessageId, cancellationToken))
        {
            return await DenyAsync(userId, "ConversationReadDenied", "Conversation", conversationId, "Message not found.", cancellationToken, "cursor_message_denied");
        }

        var member = await messaging.GetMemberAsync(conversationId, userId, cancellationToken);
        if (!IsActiveParticipant(member))
        {
            return await DenyAsync(userId, "ConversationReadDenied", "Conversation", conversationId, "Conversation not found.", cancellationToken, "participant_removed");
        }

        var state = await messaging.GetReadStateAsync(conversationId, userId, cancellationToken);
        if (state is null)
        {
            state = new ReadState { UserId = userId, ScopeType = ReadScopeType.Conversation, ScopeId = conversationId, ConversationId = conversationId, LastReadAt = clock.UtcNow };
            await messaging.AddReadStateAsync(state, cancellationToken);
        }
        var cursorMessage = request.LastReadMessageId.HasValue
            ? await messaging.GetMessageAsync(request.LastReadMessageId.Value, cancellationToken)
            : null;
        var proposedSequence = cursorMessage?.CreatedAt.UtcTicks ?? state.LastReadSequence;
        var advanced = proposedSequence > state.LastReadSequence;
        if (advanced)
        {
            state.LastReadMessageId = request.LastReadMessageId;
            state.LastReadItemId = request.LastReadMessageId;
            state.LastReadAt = clock.UtcNow;
            state.LastReadSequence = proposedSequence;
            state.StateVersion++;
            member!.LastReadMessageId = request.LastReadMessageId;
            member.LastReadAt = state.LastReadAt;
            member.UnreadCursorMessageId = null;
        }
        await AuditParticipantStateAsync(userId, "mark_read", conversationId, "allow", "self_state_only", cancellationToken);
        if (advanced)
        {
            var unread = await messaging.CountUnreadMessagesAsync(conversationId, userId, state.LastReadAt, cancellationToken);
            var unreadEvent = await EnqueueUnreadChangedAsync(conversationId, userId, state, unread, cancellationToken);
            if (!unreadEvent.IsSuccess)
            {
                return Result.Failure(unreadEvent.Error!);
            }
        }
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result<ParticipantStateResponse>> GetParticipantStateAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId)) return Result<ParticipantStateResponse>.Failure("Conversation not found.");
        if (!await authorization.CanViewConversation(userId, conversationId, cancellationToken))
        {
            return await DenyAsync<ParticipantStateResponse>(userId, "ParticipantStateReadDenied", "Conversation", conversationId, "Conversation not found.", cancellationToken, "self_state_only");
        }

        var member = await messaging.GetMemberAsync(conversationId, userId, cancellationToken);
        if (!IsActiveParticipant(member))
        {
            return await DenyAsync<ParticipantStateResponse>(userId, "ParticipantStateReadDenied", "Conversation", conversationId, "Conversation not found.", cancellationToken, "participant_removed");
        }

        return Result<ParticipantStateResponse>.Success(await ToParticipantStateAsync(member!, userId, cancellationToken));
    }

    public async Task<Result<ParticipantStateResponse>> UpdateParticipantStateAsync(Guid conversationId, UpdateParticipantStateRequest request, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId)) return Result<ParticipantStateResponse>.Failure("Conversation not found.");
        if (!await authorization.CanViewConversation(userId, conversationId, cancellationToken))
        {
            return await DenyAsync<ParticipantStateResponse>(userId, "ParticipantStateUpdateDenied", "Conversation", conversationId, "Conversation not found.", cancellationToken, "self_state_only");
        }

        var member = await messaging.GetMemberAsync(conversationId, userId, cancellationToken);
        if (!IsActiveParticipant(member))
        {
            return await DenyAsync<ParticipantStateResponse>(userId, "ParticipantStateUpdateDenied", "Conversation", conversationId, "Conversation not found.", cancellationToken, "participant_removed");
        }

        if (!await ValidateReadableConversationMessageAsync(userId, conversationId, request.LastReadMessageId, cancellationToken) ||
            !await ValidateReadableConversationMessageAsync(userId, conversationId, request.UnreadCursorMessageId, cancellationToken))
        {
            return await DenyAsync<ParticipantStateResponse>(userId, "ParticipantStateUpdateDenied", "Conversation", conversationId, "Message not found.", cancellationToken, "cursor_message_denied");
        }

        var now = clock.UtcNow;
        member!.LastOpenedAt = request.LastOpenedAt ?? now;
        if (request.LastReadMessageId.HasValue)
        {
            var state = await messaging.GetReadStateAsync(conversationId, userId, cancellationToken);
            if (state is null)
            {
                state = new ReadState { UserId = userId, ScopeType = ReadScopeType.Conversation, ScopeId = conversationId, ConversationId = conversationId, LastReadAt = now };
                await messaging.AddReadStateAsync(state, cancellationToken);
            }
            var cursorMessage = await messaging.GetMessageAsync(request.LastReadMessageId.Value, cancellationToken);
            var proposedSequence = cursorMessage?.CreatedAt.UtcTicks ?? state.LastReadSequence;
            if (proposedSequence > state.LastReadSequence)
            {
                member.LastReadMessageId = request.LastReadMessageId;
                member.LastReadAt = now;
                state.LastReadMessageId = request.LastReadMessageId;
                state.LastReadItemId = request.LastReadMessageId;
                state.LastReadAt = now;
                state.LastReadSequence = proposedSequence;
                state.StateVersion++;
                var unread = await messaging.CountUnreadMessagesAsync(conversationId, userId, state.LastReadAt, cancellationToken);
                var unreadEvent = await EnqueueUnreadChangedAsync(conversationId, userId, state, unread, cancellationToken);
                if (!unreadEvent.IsSuccess)
                {
                    return Result<ParticipantStateResponse>.Failure(unreadEvent.Error!);
                }
            }
        }

        if (request.UnreadCursorMessageId.HasValue)
        {
            member.UnreadCursorMessageId = request.UnreadCursorMessageId;
        }

        if (request.IsMuted.HasValue)
        {
            member.IsMuted = request.IsMuted.Value;
        }

        if (request.IsArchived.HasValue)
        {
            member.IsArchived = request.IsArchived.Value;
        }

        if (request.IsLater.HasValue)
        {
            member.IsLater = request.IsLater.Value;
        }

        await AuditParticipantStateAsync(userId, "update_participant_state", conversationId, "allow", "self_state_only", cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result<ParticipantStateResponse>.Success(await ToParticipantStateAsync(member, userId, cancellationToken));
    }

    private async Task<Result<ConversationDetailResponse>> SetConversationLockAsync(Guid conversationId, bool isLocked, string? reasonCode, CancellationToken cancellationToken)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId)) return Result<ConversationDetailResponse>.Failure("You are not allowed to manage this conversation.");
        if (!await authorization.CanModerateConversation(userId, conversationId, cancellationToken))
        {
            return await DenyAsync<ConversationDetailResponse>(userId, isLocked ? "communication.conversation_lock_denied" : "communication.conversation_unlock_denied", "Conversation", conversationId, "You are not allowed to manage this conversation.", cancellationToken, "moderation_permission_denied");
        }

        var conversation = await messaging.GetConversationAsync(conversationId, cancellationToken);
        if (conversation is null || !IsSupportedMvpType(conversation.Type))
        {
            return Result<ConversationDetailResponse>.Failure("Conversation not found.");
        }

        conversation.IsLocked = isLocked;
        var action = isLocked
            ? conversation.Type == ConversationType.Thread ? "communication.thread_locked" : "communication.conversation_locked"
            : conversation.Type == ConversationType.Thread ? "communication.thread_unlocked" : "communication.conversation_unlocked";
        await LogCommunicationAuditAsync(userId, action, "Conversation", conversation.Id, conversation, null, conversation.Type == ConversationType.Thread ? conversation.Id : null, "allow", NormalizeReasonCode(reasonCode, isLocked ? "locked" : "unlocked"), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result<ConversationDetailResponse>.Success(await ToDetailAsync(conversation, cancellationToken));
    }

    private static bool IsSupportedMvpType(ConversationType type) => type is ConversationType.DirectMessage or ConversationType.ProjectChannel or ConversationType.Thread;

    private async Task<bool> CanBindConversationToProjectAsync(
        Project project,
        Guid userId,
        CancellationToken cancellationToken)
    {
        // A Project-bound conversation is an operational surface.  The
        // generic Messaging create command must not substitute for explicit
        // Project activation or provision channels for a provenance-ambiguous
        // Planning/Suspended Project.
        return project.Status is ProjectStatus.Active or ProjectStatus.Review or ProjectStatus.Completed &&
               await projectAuthorization.CanViewProject(userId, project.Id, cancellationToken);
    }

    private async Task<Result<ConversationDetailResponse>> CreateThreadAsync(CreateConversationRequest request, Guid userId, CancellationToken cancellationToken)
    {
        if (!request.ParentConversationId.HasValue || request.ParentConversationId.Value == Guid.Empty)
        {
            return Result<ConversationDetailResponse>.Failure("Thread conversations require ParentConversationId.");
        }

        var parent = await messaging.GetConversationAsync(request.ParentConversationId.Value, cancellationToken);
        if (parent is null || parent is { Type: ConversationType.Thread, ParentConversationId: null })
        {
            return Result<ConversationDetailResponse>.Failure("Parent conversation not found.");
        }

        if (parent.IsArchived || parent.IsLocked)
        {
            return Result<ConversationDetailResponse>.Failure("Parent conversation cannot accept threads.");
        }

        if (!await authorization.CanCreateThread(userId, parent.Id, cancellationToken))
        {
            return await DenyAsync<ConversationDetailResponse>(userId, "ConversationThreadCreateDenied", "Conversation", parent.Id, "Parent conversation not found.", cancellationToken, "participant_thread_create_denied");
        }

        var safetyDecision = safetyGuard.CheckThreadCreate(new CommunicationSafetyScope(userId, parent.TenantId, parent.WorkspaceId, parent.Id), clock.UtcNow);
        if (!safetyDecision.IsAllowed)
        {
            await LogCommunicationAuditAsync(userId, "communication.rate_limited", "Conversation", parent.Id, parent, null, null, "deny", safetyDecision.ReasonCode, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Result<ConversationDetailResponse>.Failure("Thread cannot be created right now.");
        }

        if (request.WorkspaceId.HasValue && request.WorkspaceId.Value != parent.WorkspaceId)
        {
            return Result<ConversationDetailResponse>.Failure("Thread workspace scope must match parent conversation.");
        }

        if (request.ProjectId.HasValue && request.ProjectId != parent.ProjectId)
        {
            return Result<ConversationDetailResponse>.Failure("Thread project scope must match parent conversation.");
        }

        var parentMembers = (await messaging.ListMembersAsync(parent.Id, cancellationToken))
            .Where(IsActiveParticipant)
            .ToList();
        var requestedMembers = request.MemberUserIds.Distinct().ToList();
        if (requestedMembers.Any(memberId => parentMembers.All(parentMember => parentMember.UserId != memberId)))
        {
            return Result<ConversationDetailResponse>.Failure("Thread membership must stay within the parent conversation.");
        }

        var conversation = new Conversation
        {
            WorkspaceId = parent.WorkspaceId,
            ProjectId = parent.ProjectId,
            Type = ConversationType.Thread,
            Title = request.Title?.Trim(),
            ParentConversationId = parent.Id,
            RootConversationId = parent.RootConversationId ?? parent.Id,
            CreatedByUserId = userId
        };
        await messaging.AddConversationAsync(conversation, cancellationToken);
        foreach (var parentMember in parentMembers)
        {
            await messaging.AddMemberAsync(new ConversationMember
            {
                ConversationId = conversation.Id,
                UserId = parentMember.UserId,
                Role = parentMember.Role,
                CanRead = parentMember.CanRead,
                CanPost = parentMember.CanPost,
                CanManageMembers = false,
                CanCreateThread = parentMember.CanCreateThread,
                JoinedAt = clock.UtcNow
            }, cancellationToken);
        }

        await AuditAsync(userId, "ConversationThreadCreated", conversation.Id, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result<ConversationDetailResponse>.Success(await ToDetailAsync(conversation, cancellationToken));
    }

    public async Task<Result<ConversationDetailResponse>> LockAsync(Guid conversationId, ConversationLockRequest request, CancellationToken cancellationToken = default)
    {
        return await SetConversationLockAsync(conversationId, isLocked: true, request.ReasonCode, cancellationToken);
    }

    public async Task<Result<ConversationDetailResponse>> UnlockAsync(Guid conversationId, ConversationLockRequest request, CancellationToken cancellationToken = default)
    {
        return await SetConversationLockAsync(conversationId, isLocked: false, request.ReasonCode, cancellationToken);
    }

    public async Task<Result<ConversationDetailResponse>> ArchiveAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId)) return Result<ConversationDetailResponse>.Failure("You are not allowed to manage this conversation.");
        if (!await authorization.CanModerateConversation(userId, conversationId, cancellationToken))
        {
            return await DenyAsync<ConversationDetailResponse>(userId, "communication.conversation_archive_denied", "Conversation", conversationId, "You are not allowed to manage this conversation.", cancellationToken, "moderation_permission_denied");
        }

        var conversation = await messaging.GetConversationAsync(conversationId, cancellationToken);
        if (conversation is null || !IsSupportedMvpType(conversation.Type))
        {
            return Result<ConversationDetailResponse>.Failure("Conversation not found.");
        }

        conversation.IsArchived = true;
        await LogCommunicationAuditAsync(userId, "communication.conversation_archived", "Conversation", conversation.Id, conversation, messageId: null, threadId: conversation.Type == ConversationType.Thread ? conversation.Id : null, decision: "allow", reasonCode: "archived", cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result<ConversationDetailResponse>.Success(await ToDetailAsync(conversation, cancellationToken));
    }

    private Task AuditAsync(Guid actorUserId, string action, Guid targetId, CancellationToken cancellationToken) => auditLogger.LogAsync(new AuditLogEntry(actorUserId, action, "Message", targetId), cancellationToken);

    private Task LogCommunicationAuditAsync(
        Guid actorUserId,
        string action,
        string entityType,
        Guid entityId,
        Conversation conversation,
        Guid? messageId,
        Guid? threadId,
        string decision,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        return auditLogger.LogAsync(new AuditLogEntry(
            actorUserId,
            action,
            entityType,
            entityId,
            "Communication safety event.",
            WorkspaceId: conversation.WorkspaceId,
            ProjectId: conversation.ProjectId,
            Metadata: new Dictionary<string, object?>
            {
                ["actorUserId"] = actorUserId,
                ["tenantId"] = conversation.TenantId,
                ["workspaceId"] = conversation.WorkspaceId,
                ["conversationId"] = conversation.Id,
                ["threadId"] = threadId,
                ["messageId"] = messageId,
                ["targetOperation"] = action,
                ["decision"] = decision,
                ["reasonCode"] = reasonCode,
                ["conversationType"] = conversation.Type.ToString(),
                ["timestamp"] = clock.UtcNow
            }),
            cancellationToken);
    }

    private Task AuditParticipantStateAsync(Guid actorUserId, string operation, Guid conversationId, string decision, string reasonCode, CancellationToken cancellationToken)
    {
        return auditLogger.LogAsync(new AuditLogEntry(
            actorUserId,
            operation == "mark_read" ? "ConversationMarkedRead" : "ParticipantStateUpdated",
            "Conversation",
            conversationId,
            "Conversation participant state updated.",
            Metadata: new Dictionary<string, object?>
            {
                ["operation"] = operation,
                ["decision"] = decision,
                ["reasonCode"] = reasonCode
            }),
            cancellationToken);
    }

    private async Task<Result<T>> DenyAsync<T>(Guid actorUserId, string action, string entityType, Guid entityId, string error, CancellationToken cancellationToken, string? reasonCode = null)
    {
        await auditLogger.LogAsync(new AuditLogEntry(
            actorUserId,
            action,
            entityType,
            entityId,
            "Conversation access denied.",
            Metadata: await BuildDenialMetadataAsync(actorUserId, action, entityType, entityId, reasonCode, cancellationToken)),
            cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result<T>.Failure(error);
    }
    private async Task<Result> DenyAsync(Guid actorUserId, string action, string entityType, Guid entityId, string error, CancellationToken cancellationToken, string? reasonCode = null)
    {
        await auditLogger.LogAsync(new AuditLogEntry(
            actorUserId,
            action,
            entityType,
            entityId,
            "Conversation access denied.",
            Metadata: await BuildDenialMetadataAsync(actorUserId, action, entityType, entityId, reasonCode, cancellationToken)),
            cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Failure(error);
    }

    private async Task<ConversationDetailResponse> ToDetailAsync(Conversation conversation, CancellationToken cancellationToken, Guid? viewerUserId = null)
    {
        var members = await messaging.ListMembersAsync(conversation.Id, cancellationToken);
        return new ConversationDetailResponse(
            conversation.Id,
            conversation.WorkspaceId,
            conversation.ProjectId,
            conversation.Type,
            ConversationTitleFor(conversation, members, viewerUserId),
            conversation.ParentConversationId,
            conversation.RootConversationId,
            conversation.IsArchived,
            conversation.IsLocked,
            members.Select(ToMember).ToList(),
            conversation.CreatedAt,
            conversation.UpdatedAt);
    }

    private static string? ConversationTitleFor(Conversation conversation, IReadOnlyList<ConversationMember> members, Guid? viewerUserId)
    {
        if (conversation.Type != ConversationType.DirectMessage)
        {
            return conversation.Title;
        }

        if (!string.IsNullOrWhiteSpace(conversation.Title))
        {
            return conversation.Title;
        }

        return members
            .Where(member => member.UserId != viewerUserId && IsActiveParticipant(member))
            .Select(member => member.User?.DisplayName)
            .FirstOrDefault(displayName => !string.IsNullOrWhiteSpace(displayName))
            ?? "Direct message";
    }

    private async Task<ParticipantStateResponse> ToParticipantStateAsync(ConversationMember member, Guid actorUserId, CancellationToken cancellationToken)
    {
        var unread = await messaging.CountUnreadMessagesAsync(member.ConversationId, actorUserId, member.LastReadAt, cancellationToken);
        return new ParticipantStateResponse(
            member.Id,
            member.UserId,
            member.ConversationId,
            member.LastOpenedAt,
            member.LastReadMessageId,
            member.LastReadAt,
            member.UnreadCursorMessageId,
            unread,
            member.IsMuted,
            member.IsArchived,
            member.IsLater,
            member.CreatedAt,
            member.UpdatedAt);
    }

    private async Task<bool> ValidateReadableConversationMessageAsync(Guid actorUserId, Guid conversationId, Guid? messageId, CancellationToken cancellationToken)
    {
        if (!messageId.HasValue)
        {
            return true;
        }

        var message = await messaging.GetMessageAsync(messageId.Value, cancellationToken);
        return message is not null &&
            message.ConversationId == conversationId &&
            !message.DeletedAt.HasValue &&
            await authorization.CanViewConversation(actorUserId, message.ConversationId, cancellationToken);
    }

    private async Task<IReadOnlyDictionary<string, object?>> BuildDenialMetadataAsync(Guid actorUserId, string action, string entityType, Guid entityId, string? reasonCode, CancellationToken cancellationToken)
    {
        Conversation? conversation = null;
        Guid? messageId = null;
        if (entityType == "Conversation")
        {
            conversation = await messaging.GetConversationAsync(entityId, cancellationToken);
        }
        else if (entityType == "Message")
        {
            var message = await messaging.GetMessageAsync(entityId, cancellationToken);
            if (message is not null)
            {
                messageId = message.Id;
                conversation = await messaging.GetConversationAsync(message.ConversationId, cancellationToken);
            }
        }

        return new Dictionary<string, object?>
        {
            ["actorUserId"] = actorUserId,
            ["tenantId"] = conversation?.TenantId,
            ["workspaceId"] = conversation?.WorkspaceId,
            ["conversationId"] = conversation?.Id,
            ["threadId"] = conversation?.Type == ConversationType.Thread ? conversation.Id : null,
            ["messageId"] = messageId,
            ["targetOperation"] = action,
            ["decision"] = "deny",
            ["reasonCode"] = reasonCode ?? DefaultReasonCode(action),
            ["participantState"] = conversation is not null
                ? await GetParticipantStateAsync(conversation.Id, actorUserId, cancellationToken)
                : "unknown",
            ["conversationType"] = conversation?.Type.ToString(),
            ["timestamp"] = clock.UtcNow
        };
    }

    private async Task<string> GetParticipantStateAsync(Guid conversationId, Guid actorUserId, CancellationToken cancellationToken)
    {
        var member = await messaging.GetMemberAsync(conversationId, actorUserId, cancellationToken);
        if (member is null)
        {
            return "missing";
        }

        return IsActiveParticipant(member) ? "active" : "removed";
    }

    private static string DefaultReasonCode(string action)
    {
        return action switch
        {
            "ConversationAccessDenied" or "ConversationReadDenied" => "participant_read_denied",
            "ParticipantStateReadDenied" => "self_state_only",
            "ParticipantStateUpdateDenied" => "state_update_denied",
            "MessageSendDenied" or "communication.message_post_denied" => "participant_post_denied",
            "communication.message_edit_denied" => "author_required",
            "communication.message_delete_denied" => "moderation_permission_denied",
            "communication.message_report_denied" => "report_target_not_visible",
            "communication.conversation_lock_denied" => "moderation_permission_denied",
            "ConversationManageDenied" => "participant_manage_members_denied",
            "ConversationThreadCreateDenied" => "participant_thread_create_denied",
            _ => "participant_missing"
        };
    }

    private static string NormalizeReasonCode(string? reasonCode, string fallback)
    {
        if (string.IsNullOrWhiteSpace(reasonCode))
        {
            return fallback;
        }

        var trimmed = reasonCode.Trim();
        return trimmed.Length > 80 ? trimmed[..80] : trimmed;
    }

    private static bool IsActiveParticipant(ConversationMember? member)
    {
        return member is { LeftAt: null, RemovedAt: null, CanRead: true };
    }

    private static ConversationMemberResponse ToMember(ConversationMember member) => new(
        member.UserId,
        member.User?.DisplayName ?? string.Empty,
        member.User?.Email ?? string.Empty,
        member.Role,
        member.CanRead,
        member.CanPost,
        member.CanManageMembers,
        member.CanCreateThread,
        member.JoinedAt,
        member.LeftAt,
        member.RemovedAt);
    private static MessageResponse ToMessage(
        Message message,
        MessageThreadSummaryResponse? threadSummary = null) => new(
        message.Id,
        message.WorkspaceId,
        message.ConversationId,
        message.AuthorUserId,
        message.AuthorUser?.DisplayName ?? string.Empty,
        message.DeletedAt.HasValue ? string.Empty : message.Body,
        message.DeletedAt.HasValue
            ? []
            : message.Attachments.Select(link => new AttachmentResponse(
                link.AttachmentId,
                link.Attachment?.FileName ?? string.Empty,
                link.Attachment?.ContentType ?? string.Empty,
                link.Attachment?.SizeBytes ?? 0)).ToList(),
        message.CreatedAt,
        message.UpdatedAt,
        message.EditedAt,
        message.DeletedAt.HasValue,
        message.ClientRequestId,
        message.Version,
        message.ThreadRootMessageId,
        threadSummary);

    private Task<Result<Guid>> EnqueueMessageCreatedAsync(Conversation conversation, Message message, Guid actorUserId, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            message = new
            {
                id = message.Id,
                conversationId = message.ConversationId,
                sender = new { userId = message.AuthorUserId, displayName = message.AuthorUser?.DisplayName ?? string.Empty, status = "active" },
                body = message.Body,
                createdAt = message.CreatedAt,
                updatedAt = message.UpdatedAt,
                version = message.Version,
                clientRequestId = message.ClientRequestId,
                threadRootMessageId = message.ThreadRootMessageId,
                attachmentSummaries = Array.Empty<object>()
            }
        });
        return EnqueueMessagingEventAsync("Messaging.MessageCreated.v1", "Message", message.Id, message.Version, actorUserId, message.ClientRequestId?.ToString(), payload, [new RealtimeRoutingTarget(RealtimeSubscriptionType.Conversation, conversation.Id)], cancellationToken);
    }

    private Task<Result<Guid>> EnqueueMessageUpdatedAsync(Conversation conversation, Message message, Guid actorUserId, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToElement(new { conversationId = conversation.Id, messageId = message.Id, messageVersion = message.Version, threadRootMessageId = message.ThreadRootMessageId, updatedAt = message.EditedAt, body = message.Body, attachmentSummaries = Array.Empty<object>(), requiresRefetch = false });
        return EnqueueMessagingEventAsync("Messaging.MessageUpdated.v1", "Message", message.Id, message.Version, actorUserId, null, payload, [new RealtimeRoutingTarget(RealtimeSubscriptionType.Conversation, conversation.Id)], cancellationToken);
    }

    private Task<Result<Guid>> EnqueueMessageDeletedAsync(Conversation conversation, Message message, Guid actorUserId, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToElement(new { conversationId = conversation.Id, messageId = message.Id, messageVersion = message.Version, threadRootMessageId = message.ThreadRootMessageId, deletedAt = message.DeletedAt, deletionMode = "tombstone", displayText = (string?)null });
        return EnqueueMessagingEventAsync("Messaging.MessageDeleted.v1", "Message", message.Id, message.Version, actorUserId, null, payload, [new RealtimeRoutingTarget(RealtimeSubscriptionType.Conversation, conversation.Id)], cancellationToken);
    }

    private Task<Result<Guid>> EnqueueThreadChangedAsync(
        Conversation conversation,
        Guid threadRootMessageId,
        int replyCount,
        DateTimeOffset? latestReplyAt,
        string change,
        Guid actorUserId,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            conversationId = conversation.Id,
            threadRootMessageId,
            latestReplyAt,
            replyCount,
            change,
            // Participant display names are deliberately not placed in this
            // metadata-only event. Consumers must refresh the authorized HTTP
            // projection so a new participant cannot leave summary names stale.
            requiresRefetch = true
        });
        return EnqueueMessagingEventAsync(
            "Messaging.ThreadChanged.v1",
            "MessageThread",
            threadRootMessageId,
            aggregateVersion: null,
            actorUserId,
            null,
            payload,
            [new RealtimeRoutingTarget(RealtimeSubscriptionType.Conversation, conversation.Id)],
            cancellationToken);
    }

    private Task<Result<Guid>> EnqueueUnreadChangedAsync(Guid conversationId, Guid userId, ReadState state, int unreadCount, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToElement(new { conversationId, userId, lastReadSequence = state.LastReadSequence, unreadCount, stateVersion = state.StateVersion, updatedAt = state.LastReadAt });
        return EnqueueMessagingEventAsync("Messaging.ConversationUnreadChanged.v1", "ConversationReadState", state.Id, state.StateVersion, userId, null, payload, [new RealtimeRoutingTarget(RealtimeSubscriptionType.User, userId)], cancellationToken);
    }

    private Task<Result<Guid>> EnqueueMessagingEventAsync(string eventType, string aggregateType, Guid aggregateId, long? aggregateVersion, Guid actorUserId, string? causationId, JsonElement payload, IReadOnlyCollection<RealtimeRoutingTarget> routingTargets, CancellationToken cancellationToken)
    {
        var tenantId = ConversationTenantId();
        if (tenantId == Guid.Empty)
        {
            return Task.FromResult(Result<Guid>.Failure("A matching active tenant context is required."));
        }
        return outbox.EnqueueAsync(new DurableEventEnvelope(Guid.NewGuid(), eventType, RealtimeEventCatalog.PayloadSchemaVersion1, clock.UtcNow, tenantId, aggregateType, aggregateId, aggregateVersion, new RealtimeActor("User", actorUserId), null, causationId, payload), routingTargets, cancellationToken);
    }

    private Guid ConversationTenantId() =>
        currentTenant is { IsAvailable: true, IsPlatformScope: false }
            ? currentTenant.TenantId
            : Guid.Empty;
}