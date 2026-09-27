using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Security.Redaction;
using Coglatas.Application.Workspaces;
using Coglatas.Domain.Enums;
using Coglatas.Web.Models;
using Coglatas.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Coglatas.Web.Controllers;

[ApiController]
[Authorize]
public sealed class WorkspacesController(
    IWorkspaceService workspaces,
    ITenantRepository? tenants = null,
    ICurrentTenant? currentTenant = null,
    IWorkspaceMemberProjectionService? memberProjections = null) : ControllerBase
{
    [HttpGet("api/workspaces")]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var result = await workspaces.ListAsync(cancellationToken);
        return result.IsSuccess
            ? Ok(CanonicalRedactionProjection.Apply(HttpContext, result.Value!, RedactionProfile.UiList, "Workspaces", RedactionAuthorizationState.Allowed))
            : ToWpcError(result.ErrorDetail, result.Error, "Workspaces could not be listed.");
    }

    [HttpGet("api/workspaces/archived")]
    public async Task<IActionResult> ListArchived(CancellationToken cancellationToken)
    {
        var result = await workspaces.ListArchivedAsync(cancellationToken);
        return result.IsSuccess
            ? Ok(CanonicalRedactionProjection.Apply(HttpContext, result.Value!, RedactionProfile.UiList, "Workspaces", RedactionAuthorizationState.Allowed))
            : ToWpcError(result.ErrorDetail, result.Error, "Archived Workspaces could not be listed.");
    }

    [HttpGet("api/workspaces/capabilities")]
    public async Task<IActionResult> Capabilities(CancellationToken cancellationToken)
    {
        var result = await workspaces.GetCapabilitiesAsync(cancellationToken);
        return result.IsSuccess
            ? Ok(ApiEnvelope.Success(HttpContext, CanonicalRedactionProjection.Apply(HttpContext, result.Value!, RedactionProfile.UiDetail, "WorkspaceCapabilities", RedactionAuthorizationState.Allowed)))
            : ToWpcError(result.ErrorDetail, result.Error, "Workspace capabilities could not be evaluated.");
    }

    [HttpPost("api/workspaces")]
    [ProducesResponseType(typeof(ApiSuccessEnvelope<WorkspaceDetailResponse>), StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(
        CreateWorkspaceRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var result = await workspaces.CreateAsync(request, idempotencyKey, cancellationToken);
        if (result.IsSuccess)
        {
            return CreatedAtAction(
                nameof(Get),
                new { workspaceId = result.Value!.Id },
                ApiEnvelope.Success(
                    HttpContext,
                    CanonicalRedactionProjection.Apply(HttpContext, result.Value!, RedactionProfile.UiDetail, "WorkspaceCreate", RedactionAuthorizationState.Allowed)));
        }

        return ToWpcError(result.ErrorDetail, result.Error, "Workspace creation failed.");
    }

    private IActionResult ToWpcError(
        ApplicationErrorDetail? detail,
        string? fallbackError,
        string fallbackMessage)
    {
        var sourceCode = detail?.Code ?? "ValidationFailed";
        var code = sourceCode;
        var message = detail?.Message ?? fallbackError ?? fallbackMessage;
        var target = detail?.Target ?? (sourceCode switch
        {
            "CapabilityDenied" => "workspace",
            "IdempotencyConflict" => "header.Idempotency-Key",
            _ => null
        });
        var status = sourceCode switch
        {
            "AuthenticationRequired" => StatusCodes.Status401Unauthorized,
            "CapabilityDenied" or "TenantMembershipRequired" => StatusCodes.Status403Forbidden,
            "NotFound" => StatusCodes.Status404NotFound,
            "IdempotencyConflict" or "ConcurrentModification" or "InvalidStateTransition" => StatusCodes.Status409Conflict,
            "DependencyUnavailable" => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status400BadRequest
        };
        var payload = ApiEnvelope.Error(
            HttpContext,
            status,
            code,
            message,
            target,
            CanonicalErrorExposurePolicy.IsSensitive(sourceCode));
        return sourceCode switch
        {
            "AuthenticationRequired" => Unauthorized(payload),
            "CapabilityDenied" or "TenantMembershipRequired" => StatusCode(status, payload),
            "NotFound" => NotFound(payload),
            "IdempotencyConflict" or "ConcurrentModification" or "InvalidStateTransition" => Conflict(payload),
            "DependencyUnavailable" => StatusCode(status, payload),
            _ => BadRequest(payload)
        };
    }

    [HttpGet("api/workspaces/{workspaceId:guid}")]
    public async Task<IActionResult> Get(Guid workspaceId, CancellationToken cancellationToken)
    {
        var result = await workspaces.GetAsync(workspaceId, cancellationToken);
        return result.IsSuccess
            ? Ok(CanonicalRedactionProjection.Apply(HttpContext, result.Value!, RedactionProfile.UiDetail, "WorkspaceRead", RedactionAuthorizationState.Allowed))
            : ToWpcError(result.ErrorDetail, result.Error, "Workspace could not be read.");
    }

    [HttpPatch("api/workspaces/{workspaceId:guid}")]
    public async Task<IActionResult> Update(Guid workspaceId, UpdateWorkspaceRequest request, CancellationToken cancellationToken)
    {
        var result = await workspaces.UpdateAsync(workspaceId, request, cancellationToken);
        return result.IsSuccess
            ? Ok(CanonicalRedactionProjection.Apply(HttpContext, result.Value!, RedactionProfile.UiDetail, "WorkspaceUpdate", RedactionAuthorizationState.Allowed))
            : ToWpcError(result.ErrorDetail, result.Error, "Workspace update failed.");
    }

    [HttpDelete("api/workspaces/{workspaceId:guid}")]
    public async Task<IActionResult> Delete(Guid workspaceId, CancellationToken cancellationToken) =>
        await ArchiveResult(workspaceId, cancellationToken);

    [HttpPost("api/workspaces/{workspaceId:guid}/archive")]
    public async Task<IActionResult> Archive(Guid workspaceId, CancellationToken cancellationToken) =>
        await ArchiveResult(workspaceId, cancellationToken);

    [HttpPost("api/workspaces/{workspaceId:guid}/restore")]
    public async Task<IActionResult> Restore(Guid workspaceId, CancellationToken cancellationToken)
    {
        var result = await workspaces.RestoreAsync(workspaceId, cancellationToken);
        return result.IsSuccess
            ? Ok(new { status = "OK" })
            : ToWpcError(result.ErrorDetail, result.Error, "Workspace restore failed.");
    }

    [HttpGet("api/workspaces/{workspaceId:guid}/members")]
    public async Task<IActionResult> ListMembers(Guid workspaceId, CancellationToken cancellationToken)
    {
        var result = memberProjections is not null
            ? await memberProjections.ListAsync(workspaceId, cancellationToken)
            : await workspaces.ListMembersAsync(workspaceId, cancellationToken);

        if (!result.IsSuccess)
        {
            return ToWpcError(result.ErrorDetail, result.Error, "Workspace members could not be read.");
        }

        // Fail closed for compatibility implementations that still return historical
        // membership rows. Status is server-only and is never serialized to the ordinary DTO.
        var visibleMembers = result.Value!
            .Where(member => member.Status == MembershipStatus.Active)
            .ToList();
        return Ok(CanonicalRedactionProjection.Apply(
            HttpContext,
            visibleMembers,
            RedactionProfile.UiList,
            "WorkspaceMembers",
            RedactionAuthorizationState.Allowed));
    }

    [HttpGet("api/workspaces/{workspaceId:guid}/members/{userId:guid}")]
    public async Task<IActionResult> GetMember(Guid workspaceId, Guid userId, CancellationToken cancellationToken)
    {
        Result<WorkspaceMemberResponse> result;
        if (memberProjections is not null)
        {
            result = await memberProjections.GetAsync(workspaceId, userId, cancellationToken);
        }
        else
        {
            var list = await workspaces.ListMembersAsync(workspaceId, cancellationToken);
            if (!list.IsSuccess)
            {
                return ToWpcError(list.ErrorDetail, list.Error, "Workspace member could not be read.");
            }

            var member = list.Value!.FirstOrDefault(candidate =>
                candidate.UserId == userId && candidate.Status == MembershipStatus.Active);
            result = member is null
                ? Result<WorkspaceMemberResponse>.Failure(new ApplicationErrorDetail(
                    "NotFound",
                    "The requested resource was not found."))
                : Result<WorkspaceMemberResponse>.Success(member);
        }

        return result.IsSuccess
            ? Ok(CanonicalRedactionProjection.Apply(
                HttpContext,
                result.Value!,
                RedactionProfile.UiDetail,
                "WorkspaceMember",
                RedactionAuthorizationState.Allowed))
            : ToWpcError(result.ErrorDetail, result.Error, "Workspace member could not be read.");
    }

    [HttpGet("api/workspaces/{workspaceId:guid}/members/management")]
    public async Task<IActionResult> ListManagedMembers(Guid workspaceId, CancellationToken cancellationToken)
    {
        if (memberProjections is null)
        {
            return MemberProjectionUnavailable();
        }

        var result = await memberProjections.ListManagementAsync(workspaceId, cancellationToken);
        return result.IsSuccess
            ? Ok(CanonicalRedactionProjection.Apply(
                HttpContext,
                result.Value!,
                RedactionProfile.UiList,
                "WorkspaceMemberManagement",
                RedactionAuthorizationState.Allowed,
                fieldAccessPolicy: FieldAccessPolicySnapshot.ThroughConfidential))
            : ToWpcError(result.ErrorDetail, result.Error, "Workspace membership management data could not be read.");
    }

    [HttpGet("api/workspaces/{workspaceId:guid}/members/{userId:guid}/management")]
    public async Task<IActionResult> GetManagedMember(Guid workspaceId, Guid userId, CancellationToken cancellationToken)
    {
        if (memberProjections is null)
        {
            return MemberProjectionUnavailable();
        }

        var result = await memberProjections.GetManagementAsync(workspaceId, userId, cancellationToken);
        return result.IsSuccess
            ? Ok(CanonicalRedactionProjection.Apply(
                HttpContext,
                result.Value!,
                RedactionProfile.UiDetail,
                "WorkspaceMemberManagement",
                RedactionAuthorizationState.Allowed,
                fieldAccessPolicy: FieldAccessPolicySnapshot.ThroughConfidential))
            : ToWpcError(result.ErrorDetail, result.Error, "Workspace membership management data could not be read.");
    }

    [HttpPost("api/workspaces/{workspaceId:guid}/members")]
    public async Task<IActionResult> AddMember(Guid workspaceId, AddWorkspaceMemberRequest request, CancellationToken cancellationToken)
    {
        if (!await IsActiveCurrentTenantUserAsync(request.UserId, cancellationToken))
        {
            return ToWpcError(
                new ApplicationErrorDetail("NotFound", "The requested resource was not found."),
                null,
                "Workspace member could not be added.");
        }

        var result = await workspaces.AddMemberAsync(workspaceId, request, cancellationToken);
        return result.IsSuccess
            ? Ok(CanonicalRedactionProjection.Apply(HttpContext, result.Value!, RedactionProfile.UiDetail, "WorkspaceMemberUpdate", RedactionAuthorizationState.Allowed))
            : ToWpcError(result.ErrorDetail, result.Error, "Workspace member could not be added.");
    }

    [HttpPatch("api/workspaces/{workspaceId:guid}/members/{userId:guid}")]
    public async Task<IActionResult> UpdateMember(Guid workspaceId, Guid userId, UpdateWorkspaceMemberRequest request, CancellationToken cancellationToken)
    {
        var result = await workspaces.UpdateMemberAsync(workspaceId, userId, request, cancellationToken);
        return result.IsSuccess
            ? Ok(CanonicalRedactionProjection.Apply(HttpContext, result.Value!, RedactionProfile.UiDetail, "WorkspaceMemberUpdate", RedactionAuthorizationState.Allowed))
            : ToWpcError(result.ErrorDetail, result.Error, "Workspace member could not be updated.");
    }

    [HttpDelete("api/workspaces/{workspaceId:guid}/members/{userId:guid}")]
    public async Task<IActionResult> RemoveMember(Guid workspaceId, Guid userId, CancellationToken cancellationToken)
    {
        var result = await workspaces.RemoveMemberAsync(workspaceId, userId, cancellationToken);
        return result.IsSuccess
            ? Ok(new { status = "OK" })
            : ToWpcError(result.ErrorDetail, result.Error, "Workspace member could not be removed.");
    }

    private IActionResult MemberProjectionUnavailable() =>
        ToWpcError(
            new ApplicationErrorDetail(
                "DependencyUnavailable",
                "Workspace member projection is temporarily unavailable."),
            null,
            "Workspace member projection is temporarily unavailable.");

    private async Task<bool> IsActiveCurrentTenantUserAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty ||
            tenants is null ||
            currentTenant is not { IsAvailable: true, IsPlatformScope: false } ||
            currentTenant.TenantId == Guid.Empty)
        {
            return false;
        }

        var membership = await tenants.GetTenantUserAsync(
            currentTenant.TenantId,
            userId,
            cancellationToken);
        return membership is
               {
                   Status: TenantUserStatus.Active,
                   User: { Status: UserStatus.Active, DeletedAt: null },
                   Tenant: { Status: TenantStatus.Active, DeletedAt: null }
               } &&
               membership.TenantId == currentTenant.TenantId &&
               membership.UserId == userId &&
               membership.Tenant.Id == currentTenant.TenantId &&
               membership.User.Id == userId;
    }

    private async Task<IActionResult> ArchiveResult(Guid workspaceId, CancellationToken cancellationToken)
    {
        var result = await workspaces.ArchiveAsync(workspaceId, cancellationToken);
        return result.IsSuccess
            ? Ok(new { status = "OK" })
            : ToWpcError(result.ErrorDetail, result.Error, "Workspace archive failed.");
    }
}
