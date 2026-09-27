using System.ComponentModel.DataAnnotations;
using Coglatas.Application.Files;
using Coglatas.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Coglatas.Web.Controllers;

[ApiController]
[Authorize]
public sealed class AttachmentsController(IFileService files) : ApiResultControllerBase
{
    [HttpPost("api/attachments")]
    [Consumes("multipart/form-data")]
    [EnableRateLimiting("file-upload")]
    public async Task<IActionResult> Upload([FromForm] UploadAttachmentForm form, CancellationToken cancellationToken)
    {
        if (form.File is null)
        {
            return BadRequest(new { error = "File is required." });
        }

        await using var stream = form.File.OpenReadStream();
        return ToActionResult(await files.UploadAsync(new AttachmentUploadInput(
            form.OwnerType,
            form.OwnerId,
            form.File.FileName,
            form.File.ContentType,
            form.File.Length,
            stream), cancellationToken));
    }

    [HttpGet("api/attachments/{attachmentId:guid}")]
    public async Task<IActionResult> Get(Guid attachmentId, CancellationToken cancellationToken) => ToActionResult(await files.GetAsync(attachmentId, cancellationToken));

    [HttpGet("api/attachments/{attachmentId:guid}/download")]
    public async Task<IActionResult> Download(Guid attachmentId, CancellationToken cancellationToken)
    {
        var result = await files.DownloadAsync(attachmentId, cancellationToken);
        return result.IsSuccess
            ? PrivateFile(result.Value!.Content, result.Value.ContentType, result.Value.FileName)
            : BadRequest(new { error = result.Error });
    }

    [HttpPost("api/attachments/{attachmentId:guid}/download-grants")]
    public async Task<IActionResult> CreateDownloadGrant(
        Guid attachmentId,
        [FromBody] FileDownloadGrantRequest? request,
        CancellationToken cancellationToken)
    {
        var result = await files.RequestDownloadGrantAsync(
            attachmentId,
            request ?? new FileDownloadGrantRequest(),
            cancellationToken);
        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        // Grant issuance uses the same safe not-found shape for missing and
        // no-longer-authorized attachments.
        return NotFound(new
        {
            requestId = HttpContext.TraceIdentifier,
            error = new
            {
                code = "FILE_DOWNLOAD_GRANT_NOT_FOUND",
                message = "Attachment not found.",
                target = (string?)null,
                details = Array.Empty<object>(),
                redactionApplied = true
            }
        });
    }

    [HttpPost("api/attachment-download-grants/{fileDownloadGrantId:guid}/download")]
    public async Task<IActionResult> DownloadWithGrant(
        Guid fileDownloadGrantId,
        [FromBody] FileDownloadGrantTokenRequest request,
        CancellationToken cancellationToken)
    {
        var result = await files.DownloadWithGrantAsync(fileDownloadGrantId, request.Token, cancellationToken);
        return result.IsSuccess
            ? PrivateFile(result.Value!.Content, result.Value.ContentType, result.Value.FileName)
            : BadRequest(new { error = result.Error });
    }

    [HttpDelete("api/attachments/{attachmentId:guid}")]
    public async Task<IActionResult> Delete(Guid attachmentId, CancellationToken cancellationToken) => OkOrBad(await files.DeleteAsync(attachmentId, cancellationToken));

    private FileStreamResult PrivateFile(Stream content, string contentType, string fileName)
    {
        Response.Headers.CacheControl = "no-store, max-age=0";
        Response.Headers.Pragma = "no-cache";
        Response.Headers.Expires = "0";
        return File(content, contentType, fileName);
    }
}

public sealed class UploadAttachmentForm
{
    // ASP.NET Core form model binding assigns these setters via reflection.
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    public AttachmentOwnerType OwnerType { get; set; }

    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    public Guid OwnerId { get; set; }

    [Required]
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    public IFormFile? File { get; set; }
}
