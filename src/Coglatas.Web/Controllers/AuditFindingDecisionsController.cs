using Coglatas.Application.Audit;
using Coglatas.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Coglatas.Web.Controllers;

[ApiController]
[Authorize]
public sealed class AuditFindingDecisionsController(IAuditFindingDecisionService decisions) : ControllerBase
{
    [HttpGet("api/admin/audit/findings/{findingId:guid}/decision")]
    public async Task<IActionResult> Get(
        Guid findingId,
        CancellationToken cancellationToken)
    {
        var result = await decisions.GetAsync(findingId, cancellationToken);
        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        return Error(result.ErrorDetail, result.Error, "AuditFindingDecisionGetFailed");
    }

    [HttpPut("api/admin/audit/findings/{findingId:guid}/decision")]
    public async Task<IActionResult> Save(
        Guid findingId,
        [FromBody] SaveAuditFindingDecisionRequest request,
        CancellationToken cancellationToken)
    {
        var result = await decisions.SaveAsync(findingId, request, cancellationToken);
        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        return Error(result.ErrorDetail, result.Error, "AuditFindingDecisionSaveFailed");
    }

    private IActionResult Error(
        Coglatas.Application.Common.ApplicationErrorDetail? detail,
        string? fallback,
        string canonicalCode)
    {
        var status = detail?.Code switch
        {
            "AuthenticationRequired" => StatusCodes.Status401Unauthorized,
            "CapabilityDenied" or "TenantMembershipRequired" => StatusCodes.Status403Forbidden,
            "FindingNotFound" => StatusCodes.Status404NotFound,
            "ReasonRequired" or "ValidationFailed" => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status400BadRequest
        };

        return StatusCode(status, CanonicalErrorEnvelope.FromSensitiveResult(
            HttpContext,
            status,
            detail,
            fallback,
            canonicalCode));
    }
}
