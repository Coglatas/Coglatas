using Coglatas.Application.Audit;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Tenancy;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Infrastructure.Persistence;

/// <summary>
/// Sends reference-only review mentions for an Audit Finding. Mention targets are
/// revalidated independently from the UI owner list so a stale client cannot use
/// this endpoint as a tenant-directory or unauthorized notification channel.
/// </summary>
public sealed class DbAuditFindingReviewerMentionsService(
    AppDbContext dbContext,
    IAuditClaimsEvidenceService claimsEvidence,
    IAuditAuthorizationService auditAuthorization,
    ICapabilityGrantEvaluator capabilityGrants,
    ICurrentUser currentUser,
    INotificationService notifications,
    IAuditLogger auditLogger,
    IUnitOfWork unitOfWork) : IAuditFindingReviewerMentionsService
{
    public async Task<Result> MentionAsync(
        Guid findingId,
        MentionAuditFindingReviewerRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!currentUser.IsAuthenticated || !currentUser.UserId.HasValue)
        {
            return Result.Failure(new ApplicationErrorDetail("AuthenticationRequired", "Authentication is required."));
        }

        var reviewAuthorization = await auditAuthorization.AuthorizeAsync(
            CapabilityKeys.AuditReview,
            "audit.findings.mention",
            cancellationToken);
        if (!reviewAuthorization.IsSuccess)
        {
            return reviewAuthorization;
        }

        if (findingId == Guid.Empty || request.UserId == Guid.Empty || request.RequestId == Guid.Empty)
        {
            return Result.Failure(new ApplicationErrorDetail(
                "ValidationFailed",
                "Finding ID, reviewer ID, and request ID are required."));
        }

        var finding = await dbContext.Set<ArtifactFinding>()
            .AsNoTracking()
            .Include(item => item.ArtifactClaim)
            .SingleOrDefaultAsync(item => item.Id == findingId, cancellationToken);
        if (finding?.ArtifactClaim is null || finding.ArtifactClaim.TenantId != finding.TenantId)
        {
            return FindingNotFound();
        }

        var claimsResult = await claimsEvidence.GetAsync(finding.ArtifactClaim.ArtifactVersionId, cancellationToken);
        if (!claimsResult.IsSuccess || claimsResult.Value is null ||
            claimsResult.Value.Claims.All(claim => claim.ClaimId != finding.ArtifactClaimId))
        {
            if (claimsResult.ErrorDetail?.Code is "AuthenticationRequired" or "CapabilityDenied" or "TenantMembershipRequired")
            {
                return claimsResult.ErrorDetail is not null
                    ? Result.Failure(claimsResult.ErrorDetail)
                    : Result.Failure(claimsResult.Error ?? "Audit view is not permitted.");
            }
            return FindingNotFound();
        }

        var recipient = await LoadRecipientAsync(finding.TenantId, request.UserId, cancellationToken);
        if (recipient is null ||
            !await HasAuditReviewAuthorityAsync(finding.TenantId, recipient, cancellationToken))
        {
            return Result.Failure(new ApplicationErrorDetail(
                "MentionTargetNotEligible",
                "The selected reviewer is not available for Audit review in the current tenant."));
        }

        var actorUserId = currentUser.UserId.Value;
        await auditLogger.LogAsync(new AuditLogEntry(
            actorUserId,
            "AuditFindingReviewerMentioned",
            "ArtifactFinding",
            finding.Id,
            "Audit finding reviewer mention requested.",
            TenantId: finding.TenantId,
            Metadata: new Dictionary<string, object?>
            {
                ["recipientUserId"] = request.UserId,
                ["requestId"] = request.RequestId
            }), cancellationToken);

        await notifications.CreateOrGetByLogicalKeyAsync(
            request.UserId,
            NotificationType.Mention,
            "Mentioned in Audit review",
            null,
            "Artifact",
            claimsResult.Value.ArtifactId,
            $"audit-finding:{finding.Id:N}:mention:{request.RequestId:N}",
            cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    private async Task<ReviewerCandidate?> LoadRecipientAsync(
        Guid tenantId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var tenantActive = await dbContext.Tenants
            .AsNoTracking()
            .AnyAsync(tenant =>
                tenant.Id == tenantId &&
                tenant.Status == TenantStatus.Active &&
                tenant.DeletedAt == null,
                cancellationToken);
        if (!tenantActive)
        {
            return null;
        }

        return await dbContext.TenantUsers
            .AsNoTracking()
            .Where(link =>
                link.TenantId == tenantId &&
                link.UserId == userId &&
                link.Status == TenantUserStatus.Active)
            .Join(
                dbContext.Users.AsNoTracking().Where(user =>
                    user.Status == UserStatus.Active &&
                    user.DeletedAt == null),
                link => link.UserId,
                user => user.Id,
                (link, user) => new ReviewerCandidate(user.Id, link.Role, user.SystemRole))
            .SingleOrDefaultAsync(cancellationToken);
    }

    private async Task<bool> HasAuditReviewAuthorityAsync(
        Guid tenantId,
        ReviewerCandidate candidate,
        CancellationToken cancellationToken)
    {
        if (candidate.SystemRole is SystemRole.PlatformAdmin or SystemRole.SystemAdmin ||
            candidate.TenantRole is TenantUserRole.Owner or TenantUserRole.Admin)
        {
            return true;
        }

        var canView = await capabilityGrants.HasActiveGrantAsync(
            candidate.UserId,
            tenantId,
            CapabilityKeys.AuditView,
            CapabilityScopeType.Tenant,
            tenantId,
            cancellationToken);
        if (!canView)
        {
            return false;
        }

        return await capabilityGrants.HasActiveGrantAsync(
            candidate.UserId,
            tenantId,
            CapabilityKeys.AuditReview,
            CapabilityScopeType.Tenant,
            tenantId,
            cancellationToken);
    }

    private static Result FindingNotFound() =>
        Result.Failure(new ApplicationErrorDetail("FindingNotFound", "The finding is not available."));

    private sealed record ReviewerCandidate(
        Guid UserId,
        TenantUserRole TenantRole,
        SystemRole SystemRole);
}
