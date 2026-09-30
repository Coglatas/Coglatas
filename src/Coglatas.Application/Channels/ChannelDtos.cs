using Coglatas.Domain.Enums;

namespace Coglatas.Application.Channels;

public sealed record CreateChannelRequest(string Name, string? Description, ChannelType ChannelType);

public sealed record UpdateChannelRequest(string? Name, string? Description, ChannelType? ChannelType, ChannelStatus? Status);

public sealed record ChannelResponse(
    Guid Id,
    Guid GroupId,
    string Name,
    string? Description,
    ChannelType ChannelType,
    ChannelStatus Status,
    Guid CreatedByUserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    IReadOnlyList<PostResponse> PinnedPosts);

public sealed record ChannelListItemResponse(
    Guid Id,
    Guid GroupId,
    string Name,
    string? Description,
    ChannelType ChannelType,
    ChannelStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

public sealed record AddChannelMemberRequest(Guid UserId, ChannelRole Role);

public sealed record ChannelMemberResponse(
    Guid UserId,
    string DisplayName,
    string Email,
    ChannelRole Role,
    DateTimeOffset JoinedAt);

public sealed record CreatePostRequest(string Body);

public sealed record UpdatePostRequest(string Body);

public sealed record PostResponse(
    Guid Id,
    Guid ChannelId,
    Guid AuthorUserId,
    string AuthorDisplayName,
    string Body,
    bool IsPinned,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? EditedAt);

public sealed record CreateThreadReplyRequest(string Body);

public sealed record ThreadReplyResponse(
    Guid Id,
    Guid PostId,
    Guid AuthorUserId,
    string AuthorDisplayName,
    string Body,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

public sealed record PostListQuery(int Page = 1, int PageSize = 50, DateTimeOffset? Before = null, DateTimeOffset? After = null)
{
    public int SafePage => Page < 1 ? 1 : Page;

    public int SafePageSize => Math.Clamp(PageSize, 1, 100);
}

public sealed record ThreadListQuery(int Page = 1, int PageSize = 50, DateTimeOffset? Before = null, DateTimeOffset? After = null)
{
    public int SafePage => Page < 1 ? 1 : Page;

    public int SafePageSize => Math.Clamp(PageSize, 1, 100);
}
