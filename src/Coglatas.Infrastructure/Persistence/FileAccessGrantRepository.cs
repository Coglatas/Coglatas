using Coglatas.Application.Files;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Infrastructure.Persistence;

/// <summary>
/// PostgreSQL/EF projection for File access grants. A persisted row is not an
/// access capability by itself: every effective-read query joins the current
/// tenant, user, Workspace, and (for external recipients) Project boundary.
/// </summary>
public sealed class FileAccessGrantRepository(AppDbContext dbContext) : IFileAccessGrantRepository
{
    public Task<Attachment?> GetWorkspaceAttachmentAsync(
        Guid fileObjectId,
        CancellationToken cancellationToken = default)
    {
        return dbContext.Attachments
            .Include(attachment => attachment.FileObject)
            .FirstOrDefaultAsync(attachment =>
                attachment.FileObjectId == fileObjectId &&
                attachment.OwnerType == AttachmentOwnerType.Workspace &&
                attachment.OwnerId == attachment.WorkspaceId &&
                attachment.DeletedAt == null &&
                attachment.FileObject != null &&
                attachment.FileObject.WorkspaceId == attachment.WorkspaceId &&
                attachment.FileObject.DeletedAt == null &&
                attachment.FileObject.Status != FileObjectStatus.Deleted,
                cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, FileAccessGrantSummary>> GetEffectiveSummariesAsync(
        IReadOnlyCollection<Guid> fileObjectIds,
        CancellationToken cancellationToken = default)
    {
        var ids = fileObjectIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return new Dictionary<Guid, FileAccessGrantSummary>();
        }

        var summaries = await EffectiveFileAccessGrantQuery.For(dbContext)
            .Where(grant => Enumerable.Contains(ids, grant.FileObjectId))
            .GroupBy(grant => grant.FileObjectId)
            .Select(group => new
            {
                FileObjectId = group.Key,
                InternalRecipientCount = group.Count(grant =>
                    grant.RecipientKind == FileAccessGrantRecipientKind.WorkspaceMember),
                ExternalRecipientCount = group.Count(grant =>
                    grant.RecipientKind == FileAccessGrantRecipientKind.ExternalProjectMember)
            })
            .ToListAsync(cancellationToken);

        return summaries.ToDictionary(
            summary => summary.FileObjectId,
            summary => new FileAccessGrantSummary(
                summary.InternalRecipientCount,
                summary.ExternalRecipientCount));
    }

    public Task<bool> HasEffectiveGrantAsync(
        Guid fileObjectId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        return EffectiveFileAccessGrantQuery.For(dbContext).AnyAsync(grant =>
            grant.FileObjectId == fileObjectId &&
            grant.RecipientUserId == userId,
            cancellationToken);
    }

    public async Task<IReadOnlyList<FileAccessGrantRecipient>> ListEffectiveRecipientsAsync(
        Guid fileObjectId,
        CancellationToken cancellationToken = default)
    {
        return await EffectiveFileAccessGrantQuery.For(dbContext)
            .Where(grant => grant.FileObjectId == fileObjectId)
            .OrderBy(grant => grant.RecipientUser!.DisplayName)
            .ThenBy(grant => grant.RecipientUserId)
            .Select(grant => new FileAccessGrantRecipient(
                grant.Id,
                grant.RecipientUserId,
                grant.RecipientUser!.DisplayName,
                grant.RecipientKind))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<FileAccessGrantCandidate>> ListEligibleRecipientsAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default)
    {
        var internalCandidates = await dbContext.WorkspaceMembers
            .AsNoTracking()
            .Where(member =>
                member.WorkspaceId == workspaceId &&
                member.Status == MembershipStatus.Active &&
                dbContext.TenantUsers.Any(tenantUser =>
                    tenantUser.TenantId == member.TenantId &&
                    tenantUser.UserId == member.UserId &&
                    tenantUser.Status == TenantUserStatus.Active) &&
                dbContext.Users.Any(user =>
                    user.Id == member.UserId &&
                    user.Status == UserStatus.Active &&
                    user.DeletedAt == null))
            .Select(member => new FileAccessGrantCandidate(
                member.UserId,
                member.User!.DisplayName,
                FileAccessGrantRecipientKind.WorkspaceMember))
            .ToListAsync(cancellationToken);

        var externalCandidates = await dbContext.ProjectMembers
            .AsNoTracking()
            .Where(member =>
                dbContext.Projects.Any(project =>
                    project.Id == member.ProjectId &&
                    project.TenantId == member.TenantId &&
                    project.WorkspaceId == workspaceId &&
                    project.DeletedAt == null &&
                    project.Status != ProjectStatus.Archived &&
                    project.Status != ProjectStatus.Deleted) &&
                !dbContext.WorkspaceMembers.Any(workspaceMember =>
                    workspaceMember.TenantId == member.TenantId &&
                    workspaceMember.WorkspaceId == workspaceId &&
                    workspaceMember.UserId == member.UserId &&
                    workspaceMember.Status == MembershipStatus.Active) &&
                dbContext.TenantUsers.Any(tenantUser =>
                    tenantUser.TenantId == member.TenantId &&
                    tenantUser.UserId == member.UserId &&
                    tenantUser.Status == TenantUserStatus.Active) &&
                dbContext.Users.Any(user =>
                    user.Id == member.UserId &&
                    user.Status == UserStatus.Active &&
                    user.DeletedAt == null))
            .Select(member => new FileAccessGrantCandidate(
                member.UserId,
                member.User!.DisplayName,
                FileAccessGrantRecipientKind.ExternalProjectMember))
            .ToListAsync(cancellationToken);

        return internalCandidates
            .Concat(externalCandidates)
            .GroupBy(candidate => candidate.UserId)
            // An active Workspace membership is always the authoritative
            // classification if a user is represented in both scopes.
            .Select(group => group
                .OrderBy(candidate => candidate.RecipientKind == FileAccessGrantRecipientKind.WorkspaceMember ? 0 : 1)
                .ThenBy(candidate => candidate.DisplayName, StringComparer.Ordinal)
                .First())
            .OrderBy(candidate => candidate.DisplayName, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.UserId)
            .ToList();
    }

    public async Task<FileAccessGrantCandidate?> FindEligibleRecipientAsync(
        Guid workspaceId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var candidates = await ListEligibleRecipientsAsync(workspaceId, cancellationToken);
        return candidates.FirstOrDefault(candidate => candidate.UserId == userId);
    }

    public Task<FileAccessGrant?> GetActiveGrantAsync(
        Guid fileObjectId,
        Guid grantId,
        CancellationToken cancellationToken = default)
    {
        return dbContext.FileAccessGrants.FirstOrDefaultAsync(grant =>
            grant.Id == grantId &&
            grant.FileObjectId == fileObjectId &&
            grant.RevokedAt == null,
            cancellationToken);
    }

    public Task<FileAccessGrant?> GetActiveGrantForRecipientAsync(
        Guid fileObjectId,
        Guid recipientUserId,
        CancellationToken cancellationToken = default)
    {
        return dbContext.FileAccessGrants.FirstOrDefaultAsync(grant =>
            grant.FileObjectId == fileObjectId &&
            grant.RecipientUserId == recipientUserId &&
            grant.RevokedAt == null,
            cancellationToken);
    }

    public Task AddAsync(FileAccessGrant grant, CancellationToken cancellationToken = default) =>
        dbContext.FileAccessGrants.AddAsync(grant, cancellationToken).AsTask();

}
