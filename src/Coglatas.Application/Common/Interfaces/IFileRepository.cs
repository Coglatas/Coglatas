using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;

namespace Coglatas.Application.Common.Interfaces;

public sealed record FileOwnerContext(
    Guid WorkspaceId,
    Guid? ProjectId = null,
    Guid? ConversationId = null,
    Guid? ChannelId = null,
    Guid? AuthorUserId = null);

/// <summary>
/// Immutable File-version projection. StorageKey stays inside the application
/// boundary and is never serialized by the Files Activity API.
/// </summary>
public sealed record FileVersionRecord(
    Guid Id,
    Guid FileObjectId,
    int VersionNumber,
    string OriginalFileName,
    string StorageKey,
    string ContentType,
    long SizeBytes,
    string? HashSha256,
    Guid CreatedByUserId,
    string CreatedByDisplayName,
    DateTimeOffset CreatedAt);

/// <summary>
/// The only generic-audit subset the Files Activity projection may consume.
/// Callers must allow-list fields from MetadataJson before returning anything
/// to the browser.
/// </summary>
public sealed record FileSharingActivityRecord(
    Guid Id,
    string ActorDisplayName,
    DateTimeOffset OccurredAt,
    string? MetadataJson);

public interface IFileRepository
{
    Task<FileObject?> GetFileObjectAsync(Guid fileObjectId, CancellationToken cancellationToken = default);

    Task<PagedResponse<Attachment>> ListWorkspaceFileObjectsAsync(
        Guid workspaceId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the Workspace-owned inventory after applying the server-side
    /// File sharing boundary. Implementations that have not yet adopted the
    /// sharing projection preserve their legacy behavior for test doubles;
    /// the production repository must override this method.
    /// </summary>
    Task<PagedResponse<Attachment>> ListAccessibleWorkspaceFileObjectsAsync(
        Guid workspaceId,
        Guid userId,
        bool canManageSharing,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        ListWorkspaceFileObjectsAsync(workspaceId, page, pageSize, cancellationToken);

    Task AddFileObjectAsync(FileObject fileObject, CancellationToken cancellationToken = default);

    Task<Attachment?> GetAttachmentAsync(Guid attachmentId, CancellationToken cancellationToken = default);

    Task<Attachment?> GetAttachmentByFileObjectAsync(Guid fileObjectId, CancellationToken cancellationToken = default);

    Task AddAttachmentAsync(Attachment attachment, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FileVersionRecord>> ListFileVersionsAsync(
        Guid tenantId,
        Guid fileObjectId,
        int limit,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<FileVersionRecord>>([]);

    Task<FileVersionRecord?> GetFileVersionAsync(
        Guid tenantId,
        Guid fileObjectId,
        Guid versionId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<FileVersionRecord?>(null);

    Task<IReadOnlyList<FileSharingActivityRecord>> ListFileSharingActivityAsync(
        Guid tenantId,
        Guid fileObjectId,
        int limit,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<FileSharingActivityRecord>>([]);

    Task<IReadOnlyList<Attachment>> ListTaskAttachmentsAsync(Guid taskItemId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Attachment>>([]);
    Task<PagedResponse<Attachment>> ListTaskAttachmentsPageAsync(Guid taskItemId, int page, int pageSize, CancellationToken cancellationToken = default) => Task.FromResult(new PagedResponse<Attachment>([], page, pageSize, 0));

    void RemoveAttachment(Attachment attachment) { }

    Task<FileOwnerContext?> ResolveOwnerAsync(AttachmentOwnerType ownerType, Guid ownerId, CancellationToken cancellationToken = default);
}
