using System.ComponentModel.DataAnnotations;
using Coglatas.Application.Artifacts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Coglatas.Web.Controllers;

[ApiController]
[Authorize]
public sealed class ArtifactsController(IArtifactService artifacts) : ControllerBase
{
    [HttpGet("api/projects/{projectId:guid}/artifacts")]
    public async Task<IActionResult> List(Guid projectId, CancellationToken cancellationToken) => ToActionResult(await artifacts.ListAsync(projectId, cancellationToken));

    [HttpPost("api/projects/{projectId:guid}/artifacts")]
    public async Task<IActionResult> Create(Guid projectId, CreateArtifactRequest request, CancellationToken cancellationToken) => ToActionResult(await artifacts.CreateAsync(projectId, request, cancellationToken));

    [HttpGet("api/artifacts/{artifactId:guid}")]
    public async Task<IActionResult> Get(Guid artifactId, CancellationToken cancellationToken) => ToActionResult(await artifacts.GetAsync(artifactId, cancellationToken));

    [HttpPatch("api/artifacts/{artifactId:guid}")]
    public async Task<IActionResult> Update(Guid artifactId, UpdateArtifactRequest request, CancellationToken cancellationToken) => ToActionResult(await artifacts.UpdateAsync(artifactId, request, cancellationToken));

    [HttpDelete("api/artifacts/{artifactId:guid}")]
    public async Task<IActionResult> Delete(Guid artifactId, CancellationToken cancellationToken) => OkOrBad(await artifacts.DeleteAsync(artifactId, cancellationToken));

    [HttpGet("api/artifacts/{artifactId:guid}/versions")]
    public async Task<IActionResult> ListVersions(Guid artifactId, CancellationToken cancellationToken) => ToActionResult(await artifacts.ListVersionsAsync(artifactId, cancellationToken));

    [HttpPost("api/artifacts/{artifactId:guid}/versions")]
    [Consumes("multipart/form-data")]
    [EnableRateLimiting("file-upload")]
    public async Task<IActionResult> UploadVersion(Guid artifactId, [FromForm] UploadArtifactVersionForm form, CancellationToken cancellationToken)
    {
        if (form.File is null)
        {
            return BadRequest(new { error = "File is required." });
        }

        await using var stream = form.File.OpenReadStream();
        return ToActionResult(await artifacts.UploadVersionAsync(artifactId, new UploadArtifactVersionInput(
            form.File.FileName,
            form.File.ContentType,
            form.File.Length,
            stream,
            form.ChangeNote), cancellationToken));
    }

    [HttpGet("api/artifact-versions/{versionId:guid}/download")]
    public async Task<IActionResult> DownloadVersion(Guid versionId, CancellationToken cancellationToken)
    {
        var result = await artifacts.DownloadVersionAsync(versionId, cancellationToken);
        return result.IsSuccess
            ? PrivateFile(result.Value!.Content, result.Value.ContentType, result.Value.FileName)
            : Failure(result.Error);
    }

    [HttpDelete("api/artifact-versions/{versionId:guid}")]
    public async Task<IActionResult> DeleteVersion(Guid versionId, CancellationToken cancellationToken) => OkOrBad(await artifacts.DeleteVersionAsync(versionId, cancellationToken));

    private IActionResult OkOrBad(Application.Common.Result result) => result.IsSuccess ? Ok(new { status = "OK" }) : Failure(result.Error);
    private IActionResult ToActionResult<T>(Application.Common.Result<T> result) => result.IsSuccess ? Ok(result.Value) : Failure(result.Error);

    private IActionResult Failure(string? error) => StatusCode(error switch
    {
        "Artifact not found." or "Artifact version not found." or "Project not found." => StatusCodes.Status404NotFound,
        "You are not allowed to create artifacts for this project." => StatusCodes.Status403Forbidden,
        _ => StatusCodes.Status400BadRequest
    }, new { error });

    private FileStreamResult PrivateFile(Stream content, string contentType, string fileName)
    {
        Response.Headers.CacheControl = "no-store, max-age=0";
        Response.Headers.Pragma = "no-cache";
        Response.Headers.Expires = "0";
        return File(content, contentType, fileName);
    }
}

public sealed class UploadArtifactVersionForm
{
    [Required]
    public IFormFile? File { get; set; }

    public string? ChangeNote { get; set; }
}
