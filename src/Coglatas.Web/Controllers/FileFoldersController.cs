using Coglatas.Application.Common;
using Coglatas.Application.Files;
using Coglatas.Application.Security.Redaction;
using Coglatas.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Coglatas.Web.Controllers;

[ApiController]
[Authorize]
public sealed class FileFoldersController(IFileFolderService folders) : ControllerBase
{
    [HttpGet("api/file-folders")]
    public async Task<IActionResult> List(
        [FromQuery] Guid workspaceId,
        CancellationToken cancellationToken = default) =>
        ToActionResult(await folders.ListAsync(workspaceId, cancellationToken), "FileFolderList");

    [HttpPost("api/file-folders")]
    public async Task<IActionResult> Create(
        [FromBody] FileFolderCreateRequest request,
        CancellationToken cancellationToken = default) =>
        ToActionResult(await folders.CreateAsync(request, cancellationToken), "FileFolderCreate");

    [HttpPost("api/file-folders/{folderId:guid}/move")]
    public async Task<IActionResult> MoveFolder(
        Guid folderId,
        [FromBody] FileFolderMoveRequest request,
        CancellationToken cancellationToken = default) =>
        ToActionResult(await folders.MoveFolderAsync(folderId, request, cancellationToken), "FileFolderMove");

    [HttpGet("api/files/{fileObjectId:guid}/location")]
    public async Task<IActionResult> GetFileLocation(
        Guid fileObjectId,
        CancellationToken cancellationToken = default) =>
        ToActionResult(await folders.GetFileLocationAsync(fileObjectId, cancellationToken), "FileLocation");

    [HttpPost("api/files/{fileObjectId:guid}/move")]
    public async Task<IActionResult> MoveFile(
        Guid fileObjectId,
        [FromBody] FileMoveRequest request,
        CancellationToken cancellationToken = default) =>
        ToActionResult(await folders.MoveFileAsync(fileObjectId, request, cancellationToken), "FileMove");

    private IActionResult ToActionResult<T>(Result<T> result, string moduleKey) =>
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
                "FileFolderOperationFailed"));
}
