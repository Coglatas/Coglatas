using Coglatas.Application.Artifacts;
using Coglatas.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Coglatas.Web.Controllers;

[ApiController]
[Authorize]
public sealed class ArtifactEvidenceController(IArtifactEvidenceManifestService evidence) : ControllerBase
{
    [HttpPost("api/artifact-versions/{artifactVersionId:guid}/claims-evidence")]
    public async Task<IActionResult> Attach(
        Guid artifactVersionId,
        AttachArtifactEvidenceManifestRequest request,
        CancellationToken cancellationToken)
    {
        var result = await evidence.AttachAsync(artifactVersionId, request, cancellationToken);
        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        var status = result.ErrorDetail?.Code switch
        {
            "AuthenticationRequired" => StatusCodes.Status401Unauthorized,
            "CapabilityDenied" => StatusCodes.Status403Forbidden,
            "ArtifactVersionNotFound" or "SourceNotAuthorized" => StatusCodes.Status404NotFound,
            "EvidenceManifestAlreadyAttached" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest
        };

        return StatusCode(status, CanonicalErrorEnvelope.FromSensitiveResult(
            HttpContext,
            status,
            result.ErrorDetail,
            result.Error,
            "ArtifactEvidenceManifestFailed"));
    }
}
