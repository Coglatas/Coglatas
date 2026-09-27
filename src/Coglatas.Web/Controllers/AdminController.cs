using Coglatas.Application.Admin;
using Coglatas.Web.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Coglatas.Web.Controllers;

[ApiController]
[Authorize(Roles = "PlatformAdmin,SystemAdmin")]
[EnableRateLimiting(HttpSecurityPolicy.AdminRateLimitPolicy)]
[Route("api/admin")]
public sealed class AdminController(IAdminService adminService) : ApiResultControllerBase
{
    [HttpGet("users")]
    public async Task<IActionResult> ListUsers([FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken cancellationToken = default)
    {
        return ToActionResult(await adminService.ListUsersAsync(page, pageSize, cancellationToken));
    }

    [HttpGet("users/{userId:guid}")]
    public async Task<IActionResult> GetUser(Guid userId, CancellationToken cancellationToken)
    {
        return ToActionResult(await adminService.GetUserAsync(userId, cancellationToken));
    }

    [HttpPatch("users/{userId:guid}")]
    public async Task<IActionResult> UpdateUser(Guid userId, UpdateAdminUserRequest request, CancellationToken cancellationToken)
    {
        return ToActionResult(await adminService.UpdateUserAsync(userId, request, cancellationToken));
    }

    [HttpPost("users/{userId:guid}/suspend")]
    public async Task<IActionResult> SuspendUser(Guid userId, CancellationToken cancellationToken)
    {
        return OkOrBad(await adminService.SuspendUserAsync(userId, cancellationToken));
    }

    [HttpPost("users/{userId:guid}/activate")]
    public async Task<IActionResult> ActivateUser(Guid userId, CancellationToken cancellationToken)
    {
        return OkOrBad(await adminService.ActivateUserAsync(userId, cancellationToken));
    }

    [HttpPost("users/{userId:guid}/reset-password-invite")]
    public async Task<IActionResult> ResetPasswordInvite(Guid userId, CancellationToken cancellationToken)
    {
        return OkOrBad(await adminService.ResetPasswordInviteAsync(userId, cancellationToken));
    }

    [HttpDelete("users/{userId:guid}")]
    public async Task<IActionResult> ArchiveUser(Guid userId, CancellationToken cancellationToken)
    {
        return OkOrBad(await adminService.ArchiveUserAsync(userId, cancellationToken));
    }

    [HttpPatch("users/{userId:guid}/system-role")]
    public async Task<IActionResult> ChangeSystemRole(Guid userId, ChangeSystemRoleRequest request, CancellationToken cancellationToken)
    {
        return ToActionResult(await adminService.ChangeSystemRoleAsync(userId, request, cancellationToken));
    }

    [HttpGet("invites")]
    public async Task<IActionResult> ListInvites([FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken cancellationToken = default)
    {
        return ToActionResult(await adminService.ListInvitesAsync(page, pageSize, cancellationToken));
    }

    [HttpPost("invites")]
    public async Task<IActionResult> CreateInvite(CreateInviteRequest request, CancellationToken cancellationToken)
    {
        var result = await adminService.CreateInviteAsync(request, cancellationToken);
        return result is { IsSuccess: true, Value: { } invite }
            ? Ok(ToHttpInviteResponse(invite))
            : BadRequest(new { error = result.Error });
    }

    [HttpPost("invites/{inviteId:guid}/revoke")]
    public async Task<IActionResult> RevokeInvite(Guid inviteId, CancellationToken cancellationToken)
    {
        return OkOrBad(await adminService.RevokeInviteAsync(inviteId, cancellationToken));
    }

    [HttpPost("invites/bulk")]
    public async Task<IActionResult> BulkCreateInvites(BulkCreateInviteRequest request, CancellationToken cancellationToken)
    {
        var result = await adminService.BulkCreateInvitesAsync(request, cancellationToken);
        return result is { IsSuccess: true, Value: { } invites }
            ? Ok(invites.Select(ToHttpInviteResponse).ToList())
            : BadRequest(new { error = result.Error });
    }

    [HttpGet("settings")]
    public async Task<IActionResult> ListSettings(CancellationToken cancellationToken)
    {
        return ToActionResult(await adminService.ListSettingsAsync(cancellationToken));
    }

    [HttpGet("settings/{key}")]
    public async Task<IActionResult> GetSetting(string key, CancellationToken cancellationToken)
    {
        return ToActionResult(await adminService.GetSettingAsync(key, cancellationToken));
    }

    [HttpPatch("settings/{key}")]
    public async Task<IActionResult> UpdateSetting(string key, UpdateSystemSettingRequest request, CancellationToken cancellationToken)
    {
        return ToActionResult(await adminService.UpdateSettingAsync(key, request, cancellationToken));
    }

    [HttpPost("lifecycle/workspaces/{workspaceId:guid}/archive")]
    public async Task<IActionResult> ArchiveWorkspace(Guid workspaceId, CancellationToken cancellationToken)
    {
        return OkOrBad(await adminService.ArchiveWorkspaceAsync(workspaceId, cancellationToken));
    }

    [HttpPost("lifecycle/groups/{groupId:guid}/archive")]
    public async Task<IActionResult> ArchiveGroup(Guid groupId, CancellationToken cancellationToken)
    {
        return OkOrBad(await adminService.ArchiveGroupAsync(groupId, cancellationToken));
    }

    [HttpPost("lifecycle/projects/{projectId:guid}/archive")]
    public async Task<IActionResult> ArchiveProject(Guid projectId, CancellationToken cancellationToken)
    {
        return OkOrBad(await adminService.ArchiveProjectAsync(projectId, cancellationToken));
    }

    [HttpPost("lifecycle/channels/{channelId:guid}/archive")]
    public async Task<IActionResult> ArchiveChannel(Guid channelId, CancellationToken cancellationToken)
    {
        return OkOrBad(await adminService.ArchiveChannelAsync(channelId, cancellationToken));
    }

    [HttpPost("task-deadline-digests/{jobId:guid}/restart")]
    public async Task<IActionResult> RestartTaskDeadlineDigest(
        Guid jobId,
        RestartTaskDeadlineDigestRequest request,
        CancellationToken cancellationToken)
    {
        return OkOrBad(await adminService.RestartTaskDeadlineDigestAsync(jobId, request, cancellationToken));
    }

    [HttpGet("dashboard")]
    public async Task<IActionResult> Dashboard(CancellationToken cancellationToken)
    {
        return ToActionResult(await adminService.GetDashboardAsync(cancellationToken));
    }

    private object ToHttpInviteResponse(AdminInviteResponse invite)
    {
        var inviteUrl = string.IsNullOrWhiteSpace(invite.InviteToken)
            ? null
            : BuildInviteUrl(invite.InviteToken);

        return new
        {
            invite.Id,
            invite.WorkspaceId,
            invite.Email,
            Role = invite.Role.ToString(),
            invite.ExpiresAt,
            invite.AcceptedAt,
            invite.RevokedAt,
            invite.InvitedByUserId,
            invite.CreatedAt,
            InviteUrl = inviteUrl
        };
    }

    private string BuildInviteUrl(string token)
    {
        var pathBase = Request.PathBase.HasValue ? Request.PathBase.Value : string.Empty;
        return $"{Request.Scheme}://{Request.Host}{pathBase}/app/register/invite?token={Uri.EscapeDataString(token)}";
    }

}
