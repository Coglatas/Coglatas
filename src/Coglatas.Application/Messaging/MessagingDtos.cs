using Coglatas.Domain.Enums;
using System.Text.Json.Serialization;

namespace Coglatas.Application.Messaging;

public sealed record ConversationListItemResponse(
    Guid Id,
    Guid WorkspaceId,
    Guid? ProjectId,
    ConversationType Type,
    string? Title,
    Guid? ParentConversationId,
    Guid? RootConversationId,
    MessageResponse? LastMessage,
    int UnreadCount,
    bool HasMention,
    bool IsMuted,
    bool IsArchived,
    bool IsLater,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConversationInboxView
{
    All = 0,
    Unread = 1,
    Mentions = 2,
    Later = 3
}

public sealed record ConversationInboxCountsResponse(
    int All,
    int Unread,
    int Mentions,
    int Later);

public sealed record ConversationInboxResponse(
    IReadOnlyList<ConversationListItemResponse> Items,
    int Page,
    int PageSize,
    int TotalCount,
    ConversationInboxView View,
    ConversationInboxCountsResponse Counts);

public sealed record ConversationDetailResponse(
    Guid Id,
    Guid WorkspaceId,
    Guid? ProjectId,
    ConversationType Type,
    string? Title,
    Guid? ParentConversationId,
    Guid? RootConversationId,
    bool IsArchived,
    bool IsLocked,
    IReadOnlyList<ConversationMemberResponse> Members,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

public sealed record CreateConversationRequest(
    ConversationType Type,
    string? Title,
    IReadOnlyList<Guid> MemberUserIds,
    Guid? WorkspaceId = null,
    Guid? ProjectId = null,
    Guid? ParentConversationId = null);

public sealed record CreateDirectConversationRequest(Guid RecipientUserId);

public sealed record ConversationRecipientResponse(Guid UserId, string DisplayName);

public sealed record ConversationListQuery(
    int Page = 1,
    int PageSize = 20,
    ConversationInboxView View = ConversationInboxView.All)
{
    public int SafePage => Page < 1 ? 1 : Page;

    public int SafePageSize => Math.Clamp(PageSize, 1, 100);
}

public sealed record UpdateConversationRequest(string? Title);

public sealed record ConversationLockRequest(string? ReasonCode = null);

public sealed record ConversationReportRequest(string ReasonCode, string? Reason = null);

public sealed record ConversationMemberResponse(
    Guid UserId,
    string DisplayName,
    string Email,
    ConversationMemberRole Role,
    bool CanRead,
    bool CanPost,
    bool CanManageMembers,
    bool CanCreateThread,
    DateTimeOffset JoinedAt,
    DateTimeOffset? LeftAt,
    DateTimeOffset? RemovedAt);

public sealed record AddConversationMemberRequest(Guid UserId);

/// <summary>Canonical server-owned Attachment identity accepted by Message send.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MessageAttachmentReferenceRequest(Guid AttachmentId);

public sealed record AttachmentResponse(Guid Id, string FileName, string ContentType, long FileSize);

public sealed record MessageResponse(
    Guid Id,
    Guid WorkspaceId,
    Guid ConversationId,
    Guid AuthorUserId,
    string AuthorDisplayName,
    string Body,
    IReadOnlyList<AttachmentResponse> Attachments,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? EditedAt,
    bool IsDeleted,
    Guid? ClientRequestId = null,
    long Version = 1,
    Guid? ThreadRootMessageId = null,
    MessageThreadSummaryResponse? Thread = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SendMessageRequest(
    string? Body,
    IReadOnlyList<MessageAttachmentReferenceRequest>? Attachments = null,
    Guid? ClientRequestId = null,
    IReadOnlyList<Guid>? MentionedUserIds = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SendThreadMessageRequest(
    string? Body,
    Guid? ClientRequestId = null,
    IReadOnlyList<Guid>? MentionedUserIds = null);

public sealed record MessageThreadSummaryResponse(
    Guid ThreadRootMessageId,
    int ReplyCount,
    DateTimeOffset? LatestReplyAt,
    IReadOnlyList<string> ParticipantDisplayNames);

public sealed record MessageThreadResponse(
    MessageResponse RootMessage,
    IReadOnlyList<MessageResponse> Replies,
    MessageThreadSummaryResponse Summary,
    bool HasMore,
    int MaximumReplies);

public sealed record ThreadMessageCreatedResponse(
    MessageResponse Message,
    MessageThreadSummaryResponse Summary);

public sealed record UpdateMessageRequest(string Body);

public sealed record MessageReportRequest(string ReasonCode, string? Reason = null);

public sealed record MarkConversationReadRequest(Guid? LastReadMessageId);

public sealed record ParticipantStateResponse(
    Guid ParticipantId,
    Guid UserId,
    Guid ConversationId,
    DateTimeOffset? LastOpenedAt,
    Guid? LastReadMessageId,
    DateTimeOffset? LastReadAt,
    Guid? UnreadCursorMessageId,
    int UnreadCount,
    bool IsMuted,
    bool IsArchived,
    bool IsLater,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

public sealed record UpdateParticipantStateRequest(
    DateTimeOffset? LastOpenedAt = null,
    Guid? LastReadMessageId = null,
    Guid? UnreadCursorMessageId = null,
    bool? IsMuted = null,
    bool? IsArchived = null,
    bool? IsLater = null);

public sealed record MessageListQuery(int Limit = 50, DateTimeOffset? Before = null, Guid? AnchorMessageId = null)
{
    public int SafeLimit => Math.Clamp(Limit, 1, 100);
}

public sealed record MessageFollowUpListQuery(int Page = 1, int PageSize = 20)
{
    public int SafePage => Page < 1 ? 1 : Page;
    public int SafePageSize => Math.Clamp(PageSize, 1, 50);
}

public sealed record MessageFollowUpListItemResponse(
    Guid MessageId,
    Guid ConversationId,
    Guid WorkspaceId,
    ConversationType ConversationType,
    string ConversationTitle,
    Guid? ThreadRootMessageId,
    string AuthorDisplayName,
    string Body,
    DateTimeOffset MessageCreatedAt,
    DateTimeOffset SavedAt);

public sealed record MessageFollowUpStateResponse(
    Guid MessageId,
    bool IsSaved,
    DateTimeOffset? SavedAt);