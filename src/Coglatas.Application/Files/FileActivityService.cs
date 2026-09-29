using System.Text.Json;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;

namespace Coglatas.Application.Files;

public sealed record FileActivityVersionResponse(
    Guid VersionId,
    int VersionNumber,
    string FileName,
    string ContentType,
    long SizeBytes,
    DateTimeOffset CreatedAt,
    bool IsCurrent,
    string ViewPath);

public sealed record FileActivitySharingResponse(
    string Change,
    string AccessState,
    long? SharingVersion);

public sealed record FileActivityEntryResponse(
    Guid Id,
    string Kind,
    string ActorDisplayName,
    DateTimeOffset OccurredAt,
    FileActivityVersionResponse? Version = null,
    FileActivitySharingResponse? Sharing = null);

public sealed record FileActivityResponse(
    Guid FileObjectId,
    IReadOnlyList<FileActivityEntryResponse> Items);

public interface IFileActivityService
{
    Task<Result<FileActivityResponse>> GetAsync(
        Guid fileObjectId,
        CancellationToken cancellationToken = default);

    Task<Result<FileDownloadResponse>> ViewVersionAsync(
        Guid fileObjectId,
        Guid versionId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Produces the bounded, File-specific Activity projection used by the Files
/// inspector. Generic Audit rows never cross this boundary: only immutable
/// version metadata and an allow-listed subset of FileSharingChanged metadata
/// are returned to the browser.
/// </summary>
public sealed class FileActivityService(
    IFileRepository files,
    IFileAccessGrantRepository accessGrants,
    IFileAuthorizationService authorization,
    IFileStorageService storage,
    ICurrentUser currentUser,
    ICurrentTenant currentTenant) : IFileActivityService
{
    private const int MaxActivityEntries = 100;

    public async Task<Result<FileActivityResponse>> GetAsync(
        Guid fileObjectId,
        CancellationToken cancellationToken = default)
    {
        var authorized = await AuthorizedWorkspaceAttachmentAsync(
            fileObjectId,
            requireDownload: false,
            cancellationToken);
        if (authorized is null)
        {
            return NotFound<FileActivityResponse>();
        }

        var versions = await files.ListFileVersionsAsync(
            currentTenant.TenantId,
            fileObjectId,
            MaxActivityEntries,
            cancellationToken);
        var sharing = await files.ListFileSharingActivityAsync(
            currentTenant.TenantId,
            fileObjectId,
            MaxActivityEntries,
            cancellationToken);

        var currentVersionNumber = versions.Count == 0
            ? 0
            : versions.Max(version => version.VersionNumber);
        var entries = new List<FileActivityEntryResponse>(versions.Count + sharing.Count);

        foreach (var version in versions)
        {
            entries.Add(new FileActivityEntryResponse(
                version.Id,
                version.VersionNumber == 1 ? "uploaded" : "versionCreated",
                NormalizeActor(version.CreatedByDisplayName),
                version.CreatedAt,
                new FileActivityVersionResponse(
                    version.Id,
                    version.VersionNumber,
                    version.OriginalFileName,
                    version.ContentType,
                    version.SizeBytes,
                    version.CreatedAt,
                    version.VersionNumber == currentVersionNumber,
                    $"/api/files/{fileObjectId:D}/versions/{version.Id:D}/content")));
        }

        foreach (var change in sharing)
        {
            entries.Add(new FileActivityEntryResponse(
                change.Id,
                "sharingChanged",
                NormalizeActor(change.ActorDisplayName),
                change.OccurredAt,
                Sharing: ProjectSharing(change.MetadataJson)));
        }

        var ordered = entries
            .OrderByDescending(entry => entry.OccurredAt)
            .ThenByDescending(entry => entry.Id)
            .Take(MaxActivityEntries)
            .ToList();
        return Result<FileActivityResponse>.Success(new FileActivityResponse(fileObjectId, ordered));
    }

    public async Task<Result<FileDownloadResponse>> ViewVersionAsync(
        Guid fileObjectId,
        Guid versionId,
        CancellationToken cancellationToken = default)
    {
        if (versionId == Guid.Empty)
        {
            return NotFound<FileDownloadResponse>();
        }

        var authorized = await AuthorizedWorkspaceAttachmentAsync(
            fileObjectId,
            requireDownload: true,
            cancellationToken);
        if (authorized is null)
        {
            return NotFound<FileDownloadResponse>();
        }

        var version = await files.GetFileVersionAsync(
            currentTenant.TenantId,
            fileObjectId,
            versionId,
            cancellationToken);
        if (version is null)
        {
            return NotFound<FileDownloadResponse>();
        }

        // The version row contains only an application-generated storage key.
        // Authorization is re-evaluated immediately before the historical blob
        // is opened, so a stale Activity response is never a capability.
        var content = await storage.OpenReadAsync(version.StorageKey, cancellationToken);
        return Result<FileDownloadResponse>.Success(new FileDownloadResponse(
            content,
            version.OriginalFileName,
            version.ContentType,
            version.SizeBytes));
    }

    private async Task<Coglatas.Domain.Entities.Attachment?> AuthorizedWorkspaceAttachmentAsync(
        Guid fileObjectId,
        bool requireDownload,
        CancellationToken cancellationToken)
    {
        if (fileObjectId == Guid.Empty ||
            !currentTenant.IsAvailable ||
            !currentUser.IsAuthenticated ||
            currentUser.UserId is not Guid actorUserId ||
            actorUserId == Guid.Empty)
        {
            return null;
        }

        // Files inventory Activity intentionally shares the direct-Workspace
        // boundary used by FileSharingService instead of discovering an
        // arbitrary Task/Message association for the same canonical File.
        var attachment = await accessGrants.GetWorkspaceAttachmentAsync(fileObjectId, cancellationToken);
        if (attachment?.FileObject is null ||
            attachment.TenantId != currentTenant.TenantId ||
            attachment.FileObject.TenantId != currentTenant.TenantId ||
            attachment.FileObject.WorkspaceId != attachment.WorkspaceId)
        {
            return null;
        }

        var allowed = requireDownload
            ? await authorization.CanDownloadAttachment(actorUserId, attachment, cancellationToken)
            : await authorization.CanViewAttachment(actorUserId, attachment, cancellationToken);
        return allowed ? attachment : null;
    }

    private static FileActivitySharingResponse ProjectSharing(string? metadataJson)
    {
        string change = "changed";
        string accessState = "unavailable";
        long? sharingVersion = null;

        if (!string.IsNullOrWhiteSpace(metadataJson))
        {
            try
            {
                using var document = JsonDocument.Parse(metadataJson);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (document.RootElement.TryGetProperty("change", out var changeElement) &&
                        changeElement.ValueKind == JsonValueKind.String)
                    {
                        change = changeElement.GetString() switch
                        {
                            "policyChanged" => "policyChanged",
                            "recipientGranted" => "recipientGranted",
                            "recipientRevoked" => "recipientRevoked",
                            _ => "changed"
                        };
                    }

                    if (document.RootElement.TryGetProperty("accessState", out var stateElement) &&
                        stateElement.ValueKind == JsonValueKind.String)
                    {
                        accessState = stateElement.GetString() switch
                        {
                            "Private" => "private",
                            "Workspace" => "workspace",
                            _ => "unavailable"
                        };
                    }

                    if (document.RootElement.TryGetProperty("sharingVersion", out var versionElement) &&
                        versionElement.TryGetInt64(out var projectedVersion) &&
                        projectedVersion > 0)
                    {
                        sharingVersion = projectedVersion;
                    }
                }
            }
            catch (JsonException)
            {
                // Malformed or legacy metadata is represented conservatively.
                // The raw JSON is never returned to the caller.
            }
        }

        return new FileActivitySharingResponse(change, accessState, sharingVersion);
    }

    private static string NormalizeActor(string? displayName) =>
        string.IsNullOrWhiteSpace(displayName) ? "Unknown user" : displayName.Trim();

    private static Result<T> NotFound<T>() =>
        Result<T>.Failure(new ApplicationErrorDetail("FILE_NOT_FOUND", "File not found."));
}
