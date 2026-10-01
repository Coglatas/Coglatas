using Coglatas.Application.Audit;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Tenancy;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Infrastructure.Persistence;

public sealed class DbAuditFindingDecisionService(
    AppDbContext dbContext,
    IAuditClaimsEvidenceService claimsEvidence,
    IAuditAuthorizationService auditAuthorization,
    ICurrentUser currentUser,
    IAuditLogger auditLogger,
    IUnitOfWork unitOfWork) : IAuditFindingDecisionService
{
    private const int MaxHistory = 100;
    private const int MaxRationaleLength = 1000;

    private static readonly IReadOnlyList<AuditFindingDecisionOptionResponse> DecisionOptions =
    [
        new("NoIssue", "No issue", false),
        new("NeedsFix", "Needs fix", false),
        new("AcceptedRisk", "Accepted risk", true)
    ];

    public async Task<Result<AuditFindingDecisionResponse>> GetAsync(
        Guid findingId,
        CancellationToken cancellationToken = default)
    {
        if (findingId == Guid.Empty)
        {
            return Failure<AuditFindingDecisionResponse>("ValidationFailed", "Finding ID is required.");
        }

        var findingResult = await LoadAuthorizedFindingAsync(findingId, cancellationToken);
        if (!findingResult.IsSuccess || findingResult.Value is null)
        {
            return ForwardFailure<ArtifactFinding, AuditFindingDecisionResponse>(findingResult);
        }

        return Result<AuditFindingDecisionResponse>.Success(
            await BuildResponseAsync(findingResult.Value, cancellationToken));
    }

    public async Task<Result<AuditFindingDecisionResponse>> SaveAsync(
        Guid findingId,
        SaveAuditFindingDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!currentUser.IsAuthenticated || !currentUser.UserId.HasValue)
        {
            return Failure<AuditFindingDecisionResponse>("AuthenticationRequired", "Authentication is required.");
        }

        var reviewAuthorization = await auditAuthorization.AuthorizeAsync(
            CapabilityKeys.AuditReview,
            "audit.findings.decision",
            cancellationToken);
        if (!reviewAuthorization.IsSuccess)
        {
            return reviewAuthorization.ErrorDetail is not null
                ? Result<AuditFindingDecisionResponse>.Failure(reviewAuthorization.ErrorDetail)
                : Result<AuditFindingDecisionResponse>.Failure(reviewAuthorization.Error ?? "Audit review is not permitted.");
        }

        if (findingId == Guid.Empty ||
            !Enum.TryParse<AuditFindingReviewDecision>(request.Decision?.Trim(), ignoreCase: true, out var nextDecision) ||
            !Enum.IsDefined(nextDecision))
        {
            return Failure<AuditFindingDecisionResponse>("ValidationFailed", "Review decision is invalid.");
        }

        var rationale = NormalizeOptional(request.Rationale);
        if (rationale?.Length > MaxRationaleLength)
        {
            return Failure<AuditFindingDecisionResponse>(
                "ValidationFailed",
                $"Decision rationale must be at most {MaxRationaleLength} characters.");
        }
        if (RequiresRationale(nextDecision) && rationale is null)
        {
            return Failure<AuditFindingDecisionResponse>(
                "ReasonRequired",
                $"{DecisionLabel(nextDecision)} requires a rationale.");
        }

        var findingResult = await LoadAuthorizedFindingAsync(findingId, cancellationToken);
        if (!findingResult.IsSuccess || findingResult.Value is null)
        {
            return ForwardFailure<ArtifactFinding, AuditFindingDecisionResponse>(findingResult);
        }

        var finding = findingResult.Value;
        var current = await dbContext.Set<AuditFindingDecision>()
            .AsNoTracking()
            .Where(entry =>
                entry.TenantId == finding.TenantId &&
                entry.ArtifactFindingId == finding.Id)
            .OrderByDescending(entry => entry.CreatedAt)
            .ThenByDescending(entry => entry.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (current is not null &&
            current.Decision == nextDecision &&
            string.Equals(current.Rationale, rationale, StringComparison.Ordinal))
        {
            return Result<AuditFindingDecisionResponse>.Success(
                await BuildResponseAsync(finding, cancellationToken));
        }

        var reviewerUserId = currentUser.UserId.Value;
        var reviewerDisplayName = await dbContext.Users
            .AsNoTracking()
            .Where(user => user.Id == reviewerUserId)
            .Select(user => user.DisplayName)
            .SingleOrDefaultAsync(cancellationToken);
        reviewerDisplayName = string.IsNullOrWhiteSpace(reviewerDisplayName)
            ? reviewerUserId.ToString("D")
            : reviewerDisplayName.Trim();

        dbContext.Set<AuditFindingDecision>().Add(new AuditFindingDecision
        {
            TenantId = finding.TenantId,
            ArtifactFindingId = finding.Id,
            Decision = nextDecision,
            PreviousDecision = current?.Decision,
            Rationale = rationale,
            ReviewerUserId = reviewerUserId,
            ReviewerDisplayName = reviewerDisplayName,
            Finding = null
        });

        await auditLogger.LogAsync(new AuditLogEntry(
            reviewerUserId,
            "AuditFindingDecisionRecorded",
            "ArtifactFinding",
            finding.Id,
            "Structured Audit finding review decision recorded.",
            TenantId: finding.TenantId,
            Metadata: new Dictionary<string, object?>
            {
                ["fromDecision"] = current?.Decision.ToString(),
                ["toDecision"] = nextDecision.ToString(),
                ["reviewCompleted"] = true
            }), cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result<AuditFindingDecisionResponse>.Success(
            await BuildResponseAsync(finding, cancellationToken));
    }

    private async Task<Result<ArtifactFinding>> LoadAuthorizedFindingAsync(
        Guid findingId,
        CancellationToken cancellationToken)
    {
        var finding = await dbContext.Set<ArtifactFinding>()
            .AsNoTracking()
            .Include(item => item.ArtifactClaim)
            .SingleOrDefaultAsync(item => item.Id == findingId, cancellationToken);
        if (finding?.ArtifactClaim is null || finding.ArtifactClaim.TenantId != finding.TenantId)
        {
            return FindingNotFound();
        }

        var claimsResult = await claimsEvidence.GetAsync(
            finding.ArtifactClaim.ArtifactVersionId,
            cancellationToken);
        if (!claimsResult.IsSuccess || claimsResult.Value is null)
        {
            if (claimsResult.ErrorDetail?.Code is "AuthenticationRequired" or "CapabilityDenied" or "TenantMembershipRequired")
            {
                return claimsResult.ErrorDetail is not null
                    ? Result<ArtifactFinding>.Failure(claimsResult.ErrorDetail)
                    : Result<ArtifactFinding>.Failure(claimsResult.Error ?? "Audit view is not permitted.");
            }

            return FindingNotFound();
        }

        if (claimsResult.Value.Claims.All(claim => claim.ClaimId != finding.ArtifactClaimId))
        {
            return FindingNotFound();
        }

        return Result<ArtifactFinding>.Success(finding);
    }

    private async Task<AuditFindingDecisionResponse> BuildResponseAsync(
        ArtifactFinding finding,
        CancellationToken cancellationToken)
    {
        var history = await dbContext.Set<AuditFindingDecision>()
            .AsNoTracking()
            .Where(entry =>
                entry.TenantId == finding.TenantId &&
                entry.ArtifactFindingId == finding.Id)
            .OrderByDescending(entry => entry.CreatedAt)
            .ThenByDescending(entry => entry.Id)
            .Take(MaxHistory)
            .Select(entry => new AuditFindingDecisionHistoryResponse(
                entry.Id,
                entry.Decision.ToString(),
                entry.PreviousDecision.HasValue ? entry.PreviousDecision.Value.ToString() : null,
                entry.Rationale,
                entry.ReviewerUserId,
                entry.ReviewerDisplayName,
                entry.CreatedAt))
            .ToListAsync(cancellationToken);
        var capabilities = await auditAuthorization.GetCapabilitiesAsync(cancellationToken);
        var current = history.FirstOrDefault();

        return new AuditFindingDecisionResponse(
            finding.Id,
            current is not null,
            capabilities.CanReview,
            current,
            history,
            DecisionOptions);
    }

    private static bool RequiresRationale(AuditFindingReviewDecision decision) =>
        decision == AuditFindingReviewDecision.AcceptedRisk;

    private static string DecisionLabel(AuditFindingReviewDecision decision) => decision switch
    {
        AuditFindingReviewDecision.NoIssue => "No issue",
        AuditFindingReviewDecision.NeedsFix => "Needs fix",
        AuditFindingReviewDecision.AcceptedRisk => "Accepted risk",
        _ => decision.ToString()
    };

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static Result<ArtifactFinding> FindingNotFound() =>
        Result<ArtifactFinding>.Failure(new ApplicationErrorDetail(
            "FindingNotFound",
            "The finding is not available."));

    private static Result<T> Failure<T>(string code, string message) =>
        Result<T>.Failure(new ApplicationErrorDetail(code, message));

    private static Result<TOut> ForwardFailure<TIn, TOut>(Result<TIn> result) =>
        result.ErrorDetail is not null
            ? Result<TOut>.Failure(result.ErrorDetail)
            : Result<TOut>.Failure(result.Error ?? "The requested Audit operation failed.");
}
