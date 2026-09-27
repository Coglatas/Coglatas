using Coglatas.Application.Common;
using Coglatas.Application.Files;
using Coglatas.Application.Security.Redaction;
using Coglatas.Web.Models;
using Coglatas.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Coglatas.Web.Controllers;

[ApiController]
[Authorize]
public sealed class FilesController(
    IFileObjectService files,
    IFileSelectionSnapshotService selectionSnapshots,
    IFileSharingService sharing,
    IFileActivityService activity) : ControllerBase
{
    [HttpGet("api/files")]
    public async Task<IActionResult> List(
        [FromQuery] Guid workspaceId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        return ToFileMetadataActionResult(
            await files.ListFileObjectsAsync(workspaceId, page, pageSize, cancellationToken),
            "FileList");
    }

    [HttpPost("api/files")]
    [Consumes("multipart/form-data")]
    [EnableRateLimiting("file-upload")]
    public async Task<IActionResult> Upload([FromForm] UploadAttachmentForm form, CancellationToken cancellationToken)
    {
        if (form.File is null)
        {
            return BadRequest(ApiEnvelope.Error(
                HttpContext,
                StatusCodes.Status400BadRequest,
                "ValidationFailed",
                "File is required.",
                "file"));
        }

        await using var stream = form.File.OpenReadStream();
        return ToFileMetadataActionResult(await files.UploadAsync(new AttachmentUploadInput(
            form.OwnerType,
            form.OwnerId,
            form.File.FileName,
            form.File.ContentType,
            form.File.Length,
            stream), cancellationToken),
            "FileUpload");
    }

    [HttpGet("api/files/{fileObjectId:guid}")]
    public async Task<IActionResult> Get(Guid fileObjectId, CancellationToken cancellationToken)
    {
        return ToFileMetadataActionResult(
            await files.GetFileObjectAsync(fileObjectId, cancellationToken),
            "FileRead");
    }

    [HttpGet("api/files/{fileObjectId:guid}/activity")]
    public async Task<IActionResult> GetActivity(Guid fileObjectId, CancellationToken cancellationToken)
    {
        return ToFileMetadataActionResult(
            await activity.GetAsync(fileObjectId, cancellationToken),
            "FileActivity",
            "FileActivityFailed");
    }

    [HttpGet("api/files/{fileObjectId:guid}/versions/{versionId:guid}/content")]
    public async Task<IActionResult> ViewVersion(
        Guid fileObjectId,
        Guid versionId,
        CancellationToken cancellationToken)
    {
        var result = await activity.ViewVersionAsync(fileObjectId, versionId, cancellationToken);
        return result.IsSuccess
            ? PrivateFile(result.Value!.Content, result.Value.ContentType, result.Value.FileName)
            : BadRequest(CanonicalErrorEnvelope.FromResult(
                HttpContext,
                StatusCodes.Status400BadRequest,
                result.ErrorDetail,
                result.Error,
                "FileVersionViewFailed"));
    }

    [HttpGet("api/files/{fileObjectId:guid}/sharing")]
    public async Task<IActionResult> GetSharing(Guid fileObjectId, CancellationToken cancellationToken)
    {
        return ToFileMetadataActionResult(
            await sharing.GetAsync(fileObjectId, cancellationToken),
            "FileSharing");
    }

    [HttpPut("api/files/{fileObjectId:guid}/sharing")]
    public async Task<IActionResult> UpdateSharingPolicy(
        Guid fileObjectId,
        [FromBody] FileSharingPolicyUpdateRequest request,
        CancellationToken cancellationToken)
    {
        return ToFileMetadataActionResult(
            await sharing.UpdatePolicyAsync(fileObjectId, request, cancellationToken),
            "FileSharing");
    }

    [HttpPost("api/files/{fileObjectId:guid}/sharing/recipients")]
    public async Task<IActionResult> GrantSharingRecipient(
        Guid fileObjectId,
        [FromBody] FileShareGrantCreateRequest request,
        CancellationToken cancellationToken)
    {
        return ToFileMetadataActionResult(
            await sharing.GrantAsync(fileObjectId, request, cancellationToken),
            "FileSharing");
    }

    [HttpDelete("api/files/{fileObjectId:guid}/sharing/recipients/{grantId:guid}")]
    public async Task<IActionResult> RevokeSharingRecipient(
        Guid fileObjectId,
        Guid grantId,
        [FromQuery] long expectedSharingVersion,
        CancellationToken cancellationToken)
    {
        return ToFileMetadataActionResult(
            await sharing.RevokeAsync(fileObjectId, grantId, expectedSharingVersion, cancellationToken),
            "FileSharing");
    }

    [HttpGet("api/files/{fileObjectId:guid}/download")]
    public async Task<IActionResult> Download(Guid fileObjectId, CancellationToken cancellationToken)
    {
        var result = await files.DownloadFileObjectAsync(fileObjectId, cancellationToken);
        return result.IsSuccess
            ? PrivateFile(result.Value!.Content, result.Value.ContentType, result.Value.FileName)
            : BadRequest(CanonicalErrorEnvelope.FromResult(
                HttpContext,
                StatusCodes.Status400BadRequest,
                result.ErrorDetail,
                result.Error,
                "FileDownloadFailed"));
    }

    [HttpPost("api/files/{fileObjectId:guid}/download-grants")]
    public async Task<IActionResult> CreateDownloadGrant(
        Guid fileObjectId,
        [FromBody] FileDownloadGrantRequest? request,
        CancellationToken cancellationToken)
    {
        return ToDownloadGrantActionResult(await files.RequestFileObjectDownloadGrantAsync(
            fileObjectId,
            request ?? new FileDownloadGrantRequest(),
            cancellationToken));
    }

    [HttpPost("api/files/selection-snapshots")]
    [EnableRateLimiting("search")]
    public async Task<IActionResult> CaptureSelectionSnapshot(
        [FromQuery] FileSelectionSnapshotCreateRequest request,
        CancellationToken cancellationToken)
    {
        return ToFileMetadataActionResult(
            await selectionSnapshots.CaptureAsync(request, cancellationToken),
            "FileSelectionSnapshot",
            "FileSelectionSnapshotFailed");
    }

    [HttpPost("api/files/selection-snapshots/{selectionSnapshotId:guid}/delete")]
    public async Task<IActionResult> DeleteSelectionSnapshot(
        Guid selectionSnapshotId,
        CancellationToken cancellationToken)
    {
        return ToFileMetadataActionResult(
            await selectionSnapshots.DeleteAsync(selectionSnapshotId, cancellationToken),
            "FileSelectionSnapshot",
            "FileSelectionSnapshotDeleteFailed");
    }

    [HttpPost("api/file-download-grants/{fileDownloadGrantId:guid}/download")]
    public async Task<IActionResult> DownloadWithGrant(
        Guid fileDownloadGrantId,
        [FromBody] FileDownloadGrantTokenRequest request,
        CancellationToken cancellationToken)
    {
        var result = await files.DownloadFileObjectWithGrantAsync(fileDownloadGrantId, request.Token, cancellationToken);
        return result.IsSuccess
            ? PrivateFile(result.Value!.Content, result.Value.ContentType, result.Value.FileName)
            : BadRequest(CanonicalErrorEnvelope.FromResult(
                HttpContext,
                StatusCodes.Status400BadRequest,
                result.ErrorDetail,
                result.Error,
                "FileDownloadFailed"));
    }

    [HttpDelete("api/files/{fileObjectId:guid}")]
    public async Task<IActionResult> Delete(Guid fileObjectId, [FromQuery] string? reason, CancellationToken cancellationToken)
    {
        return OkOrBad(await files.DeleteFileObjectAsync(fileObjectId, reason, cancellationToken));
    }

    private IActionResult OkOrBad(Result result) =>
        result.IsSuccess
            ? Ok(new { status = "OK" })
            : BadRequest(CanonicalErrorEnvelope.FromResult(
                HttpContext,
                StatusCodes.Status400BadRequest,
                result.ErrorDetail,
                result.Error,
                "FileOperationFailed"));

    private IActionResult ToDownloadGrantActionResult<T>(Result<T> result) =>
        result.IsSuccess
            ? Ok(CanonicalRedactionProjection.Apply(
                HttpContext,
                result.Value!,
                RedactionProfile.UiDetail,
                "FileDownloadGrant",
                RedactionAuthorizationState.Allowed,
                RedactionPurpose.FileDownload))
            : BadRequest(CanonicalErrorEnvelope.FromResult(
                HttpContext,
                StatusCodes.Status400BadRequest,
                result.ErrorDetail,
                result.Error,
                "FileGrantFailed"));

    private IActionResult ToFileMetadataActionResult<T>(
        Result<T> result,
        string moduleKey,
        string failureCode = "FileMetadataFailed") =>
        result.IsSuccess
            ? Ok(CanonicalRedactionProjection.Apply(
                HttpContext,
                result.Value!,
                RedactionProfile.FileMetadata,
                moduleKey,
                RedactionAuthorizationState.Allowed))
            : BadRequest(CanonicalErrorEnvelope.FromResult(
                HttpContext,
                StatusCodes.Status400BadRequest,
                result.ErrorDetail,
                result.Error,
                failureCode));

    private FileStreamResult PrivateFile(Stream content, string contentType, string fileName)
    {
        Response.Headers.CacheControl = "no-store, max-age=0";
        Response.Headers.Pragma = "no-cache";
        Response.Headers.Expires = "0";
        return File(content, contentType, fileName);
    }
}
