using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Tenancy;
using Coglatas.Domain.Enums;

namespace Coglatas.Application.Audit;

public sealed record AuditCapabilityResponse(
    bool CanView,
    bool CanReview,
    bool CanApprove,
    bool CanExport,
    bool CanViewSensitiveMetadata)
{
    public bool HasCapability(string capabilityKey) => capabilityKey switch
    {
        CapabilityKeys.AuditView => CanView,
        CapabilityKeys.AuditReview => CanReview,
        CapabilityKeys.AuditApprove => CanApprove,
        CapabilityKeys.AuditExport => CanExport,
        CapabilityKeys.AuditSensitiveMetadataView => CanViewSensitiveMetadata,
        _ => false
    };
}

public interface IAuditAuthorizationService
{
    Task<AuditCapabilityResponse> GetCapabilitiesAsync(CancellationToken cancellationToken = default);

    Task<Result> AuthorizeAsync(
        string capabilityKey,
        string operation,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Canonical server-side authorization boundary for Audit operations.
/// Tenant administrators receive the minimum operational baseline (view/review).
/// Higher-risk operations require an explicit delegated capability. Platform and
/// legacy system administrators retain the complete platform audit capability set.
/// </summary>
public sealed class AuditAuthorizationService(
    ICurrentUser currentUser,
    ICurrentTenant currentTenant,
    ITenantAuthorizationService tenantAuthorization,
    ICapabilityGrantEvaluator capabilityGrants,
    IAuditLogger auditLogger,
    IUnitOfWork unitOfWork) : IAuditAuthorizationService
{
    public async Task<AuditCapabilityResponse> GetCapabilitiesAsync(
        CancellationToken cancellationToken = default)
    {
        if (!currentUser.IsAuthenticated || !currentUser.UserId.HasValue)
        {
            return None();
        }

        if (currentUser.SystemRole == SystemRole.PlatformAdmin)
        {
            return All();
        }

        if (currentTenant is not { IsAvailable: true, IsPlatformScope: false })
        {
            return None();
        }

        var userId = currentUser.UserId.Value;
        var tenantId = currentTenant.TenantId;
        var isTenantAdmin = await tenantAuthorization.CanManageTenantAsync(
            userId,
            tenantId,
            cancellationToken);

        var canView = isTenantAdmin || await HasTenantGrantAsync(
            userId,
            tenantId,
            CapabilityKeys.AuditView,
            cancellationToken);
        if (!canView)
        {
            return None();
        }

        var canReview = isTenantAdmin || await HasTenantGrantAsync(
            userId,
            tenantId,
            CapabilityKeys.AuditReview,
            cancellationToken);
        var canApprove = await HasTenantGrantAsync(
            userId,
            tenantId,
            CapabilityKeys.AuditApprove,
            cancellationToken);
        var canExport = await HasTenantGrantAsync(
            userId,
            tenantId,
            CapabilityKeys.AuditExport,
            cancellationToken);
        var canViewSensitiveMetadata = await HasTenantGrantAsync(
            userId,
            tenantId,
            CapabilityKeys.AuditSensitiveMetadataView,
            cancellationToken);

        return new AuditCapabilityResponse(
            canView,
            canReview,
            canApprove,
            canExport,
            canViewSensitiveMetadata);
    }

    public async Task<Result> AuthorizeAsync(
        string capabilityKey,
        string operation,
        CancellationToken cancellationToken = default)
    {
        var capabilities = await GetCapabilitiesAsync(cancellationToken);
        if (capabilities.HasCapability(capabilityKey))
        {
            return Result.Success();
        }

        await AuditDeniedAsync(capabilityKey, operation, cancellationToken);
        return Result.Failure(new ApplicationErrorDetail(
            "CapabilityDenied",
            "The requested Audit operation is not permitted.",
            Target: "audit"));
    }

    private Task<bool> HasTenantGrantAsync(
        Guid userId,
        Guid tenantId,
        string capabilityKey,
        CancellationToken cancellationToken)
    {
        return capabilityGrants.HasActiveGrantAsync(
            userId,
            tenantId,
            capabilityKey,
            CapabilityScopeType.Tenant,
            tenantId,
            cancellationToken);
    }

    private async Task AuditDeniedAsync(
        string capabilityKey,
        string operation,
        CancellationToken cancellationToken)
    {
        await auditLogger.LogAsync(new AuditLogEntry(
            currentUser.UserId,
            "AuditCapabilityDenied",
            "AuditCapability",
            null,
            "Audit operation denied.",
            Metadata: new Dictionary<string, object?>
            {
                ["capability"] = capabilityKey,
                ["operation"] = operation
            },
            TenantId: currentTenant is { IsAvailable: true, IsPlatformScope: false }
                ? currentTenant.TenantId
                : null), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private static AuditCapabilityResponse None() => new(false, false, false, false, false);

    private static AuditCapabilityResponse All() => new(true, true, true, true, true);
}
