using Coglatas.Application.Channels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Coglatas.Web.Controllers;

[ApiController]
[Authorize]
public sealed class ChannelsController(IChannelService channels) : ApiResultControllerBase
{
    [HttpGet("api/groups/{groupId:guid}/channels")]
    public async Task<IActionResult> List(Guid groupId, CancellationToken cancellationToken)
    {
        return ToActionResult(await channels.ListByGroupAsync(groupId, cancellationToken));
    }

    [HttpPost("api/groups/{groupId:guid}/channels")]
    public async Task<IActionResult> Create(Guid groupId, CreateChannelRequest request, CancellationToken cancellationToken)
    {
        return ToActionResult(await channels.CreateAsync(groupId, request, cancellationToken));
    }

    [HttpGet("api/channels/{channelId:guid}")]
    public async Task<IActionResult> Get(Guid channelId, CancellationToken cancellationToken)
    {
        return ToActionResult(await channels.GetAsync(channelId, cancellationToken));
    }

    [HttpPatch("api/channels/{channelId:guid}")]
    public async Task<IActionResult> Update(Guid channelId, UpdateChannelRequest request, CancellationToken cancellationToken)
    {
        return ToActionResult(await channels.UpdateAsync(channelId, request, cancellationToken));
    }

    [HttpDelete("api/channels/{channelId:guid}")]
    public async Task<IActionResult> Delete(Guid channelId, CancellationToken cancellationToken)
    {
        var result = await channels.ArchiveAsync(channelId, cancellationToken);
        return result.IsSuccess ? Ok(new { status = "OK" }) : BadRequest(new { error = result.Error });
    }

    [HttpGet("api/channels/{channelId:guid}/members")]
    public async Task<IActionResult> ListMembers(Guid channelId, CancellationToken cancellationToken)
    {
        return ToActionResult(await channels.ListMembersAsync(channelId, cancellationToken));
    }

    [HttpPost("api/channels/{channelId:guid}/members")]
    public async Task<IActionResult> AddMember(Guid channelId, AddChannelMemberRequest request, CancellationToken cancellationToken)
    {
        return ToActionResult(await channels.AddMemberAsync(channelId, request, cancellationToken));
    }

    [HttpDelete("api/channels/{channelId:guid}/members/{userId:guid}")]
    public async Task<IActionResult> RemoveMember(Guid channelId, Guid userId, CancellationToken cancellationToken)
    {
        var result = await channels.RemoveMemberAsync(channelId, userId, cancellationToken);
        return result.IsSuccess ? Ok(new { status = "OK" }) : BadRequest(new { error = result.Error });
    }

    [HttpGet("api/channels/{channelId:guid}/posts")]
    public async Task<IActionResult> ListPosts(Guid channelId, [FromQuery] PostListQuery query, CancellationToken cancellationToken)
    {
        return ToActionResult(await channels.ListPostsAsync(channelId, query, cancellationToken));
    }

    [HttpPost("api/channels/{channelId:guid}/posts")]
    public async Task<IActionResult> CreatePost(Guid channelId, CreatePostRequest request, CancellationToken cancellationToken)
    {
        return ToActionResult(await channels.CreatePostAsync(channelId, request, cancellationToken));
    }

    [HttpGet("api/posts/{postId:guid}")]
    public async Task<IActionResult> GetPost(Guid postId, CancellationToken cancellationToken)
    {
        return ToActionResult(await channels.GetPostAsync(postId, cancellationToken));
    }

    [HttpPatch("api/posts/{postId:guid}")]
    public async Task<IActionResult> UpdatePost(Guid postId, UpdatePostRequest request, CancellationToken cancellationToken)
    {
        return ToActionResult(await channels.UpdatePostAsync(postId, request, cancellationToken));
    }

    [HttpDelete("api/posts/{postId:guid}")]
    public async Task<IActionResult> DeletePost(Guid postId, CancellationToken cancellationToken)
    {
        var result = await channels.DeletePostAsync(postId, cancellationToken);
        return result.IsSuccess ? Ok(new { status = "OK" }) : BadRequest(new { error = result.Error });
    }

    [HttpGet("api/posts/{postId:guid}/threads")]
    public async Task<IActionResult> ListThreads(Guid postId, [FromQuery] ThreadListQuery query, CancellationToken cancellationToken)
    {
        return ToActionResult(await channels.ListThreadsAsync(postId, query, cancellationToken));
    }

    [HttpPost("api/posts/{postId:guid}/threads")]
    public async Task<IActionResult> CreateThread(Guid postId, CreateThreadReplyRequest request, CancellationToken cancellationToken)
    {
        return ToActionResult(await channels.CreateThreadAsync(postId, request, cancellationToken));
    }

    [HttpPost("api/posts/{postId:guid}/pin")]
    public async Task<IActionResult> Pin(Guid postId, CancellationToken cancellationToken)
    {
        var result = await channels.PinPostAsync(postId, cancellationToken);
        return result.IsSuccess ? Ok(new { status = "OK" }) : BadRequest(new { error = result.Error });
    }

    [HttpDelete("api/posts/{postId:guid}/pin")]
    public async Task<IActionResult> Unpin(Guid postId, CancellationToken cancellationToken)
    {
        var result = await channels.UnpinPostAsync(postId, cancellationToken);
        return result.IsSuccess ? Ok(new { status = "OK" }) : BadRequest(new { error = result.Error });
    }

    [HttpGet("api/channels/{channelId:guid}/pinned-posts")]
    public async Task<IActionResult> PinnedPosts(Guid channelId, CancellationToken cancellationToken)
    {
        return ToActionResult(await channels.ListPinnedPostsAsync(channelId, cancellationToken));
    }

}
