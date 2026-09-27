using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Files;
using Coglatas.Application.Search;
using Coglatas.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Infrastructure.Persistence;

/// <summary>
/// Server-owned immutable Workspace File selections. The snapshot is bounded
/// before persistence and is consumed by the first batch mutation attempt.
/// </summary>
public sealed class FileSelectionSnapshotService(
    AppDbContext dbContext,
    ICurrentUser currentUser,
    ICurrentTenant currentTenant,
    IClock clock,
    IFileAuthorizationService authorization,
    IFileObjectService files) : IFileSelectionSnapshotService
{
    private const int MaximumSelectionCount = 100;
    private static readonly TimeSpan SnapshotLifetime = TimeSpan.FromMinutes(5);

    public async Task<Result<FileSelectionSnapshotCaptureResponse>> CaptureAsync(
        FileSelectionSnapshotCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TryCurrentActor(out var actorUserId) || request.WorkspaceId == Guid.Empty)
        {
            return Result<FileSelectionSnapshotCaptureResponse>.Failure("The file selection is unavailable.");
        }

        if (!Enum.IsDefined(request.FileKind))
        {
            return Result<FileSelectionSnapshotCaptureResponse>.Failure("The file selection is unavailable.");
        }

        var normalizedQuery = request.Q?.Trim() ?? string.Empty;
        if (normalizedQuery.Length == 0 &&
            request is { FileKind: FileSearchKind.All, FromDate: null, OnlyMyUploads: false })
        {
            return Result<FileSelectionSnapshotCaptureResponse>.Failure("Choose a search or filter before selecting all results.");
        }

        if (!await authorization.CanViewWorkspaceFiles(actorUserId, request.WorkspaceId, cancellationToken))
        {
            return Result<FileSelectionSnapshotCaptureResponse>.Failure("The file selection is unavailable.");
        }

        var fileObjectIds = await MatchingWorkspaceFiles(
                actorUserId,
                request.WorkspaceId,
                normalizedQuery,
                request.FileKind,
                request.FromDate,
                request.OnlyMyUploads)
            // One FileObject can retain more than one historical attachment
            // association. Materialize a FileObject identity once before
            // applying the snapshot limit; PostgreSQL requires DISTINCT order
            // terms to be projected, so group first instead of DISTINCT after
            // an attachment ordering.
            .GroupBy(attachment => attachment.FileObjectId)
            .Select(group => new
            {
                FileObjectId = group.Key,
                CreatedAt = group.Max(attachment => attachment.FileObject!.CreatedAt),
            })
            .OrderByDescending(file => file.CreatedAt)
            .ThenBy(file => file.FileObjectId)
            .Select(file => file.FileObjectId)
            .Take(MaximumSelectionCount + 1)
            .ToArrayAsync(cancellationToken);

        if (fileObjectIds.Length > MaximumSelectionCount)
        {
            return Result<FileSelectionSnapshotCaptureResponse>.Success(new(
                "Overflow",
                null,
                0,
                MaximumSelectionCount,
                null));
        }

        if (fileObjectIds.Length == 0)
        {
            return Result<FileSelectionSnapshotCaptureResponse>.Success(new(
                "Empty",
                null,
                0,
                MaximumSelectionCount,
                null));
        }

        var now = clock.UtcNow;
        var snapshot = new FileSelectionSnapshot
        {
            TenantId = currentTenant.TenantId,
            ActorUserId = actorUserId,
            WorkspaceId = request.WorkspaceId,
            NormalizedQuery = normalizedQuery,
            FileKind = request.FileKind.ToString(),
            FromDateUtc = request.FromDate,
            OnlyMyUploads = request.OnlyMyUploads,
            ExpiresAt = now.Add(SnapshotLifetime),
        };
        foreach (var fileObjectId in fileObjectIds)
        {
            snapshot.Items.Add(new FileSelectionSnapshotItem
            {
                SelectionSnapshotId = snapshot.Id,
                FileObjectId = fileObjectId,
            });
        }

        await dbContext.FileSelectionSnapshots.AddAsync(snapshot, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        return Result<FileSelectionSnapshotCaptureResponse>.Success(new(
            "Captured",
            snapshot.Id,
            snapshot.Items.Count,
            MaximumSelectionCount,
            snapshot.ExpiresAt));
    }

    public async Task<Result<FileSelectionSnapshotDeleteResponse>> DeleteAsync(
        Guid selectionSnapshotId,
        CancellationToken cancellationToken = default)
    {
        if (!TryCurrentActor(out var actorUserId) || selectionSnapshotId == Guid.Empty)
        {
            return Result<FileSelectionSnapshotDeleteResponse>.Failure("The saved file selection is unavailable.");
        }

        var now = clock.UtcNow;
        var snapshot = await dbContext.FileSelectionSnapshots
            .Include(candidate => candidate.Items)
            .SingleOrDefaultAsync(candidate =>
                candidate.Id == selectionSnapshotId &&
                candidate.ActorUserId == actorUserId,
                cancellationToken);
        if (snapshot is null || snapshot.ExpiresAt <= now || snapshot.ConsumedAt.HasValue)
        {
            return Result<FileSelectionSnapshotDeleteResponse>.Failure("The saved file selection is no longer available. Select the search results again.");
        }

        // One token is consumed by exactly one command attempt. The optimistic
        // version prevents a second request from replaying the same selection.
        snapshot.ConsumedAt = now;
        snapshot.ConsumptionVersion++;
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result<FileSelectionSnapshotDeleteResponse>.Failure("The saved file selection is no longer available. Select the search results again.");
        }

        var results = new List<FileSelectionSnapshotDeleteItemResponse>(snapshot.Items.Count);
        foreach (var item in snapshot.Items.OrderBy(candidate => candidate.FileObjectId))
        {
            try
            {
                // FileService re-loads the resource and reauthorizes this actor
                // at execution. A snapshot identity is never a mutation grant.
                var deletion = await files.DeleteFileObjectAsync(item.FileObjectId, cancellationToken: cancellationToken);
                results.Add(new(
                    item.FileObjectId,
                    deletion.IsSuccess,
                    deletion.IsSuccess ? "Deleted" : "NotDeleted"));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Preserve the bounded per-item outcome without disclosing a
                // resource existence, storage, or authorization detail.
                results.Add(new(item.FileObjectId, false, "NotDeleted"));
            }
        }

        var succeededCount = results.Count(result => result.Succeeded);
        return Result<FileSelectionSnapshotDeleteResponse>.Success(new(
            snapshot.Id,
            results.Count,
            succeededCount,
            results.Count - succeededCount,
            results));
    }

    private bool TryCurrentActor(out Guid actorUserId)
    {
        actorUserId = currentUser.UserId ?? Guid.Empty;
        return currentUser.IsAuthenticated &&
            actorUserId != Guid.Empty &&
            currentTenant is { IsAvailable: true, IsPlatformScope: false };
    }

    private IQueryable<Attachment> MatchingWorkspaceFiles(
        Guid actorUserId,
        Guid workspaceId,
        string normalizedQuery,
        FileSearchKind fileKind,
        DateTimeOffset? fromDate,
        bool onlyMyUploads)
    {
        var query = FileAttachmentQueryFilters.WorkspaceFiles(dbContext, workspaceId);

        if (normalizedQuery.Length > 0)
        {
            query = query.Where(attachment =>
                EF.Functions.ILike(attachment.FileObject!.OriginalFileName, $"%{normalizedQuery}%"));
        }

        if (onlyMyUploads)
        {
            query = query.Where(attachment => attachment.FileObject!.UploadedByUserId == actorUserId);
        }

        query = FileAttachmentQueryFilters.ApplyKind(query, fileKind);
        return query.Where(attachment =>
            !fromDate.HasValue ||
            (attachment.FileObject!.UpdatedAt ?? attachment.FileObject.CreatedAt) >= fromDate.Value);
    }

}
