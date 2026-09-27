using Coglatas.Application.Groups;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Coglatas.Web.Controllers;

[ApiController]
[Authorize]
public sealed class GroupsController(IGroupService groups) : ApiResultControllerBase
{
    [HttpGet("api/workspaces/{workspaceId:guid}/groups")]
    public async Task<IActionResult> List(Guid workspaceId, CancellationToken cancellationToken)
    {
        return ToActionResult(await groups.ListByWorkspaceAsync(workspaceId, cancellationToken));
    }

    [HttpPost("api/workspaces/{workspaceId:guid}/groups")]
    public async Task<IActionResult> Create(Guid workspaceId, CreateGroupRequest request, CancellationToken cancellationToken)
    {
        return ToActionResult(await groups.CreateAsync(workspaceId, request, cancellationToken));
    }

    [HttpGet("api/groups/{groupId:guid}")]
    public async Task<IActionResult> Get(Guid groupId, CancellationToken cancellationToken)
    {
        return ToActionResult(await groups.GetAsync(groupId, cancellationToken));
    }

    [HttpPatch("api/groups/{groupId:guid}")]
    public async Task<IActionResult> Update(Guid groupId, UpdateGroupRequest request, CancellationToken cancellationToken)
    {
        return ToActionResult(await groups.UpdateAsync(groupId, request, cancellationToken));
    }

    [HttpDelete("api/groups/{groupId:guid}")]
    public async Task<IActionResult> Delete(Guid groupId, CancellationToken cancellationToken)
    {
        var result = await groups.ArchiveAsync(groupId, cancellationToken);
        return result.IsSuccess ? Ok(new { status = "OK" }) : BadRequest(new { error = result.Error });
    }

    [HttpPost("api/groups/{groupId:guid}/archive")]
    public async Task<IActionResult> Archive(Guid groupId, CancellationToken cancellationToken)
    {
        var result = await groups.ArchiveAsync(groupId, cancellationToken);
        return result.IsSuccess ? Ok(new { status = "OK" }) : BadRequest(new { error = result.Error });
    }

    [HttpPost("api/groups/{groupId:guid}/restore")]
    public async Task<IActionResult> Restore(Guid groupId, CancellationToken cancellationToken)
    {
        var result = await groups.RestoreAsync(groupId, cancellationToken);
        return result.IsSuccess ? Ok(new { status = "OK" }) : BadRequest(new { error = result.Error });
    }

    [HttpGet("api/groups/{groupId:guid}/members")]
    public async Task<IActionResult> ListMembers(Guid groupId, CancellationToken cancellationToken)
    {
        return ToActionResult(await groups.ListMembersAsync(groupId, cancellationToken));
    }

    [HttpPost("api/groups/{groupId:guid}/members")]
    public async Task<IActionResult> AddMember(Guid groupId, AddGroupMemberRequest request, CancellationToken cancellationToken)
    {
        return ToActionResult(await groups.AddMemberAsync(groupId, request, cancellationToken));
    }

    [HttpPatch("api/groups/{groupId:guid}/members/{userId:guid}")]
    public async Task<IActionResult> UpdateMember(Guid groupId, Guid userId, UpdateGroupMemberRequest request, CancellationToken cancellationToken)
    {
        return ToActionResult(await groups.UpdateMemberAsync(groupId, userId, request, cancellationToken));
    }

    [HttpDelete("api/groups/{groupId:guid}/members/{userId:guid}")]
    public async Task<IActionResult> RemoveMember(Guid groupId, Guid userId, CancellationToken cancellationToken)
    {
        var result = await groups.RemoveMemberAsync(groupId, userId, cancellationToken);
        return result.IsSuccess ? Ok(new { status = "OK" }) : BadRequest(new { error = result.Error });
    }

}
