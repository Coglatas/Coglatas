using Coglatas.Application.Audit;
using Coglatas.Web.Models;
using Coglatas.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Coglatas.Web.Controllers;

[ApiController]
[Authorize]
[Route("api/admin/audit/package-exports")]
public sealed class AuditPackageExportsController(IAuditPackageExportService exports) : ControllerBase
{
    [HttpGet("preview")]
    public async Task<IActionResult> Preview(
        [FromQuery] Guid artifactVersionId,
        CancellationToken cancellationToken)
    {
        if (artifactVersionId == Guid.Empty)
        {
            return BadRequest(ApiEnvelope.Error(
                HttpContext,
                StatusCodes.Status400BadRequest,
                "ArtifactVersionRequired",
                "An artifact version is required.",
                "artifactVersionId"));
        }

        return FromResult(await exports.PreviewAsync(artifactVersionId, cancellationToken));
    }

    [HttpPost]
    public async Task<IActionResult> Queue(
        [FromBody] QueueAuditPackageExportRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ArtifactVersionId == Guid.Empty)
        {
            return BadRequest(ApiEnvelope.Error(
                HttpContext,
                StatusCodes.Status400BadRequest,
                "ArtifactVersionRequired",
                "An artifact version is required.",
                "artifactVersionId"));
        }

        var result = await exports.QueueAsync(request, cancellationToken);
        return result.IsSuccess
            ? AcceptedAtAction(nameof(GetJob), new { jobId = result.Value!.JobId }, result.Value)
            : FromFailure(result.ErrorDetail, result.Error);
    }

    [HttpGet("{jobId:guid}")]
    public async Task<IActionResult> GetJob(Guid jobId, CancellationToken cancellationToken) =>
        FromResult(await exports.GetJobAsync(jobId, cancellationToken));

    [HttpPost("{jobId:guid}/retry")]
    public async Task<IActionResult> Retry(Guid jobId, CancellationToken cancellationToken)
    {
        var result = await exports.RetryAsync(jobId, cancellationToken);
        return result.IsSuccess
            ? AcceptedAtAction(nameof(GetJob), new { jobId = result.Value!.JobId }, result.Value)
            : FromFailure(result.ErrorDetail, result.Error);
    }

    [HttpGet("{jobId:guid}/download")]
    public async Task<IActionResult> Download(Guid jobId, CancellationToken cancellationToken)
    {
        var result = await exports.DownloadAsync(jobId, cancellationToken);
        if (!result.IsSuccess || result.Value is null)
        {
            return FromFailure(result.ErrorDetail, result.Error);
        }

        return File(
            result.Value.Content,
            result.Value.ContentType,
            result.Value.FileName,
            enableRangeProcessing: false);
    }

    private IActionResult FromResult<T>(Coglatas.Application.Common.Result<T> result) =>
        result.IsSuccess ? Ok(result.Value) : FromFailure(result.ErrorDetail, result.Error);

    private IActionResult FromFailure(
        Coglatas.Application.Common.ApplicationErrorDetail? detail,
        string? fallback)
    {
        var status = detail?.Code switch
        {
            "AuthenticationRequired" => StatusCodes.Status401Unauthorized,
            "CapabilityDenied" or "TenantMembershipRequired" or "AuthorizationChanged" => StatusCodes.Status403Forbidden,
            "ArtifactVersionNotFound" or "ExportJobNotFound" => StatusCodes.Status404NotFound,
            "ExportRetryNotAllowed" or "ExportJobNotReady" or "ExportPackageUnavailable" or "ExportPackageCleanupFailed" => StatusCodes.Status409Conflict,
            "ArtifactVersionRequired" => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status400BadRequest
        };

        return StatusCode(status, CanonicalErrorEnvelope.FromSensitiveResult(
            HttpContext,
            status,
            detail,
            fallback,
            "AuditPackageExportFailed"));
    }
}
