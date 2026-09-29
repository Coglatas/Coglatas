using Coglatas.Application.Artifacts;
using Coglatas.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Coglatas.Web.Controllers;

[ApiController, Authorize]
public sealed class ArtifactReportsController(
    IArtifactReportService reports,
    IArtifactReportRefinementService refinements) : ControllerBase
{
    [HttpGet("api/projects/{projectId:guid}/artifact-versions/{artifactVersionId:guid}/report")]
    public async Task<IActionResult> Get(
        Guid projectId,
        Guid artifactVersionId,
        [FromQuery] Guid? taskId,
        CancellationToken cancellationToken) =>
        ToActionResult(await reports.GetAsync(projectId, artifactVersionId, taskId, cancellationToken));

    [HttpPost("api/artifact-versions/{artifactVersionId:guid}/report")]
    public async Task<IActionResult> Attach(
        Guid artifactVersionId,
        AttachArtifactReportRequest request,
        CancellationToken cancellationToken) =>
        ToActionResult(await reports.AttachAsync(artifactVersionId, request, cancellationToken));

    [HttpGet("api/projects/{projectId:guid}/artifact-versions/{artifactVersionId:guid}/report/refinement-preflight")]
    public async Task<IActionResult> RefinementPreflight(
        Guid projectId,
        Guid artifactVersionId,
        [FromQuery] ArtifactReportRefinementTargetKind targetKind,
        [FromQuery] Guid targetLogicalId,
        CancellationToken cancellationToken) =>
        ToActionResult(await refinements.PreflightAsync(
            projectId,
            artifactVersionId,
            targetKind,
            targetLogicalId,
            cancellationToken));

    [HttpPost("api/projects/{projectId:guid}/artifact-versions/{artifactVersionId:guid}/report/refinements")]
    public async Task<IActionResult> Refine(
        Guid projectId,
        Guid artifactVersionId,
        RefineArtifactReportRequest request,
        CancellationToken cancellationToken) =>
        ToActionResult(await refinements.RefineAsync(
            projectId,
            artifactVersionId,
            request,
            cancellationToken));

    private IActionResult ToActionResult<T>(Coglatas.Application.Common.Result<T> result)
    {
        if (result.IsSuccess)
            return Ok(result.Value);

        var code = result.ErrorDetail?.Code;
        var status = code switch
        {
            "ReportNotFound" or "ReportRefinementTargetNotFound" => StatusCodes.Status404NotFound,
            "ReportAlreadyAttached" or
            "ReportRefinementStaleVersion" or
            "ReportRefinementScopeChanged" or
            "ReportRefinementConcurrentUpdate" => StatusCodes.Status409Conflict,
            "ReportRefinementProjectFilesRequired" or
            "ReportRefinementUnsupportedSources" or
            "ReportRefinementTargetHasNoClaims" or
            "ReportRefinementNoAuthorizedSources" or
            "ReportRefinementNoNewEvidence" => StatusCodes.Status422UnprocessableEntity,
            "ReportRefinementUnavailable" => StatusCodes.Status503ServiceUnavailable,
            "AuthenticationRequired" => StatusCodes.Status401Unauthorized,
            _ => StatusCodes.Status400BadRequest
        };
        return StatusCode(
            status,
            CanonicalErrorEnvelope.FromSensitiveResult(
                HttpContext,
                status,
                result.ErrorDetail,
                result.Error,
                "ArtifactReportFailed"));
    }
}
