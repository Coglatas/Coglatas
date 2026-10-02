using Coglatas.Application.Audit;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Tenancy;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Infrastructure.Persistence;

public sealed class DbAuditFindingsService(
    AppDbContext dbContext,
    IAuditClaimsEvidenceService claimsEvidence,
    IAuditAuthorizationService auditAuthorization,
    ICapabilityGrantEvaluator capabilityGrants,
    ICurrentUser currentUser,
    IClock clock,
    INotificationService notifications,
    IAuditLogger auditLogger,
    IUnitOfWork unitOfWork) : IAuditFindingsService
{
    private const int MaxFindings = 200;
    private const int MaxHistoryPerFinding = 50;
    private const int MaxEligibleOwners = 500;
    private const int MaxOwnerCandidateScan = 1000;
    private const int MaxReasonLength = 1000;

    public async Task<Result<AuditFindingsResponse>> ListAsync(
        AuditFindingsQuery query,
        CancellationToken cancellationToken = default)
    {
        if (query.ArtifactVersionId == Guid.Empty)
        {
            return Failure<AuditFindingsResponse>("ValidationFailed", "Artifact version ID is required.");
        }

        if (!TryOptionalEnum(query.Status, out AuditFindingTriageStatus? status))
        {
            return Failure<AuditFindingsResponse>("ValidationFailed", "Finding status is invalid.");
        }

        if (!TryOptionalEnum(query.Severity, out AuditFindingSeverity? severity))
        {
            return Failure<AuditFindingsResponse>("ValidationFailed", "Finding severity is invalid.");
        }

        if (!TryOptionalEnum(query.WorkflowStatus, out AuditFindingWorkflowStatus? workflowStatus))
        {
            return Failure<AuditFindingsResponse>("ValidationFailed", "Finding workflow status is invalid.");
        }

        if (query.MyReviews && (!currentUser.IsAuthenticated || !currentUser.UserId.HasValue))
        {
            return Failure<AuditFindingsResponse>("AuthenticationRequired", "Authentication is required.");
        }

        var claimsResult = await claimsEvidence.GetAsync(query.ArtifactVersionId, cancellationToken);
        if (!claimsResult.IsSuccess || claimsResult.Value is null)
        {
            return ForwardFailure<AuditClaimsEvidenceResponse, AuditFindingsResponse>(claimsResult);
        }

        var capabilities = await auditAuthorization.GetCapabilitiesAsync(cancellationToken);
        var claimMap = claimsResult.Value.Claims.ToDictionary(claim => claim.ClaimId);
        if (claimMap.Count == 0)
        {
            return Result<AuditFindingsResponse>.Success(new AuditFindingsResponse(
                claimsResult.Value.ArtifactId,
                claimsResult.Value.ArtifactVersionId,
                claimsResult.Value.ArtifactVersionNumber,
                claimsResult.Value.ArtifactTitle,
                capabilities.CanReview,
                Array.Empty<AuditFindingOwnerResponse>(),
                Array.Empty<AuditFindingResponse>()));
        }

        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var claimIds = claimMap.Keys.ToArray();
        IQueryable<ArtifactFinding> findingsQuery = dbContext.Set<ArtifactFinding>()
            .AsNoTracking()
            .Include(finding => finding.History)
            .Include(finding => finding.WorkflowHistory)
            .Where(finding =>
                Enumerable.Contains(claimIds, finding.ArtifactClaimId) &&
                dbContext.Set<ArtifactClaim>().Any(claim =>
                    claim.Id == finding.ArtifactClaimId &&
                    claim.TenantId == finding.TenantId));

        if (status.HasValue)
        {
            findingsQuery = findingsQuery.Where(finding => finding.Status == status.Value);
        }
        if (severity.HasValue)
        {
            findingsQuery = findingsQuery.Where(finding => finding.Severity == severity.Value);
        }
        if (query.OpenOnly)
        {
            findingsQuery = findingsQuery.Where(finding =>
                finding.Status == AuditFindingTriageStatus.Open ||
                finding.Status == AuditFindingTriageStatus.Reviewing);
        }
        if (workflowStatus.HasValue)
        {
            findingsQuery = findingsQuery.Where(finding => finding.WorkflowStatus == workflowStatus.Value);
        }
        if (query.MyReviews)
        {
            var userId = currentUser.UserId!.Value;
            findingsQuery = findingsQuery.Where(finding => finding.OwnerUserId == userId);
        }
        if (query.Overdue)
        {
            findingsQuery = findingsQuery.Where(finding =>
                finding.WorkflowStatus != AuditFindingWorkflowStatus.Done &&
                finding.DueDate.HasValue &&
                finding.DueDate.Value < today);
        }
        if (query.Unassigned)
        {
            findingsQuery = findingsQuery.Where(finding => finding.OwnerUserId == null);
        }

        var findings = await findingsQuery
            .OrderBy(finding =>
                finding.WorkflowStatus != AuditFindingWorkflowStatus.Done &&
                finding.DueDate.HasValue &&
                finding.DueDate.Value < today
                    ? 0
                    : 1)
            .ThenBy(finding =>
                finding.Status == AuditFindingTriageStatus.Open || finding.Status == AuditFindingTriageStatus.Reviewing
                    ? 0
                    : 1)
            .ThenBy(finding =>
                finding.Severity == AuditFindingSeverity.Critical ? 0 :
                finding.Severity == AuditFindingSeverity.High ? 1 :
                finding.Severity == AuditFindingSeverity.Medium ? 2 : 3)
            .ThenBy(finding => finding.DueDate.HasValue ? 0 : 1)
            .ThenBy(finding => finding.DueDate)
            .ThenByDescending(finding => finding.ConfidencePercent)
            .ThenBy(finding => finding.CreatedAt)
            .Take(MaxFindings)
            .ToListAsync(cancellationToken);

        var ownerNames = await LoadOwnerNamesAsync(findings, cancellationToken);
        var eligibleOwners = capabilities.CanReview
            ? await LoadEligibleOwnersAsync(findings, cancellationToken)
            : Array.Empty<AuditFindingOwnerResponse>();
        var response = findings.Select(finding =>
        {
            var claim = claimMap[finding.ArtifactClaimId];
            var firstEvidence = claim.Evidence.OrderBy(item => item.Ordinal).FirstOrDefault();
            return new AuditFindingResponse(
                finding.Id,
                finding.ArtifactClaimId,
                claim.Ordinal,
                claim.Text,
                finding.Severity.ToString(),
                finding.ConfidencePercent,
                finding.DetectorKey,
                finding.PolicyVersion,
                finding.Status.ToString(),
                finding.WorkflowStatus.ToString(),
                finding.OwnerUserId,
                OwnerName(finding.OwnerUserId, ownerNames),
                finding.DueDate,
                IsOverdue(finding, today),
                finding.ResolutionReason,
                finding.CreatedAt,
                finding.UpdatedAt,
                firstEvidence?.EvidenceId,
                firstEvidence?.SourceEventAuditId,
                finding.History
                    .OrderByDescending(item => item.CreatedAt)
                    .Take(MaxHistoryPerFinding)
                    .Select(item => new AuditFindingHistoryResponse(
                        item.FromStatus?.ToString(),
                        item.ToStatus.ToString(),
                        item.Reason,
                        item.CreatedAt))
                    .ToList(),
                finding.WorkflowHistory
                    .OrderByDescending(item => item.CreatedAt)
                    .Take(MaxHistoryPerFinding)
                    .Select(item => new AuditFindingWorkflowHistoryResponse(
                        item.FromWorkflowStatus.ToString(),
                        item.ToWorkflowStatus.ToString(),
                        item.FromOwnerUserId,
                        OwnerName(item.FromOwnerUserId, ownerNames),
                        item.ToOwnerUserId,
                        OwnerName(item.ToOwnerUserId, ownerNames),
                        item.FromDueDate,
                        item.ToDueDate,
                        item.CreatedAt))
                    .ToList());
        }).ToList();

        return Result<AuditFindingsResponse>.Success(new AuditFindingsResponse(
            claimsResult.Value.ArtifactId,
            claimsResult.Value.ArtifactVersionId,
            claimsResult.Value.ArtifactVersionNumber,
            claimsResult.Value.ArtifactTitle,
            capabilities.CanReview,
            eligibleOwners,
            response));
    }

    public async Task<Result> UpdateTriageAsync(
        Guid findingId,
        UpdateAuditFindingTriageRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!currentUser.IsAuthenticated || !currentUser.UserId.HasValue)
        {
            return Result.Failure(new ApplicationErrorDetail("AuthenticationRequired", "Authentication is required."));
        }

        var reviewAuthorization = await auditAuthorization.AuthorizeAsync(
            CapabilityKeys.AuditReview,
            "audit.findings.triage",
            cancellationToken);
        if (!reviewAuthorization.IsSuccess)
        {
            return reviewAuthorization;
        }

        if (findingId == Guid.Empty ||
            !Enum.TryParse<AuditFindingTriageStatus>(request.Status?.Trim(), ignoreCase: true, out var nextStatus) ||
            !Enum.IsDefined(nextStatus))
        {
            return Result.Failure(new ApplicationErrorDetail("ValidationFailed", "Finding status is invalid."));
        }

        var finding = await LoadFindingForMutationAsync(findingId, cancellationToken);
        if (finding?.ArtifactClaim is null || finding.ArtifactClaim.TenantId != finding.TenantId)
        {
            return FindingNotFound();
        }

        var claimsResult = await claimsEvidence.GetAsync(finding.ArtifactClaim.ArtifactVersionId, cancellationToken);
        if (!IsAuthorizedFinding(claimsResult, finding))
        {
            return ForwardFindingAuthorizationFailure(claimsResult);
        }

        var normalizedReason = NormalizeOptional(request.Reason);
        if (normalizedReason?.Length > MaxReasonLength)
        {
            return Result.Failure(new ApplicationErrorDetail(
                "ValidationFailed",
                $"Finding reason must be at most {MaxReasonLength} characters."));
        }

        var statusChanged = finding.Status != nextStatus;
        var requiresReason = statusChanged &&
            nextStatus is AuditFindingTriageStatus.AcceptedRisk or AuditFindingTriageStatus.FalsePositive;
        if (requiresReason && normalizedReason is null)
        {
            return Result.Failure(new ApplicationErrorDetail(
                "ReasonRequired",
                "Accepted Risk and False Positive transitions require a reason."));
        }

        var nextOwner = finding.OwnerUserId;
        if (request.AssignOwner)
        {
            if (request.OwnerUserId.HasValue &&
                !await IsEligibleOwnerAsync(finding.TenantId, request.OwnerUserId.Value, cancellationToken))
            {
                return Result.Failure(new ApplicationErrorDetail(
                    "OwnerNotEligible",
                    "The selected finding owner is not available for Audit review in the current tenant."));
            }
            nextOwner = request.OwnerUserId;
        }

        var nextReason = statusChanged
            ? nextStatus is AuditFindingTriageStatus.Open or AuditFindingTriageStatus.Reviewing
                ? null
                : normalizedReason
            : finding.ResolutionReason;
        var ownerChanged = nextOwner != finding.OwnerUserId;

        if (!statusChanged && !ownerChanged)
        {
            return Result.Success();
        }

        var userId = currentUser.UserId.Value;
        var previousStatus = finding.Status;
        var previousOwner = finding.OwnerUserId;
        finding.Status = nextStatus;
        finding.OwnerUserId = nextOwner;
        finding.ResolutionReason = nextReason;
        dbContext.Set<AuditFindingHistory>().Add(new AuditFindingHistory
        {
            TenantId = finding.TenantId,
            ArtifactFindingId = finding.Id,
            FromStatus = previousStatus,
            ToStatus = nextStatus,
            OwnerUserId = nextOwner,
            Reason = statusChanged ? normalizedReason : null,
            ChangedByUserId = userId,
            Finding = finding
        });

        AuditFindingWorkflowHistory? workflowHistory = null;
        if (ownerChanged)
        {
            workflowHistory = AddWorkflowHistory(
                finding,
                finding.WorkflowStatus,
                finding.WorkflowStatus,
                previousOwner,
                nextOwner,
                finding.DueDate,
                finding.DueDate,
                userId);
        }

        await auditLogger.LogAsync(new AuditLogEntry(
            userId,
            "AuditFindingTriageChanged",
            "ArtifactFinding",
            finding.Id,
            "Audit finding triage state changed.",
            TenantId: finding.TenantId,
            Metadata: new Dictionary<string, object?>
            {
                ["fromStatus"] = previousStatus.ToString(),
                ["toStatus"] = nextStatus.ToString(),
                ["ownerChanged"] = ownerChanged,
                ["ownerAssigned"] = nextOwner.HasValue
            }), cancellationToken);

        if (workflowHistory is not null && nextOwner.HasValue && nextOwner.Value != userId)
        {
            await StageAssignmentNotificationAsync(
                nextOwner.Value,
                claimsResult.Value!.ArtifactId,
                finding.Id,
                workflowHistory.Id,
                cancellationToken);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result> UpdateWorkflowAsync(
        Guid findingId,
        UpdateAuditFindingWorkflowRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!currentUser.IsAuthenticated || !currentUser.UserId.HasValue)
        {
            return Result.Failure(new ApplicationErrorDetail("AuthenticationRequired", "Authentication is required."));
        }

        var reviewAuthorization = await auditAuthorization.AuthorizeAsync(
            CapabilityKeys.AuditReview,
            "audit.findings.workflow",
            cancellationToken);
        if (!reviewAuthorization.IsSuccess)
        {
            return reviewAuthorization;
        }

        if (findingId == Guid.Empty ||
            !Enum.TryParse<AuditFindingWorkflowStatus>(request.WorkflowStatus?.Trim(), ignoreCase: true, out var nextWorkflowStatus) ||
            !Enum.IsDefined(nextWorkflowStatus))
        {
            return Result.Failure(new ApplicationErrorDetail("ValidationFailed", "Finding workflow status is invalid."));
        }

        var finding = await LoadFindingForMutationAsync(findingId, cancellationToken);
        if (finding?.ArtifactClaim is null || finding.ArtifactClaim.TenantId != finding.TenantId)
        {
            return FindingNotFound();
        }

        var claimsResult = await claimsEvidence.GetAsync(finding.ArtifactClaim.ArtifactVersionId, cancellationToken);
        if (!IsAuthorizedFinding(claimsResult, finding))
        {
            return ForwardFindingAuthorizationFailure(claimsResult);
        }

        var nextOwner = finding.OwnerUserId;
        if (request.AssignOwner)
        {
            if (request.OwnerUserId.HasValue &&
                !await IsEligibleOwnerAsync(finding.TenantId, request.OwnerUserId.Value, cancellationToken))
            {
                return Result.Failure(new ApplicationErrorDetail(
                    "OwnerNotEligible",
                    "The selected finding owner is not available for Audit review in the current tenant."));
            }
            nextOwner = request.OwnerUserId;
        }

        var nextDueDate = request.SetDueDate ? request.DueDate : finding.DueDate;
        var statusChanged = finding.WorkflowStatus != nextWorkflowStatus;
        var ownerChanged = finding.OwnerUserId != nextOwner;
        var dueDateChanged = finding.DueDate != nextDueDate;
        if (!statusChanged && !ownerChanged && !dueDateChanged)
        {
            return Result.Success();
        }

        var userId = currentUser.UserId.Value;
        var previousWorkflowStatus = finding.WorkflowStatus;
        var previousOwner = finding.OwnerUserId;
        var previousDueDate = finding.DueDate;
        finding.WorkflowStatus = nextWorkflowStatus;
        finding.OwnerUserId = nextOwner;
        finding.DueDate = nextDueDate;

        var workflowHistory = AddWorkflowHistory(
            finding,
            previousWorkflowStatus,
            nextWorkflowStatus,
            previousOwner,
            nextOwner,
            previousDueDate,
            nextDueDate,
            userId);

        await auditLogger.LogAsync(new AuditLogEntry(
            userId,
            "AuditFindingWorkflowChanged",
            "ArtifactFinding",
            finding.Id,
            "Audit finding operational workflow changed.",
            TenantId: finding.TenantId,
            Metadata: new Dictionary<string, object?>
            {
                ["fromWorkflowStatus"] = previousWorkflowStatus.ToString(),
                ["toWorkflowStatus"] = nextWorkflowStatus.ToString(),
                ["ownerChanged"] = ownerChanged,
                ["ownerAssigned"] = nextOwner.HasValue,
                ["dueDateChanged"] = dueDateChanged,
                ["dueDateSet"] = nextDueDate.HasValue
            }), cancellationToken);

        if (ownerChanged && nextOwner.HasValue && nextOwner.Value != userId)
        {
            await StageAssignmentNotificationAsync(
                nextOwner.Value,
                claimsResult.Value!.ArtifactId,
                finding.Id,
                workflowHistory.Id,
                cancellationToken);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    private async Task<ArtifactFinding?> LoadFindingForMutationAsync(
        Guid findingId,
        CancellationToken cancellationToken) =>
        await dbContext.Set<ArtifactFinding>()
            .Include(item => item.ArtifactClaim)
            .SingleOrDefaultAsync(item => item.Id == findingId, cancellationToken);

    private static bool IsAuthorizedFinding(
        Result<AuditClaimsEvidenceResponse> claimsResult,
        ArtifactFinding finding) =>
        claimsResult.IsSuccess &&
        claimsResult.Value is not null &&
        claimsResult.Value.Claims.Any(claim => claim.ClaimId == finding.ArtifactClaimId);

    private static Result ForwardFindingAuthorizationFailure(Result<AuditClaimsEvidenceResponse> claimsResult)
    {
        if (claimsResult.ErrorDetail?.Code is "AuthenticationRequired" or "CapabilityDenied" or "TenantMembershipRequired")
        {
            return claimsResult.ErrorDetail is not null
                ? Result.Failure(claimsResult.ErrorDetail)
                : Result.Failure(claimsResult.Error ?? "Audit view is not permitted.");
        }
        return FindingNotFound();
    }

    private AuditFindingWorkflowHistory AddWorkflowHistory(
        ArtifactFinding finding,
        AuditFindingWorkflowStatus fromWorkflowStatus,
        AuditFindingWorkflowStatus toWorkflowStatus,
        Guid? fromOwnerUserId,
        Guid? toOwnerUserId,
        DateOnly? fromDueDate,
        DateOnly? toDueDate,
        Guid changedByUserId)
    {
        var history = new AuditFindingWorkflowHistory
        {
            TenantId = finding.TenantId,
            ArtifactFindingId = finding.Id,
            FromWorkflowStatus = fromWorkflowStatus,
            ToWorkflowStatus = toWorkflowStatus,
            FromOwnerUserId = fromOwnerUserId,
            ToOwnerUserId = toOwnerUserId,
            FromDueDate = fromDueDate,
            ToDueDate = toDueDate,
            ChangedByUserId = changedByUserId,
            Finding = finding
        };
        dbContext.Set<AuditFindingWorkflowHistory>().Add(history);
        return history;
    }

    private async Task StageAssignmentNotificationAsync(
        Guid recipientUserId,
        Guid artifactId,
        Guid findingId,
        Guid workflowHistoryId,
        CancellationToken cancellationToken)
    {
        await notifications.CreateOrGetByLogicalKeyAsync(
            recipientUserId,
            NotificationType.System,
            "Audit review assigned",
            null,
            "Artifact",
            artifactId,
            $"audit-finding:{findingId:N}:workflow:{workflowHistoryId:N}:assigned",
            cancellationToken);
    }

    private async Task<Dictionary<Guid, string>> LoadOwnerNamesAsync(
        IReadOnlyList<ArtifactFinding> findings,
        CancellationToken cancellationToken)
    {
        var ownerIds = findings
            .SelectMany(finding =>
                finding.WorkflowHistory
                    .SelectMany(history => new[] { history.FromOwnerUserId, history.ToOwnerUserId })
                    .Append(finding.OwnerUserId))
            .Where(ownerId => ownerId.HasValue)
            .Select(ownerId => ownerId!.Value)
            .Distinct()
            .ToArray();
        if (ownerIds.Length == 0 || findings.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var tenantId = findings[0].TenantId;
        var tenantOwnerIds = await dbContext.TenantUsers
            .AsNoTracking()
            .Where(link =>
                link.TenantId == tenantId &&
                link.Status == TenantUserStatus.Active &&
                Enumerable.Contains(ownerIds, link.UserId))
            .Select(link => link.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (tenantOwnerIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        return await dbContext.Users
            .AsNoTracking()
            .Where(user =>
                tenantOwnerIds.Contains(user.Id) &&
                user.Status == UserStatus.Active &&
                user.DeletedAt == null)
            .Select(user => new { user.Id, user.DisplayName })
            .ToDictionaryAsync(user => user.Id, user => user.DisplayName, cancellationToken);
    }

    private async Task<IReadOnlyList<AuditFindingOwnerResponse>> LoadEligibleOwnersAsync(
        IReadOnlyList<ArtifactFinding> findings,
        CancellationToken cancellationToken)
    {
        if (findings.Count == 0)
        {
            return Array.Empty<AuditFindingOwnerResponse>();
        }

        var tenantId = findings[0].TenantId;
        if (!await IsActiveTenantAsync(tenantId, cancellationToken))
        {
            return Array.Empty<AuditFindingOwnerResponse>();
        }

        var candidates = await dbContext.TenantUsers
            .AsNoTracking()
            .Where(link => link.TenantId == tenantId && link.Status == TenantUserStatus.Active)
            .Join(
                dbContext.Users.AsNoTracking().Where(user => user.Status == UserStatus.Active && user.DeletedAt == null),
                link => link.UserId,
                user => user.Id,
                (link, user) => new AuditOwnerCandidate(
                    user.Id,
                    user.DisplayName,
                    link.Role,
                    user.SystemRole))
            .Distinct()
            .OrderBy(owner => owner.DisplayName)
            .ThenBy(owner => owner.UserId)
            .Take(MaxOwnerCandidateScan)
            .ToListAsync(cancellationToken);

        var eligible = new List<AuditFindingOwnerResponse>(Math.Min(candidates.Count, MaxEligibleOwners));
        foreach (var candidate in candidates)
        {
            if (!await HasAuditReviewAuthorityAsync(tenantId, candidate, cancellationToken))
            {
                continue;
            }

            eligible.Add(new AuditFindingOwnerResponse(candidate.UserId, candidate.DisplayName));
            if (eligible.Count >= MaxEligibleOwners)
            {
                break;
            }
        }
        return eligible;
    }

    private async Task<bool> IsEligibleOwnerAsync(
        Guid tenantId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        if (!await IsActiveTenantAsync(tenantId, cancellationToken))
        {
            return false;
        }

        var candidate = await dbContext.TenantUsers
            .AsNoTracking()
            .Where(link =>
                link.TenantId == tenantId &&
                link.UserId == userId &&
                link.Status == TenantUserStatus.Active)
            .Join(
                dbContext.Users.AsNoTracking().Where(user => user.Status == UserStatus.Active && user.DeletedAt == null),
                link => link.UserId,
                user => user.Id,
                (link, user) => new AuditOwnerCandidate(
                    user.Id,
                    user.DisplayName,
                    link.Role,
                    user.SystemRole))
            .SingleOrDefaultAsync(cancellationToken);
        return candidate is not null &&
               await HasAuditReviewAuthorityAsync(tenantId, candidate, cancellationToken);
    }

    private async Task<bool> HasAuditReviewAuthorityAsync(
        Guid tenantId,
        AuditOwnerCandidate candidate,
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

    private Task<bool> IsActiveTenantAsync(Guid tenantId, CancellationToken cancellationToken) =>
        dbContext.Tenants
            .AsNoTracking()
            .AnyAsync(tenant =>
                tenant.Id == tenantId &&
                tenant.Status == TenantStatus.Active &&
                tenant.DeletedAt == null,
                cancellationToken);

    private static string? OwnerName(Guid? ownerUserId, IReadOnlyDictionary<Guid, string> ownerNames) =>
        ownerUserId.HasValue && ownerNames.TryGetValue(ownerUserId.Value, out var ownerName)
            ? ownerName
            : null;

    private static bool IsOverdue(ArtifactFinding finding, DateOnly today) =>
        finding.WorkflowStatus != AuditFindingWorkflowStatus.Done &&
        finding.DueDate.HasValue &&
        finding.DueDate.Value < today;

    private static bool TryOptionalEnum<TEnum>(string? value, out TEnum? parsed)
        where TEnum : struct, Enum
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!Enum.TryParse<TEnum>(value.Trim(), ignoreCase: true, out var candidate) || !Enum.IsDefined(candidate))
        {
            return false;
        }

        parsed = candidate;
        return true;
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static Result FindingNotFound() =>
        Result.Failure(new ApplicationErrorDetail("FindingNotFound", "The finding is not available."));

    private static Result<T> Failure<T>(string code, string message) =>
        Result<T>.Failure(new ApplicationErrorDetail(code, message));

    private static Result<TOut> ForwardFailure<TIn, TOut>(Result<TIn> result) =>
        result.ErrorDetail is not null
            ? Result<TOut>.Failure(result.ErrorDetail)
            : Result<TOut>.Failure(result.Error ?? "The requested Audit operation failed.");

    private sealed record AuditOwnerCandidate(
        Guid UserId,
        string DisplayName,
        TenantUserRole TenantRole,
        SystemRole SystemRole);
}
