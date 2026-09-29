using Coglatas.Application.Auth;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Realtime;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;

namespace Coglatas.Application.Tenancy;

public sealed class TenantService(
    ITenantRepository tenantRepository,
    ITenantAuthorizationService tenantAuthorization,
    ICurrentTenant currentTenant,
    ICurrentUser currentUser,
    IAuditLogger auditLogger,
    IUnitOfWork unitOfWork,
    IUserSessionService userSessions,
    TenancyOptions options,
    IAuthorizationStateChangePublisher? authorizationChanges = null) : ITenantService
{
    public async Task<Result<IReadOnlyList<TenantResponse>>> ListPlatformTenantsAsync(CancellationToken cancellationToken = default)
    {
        if (!await IsPlatformAdminAsync(cancellationToken))
        {
            return Result<IReadOnlyList<TenantResponse>>.Failure("PlatformAdmin access is required.");
        }

        var tenants = await tenantRepository.ListTenantsAsync(cancellationToken);
        return Result<IReadOnlyList<TenantResponse>>.Success(tenants.Select(ToTenantResponse).ToList());
    }

    public async Task<Result<TenantResponse>> CreateTenantAsync(CreateTenantRequest request, CancellationToken cancellationToken = default)
    {
        if (!await IsPlatformAdminAsync(cancellationToken))
        {
            return Result<TenantResponse>.Failure("PlatformAdmin access is required.");
        }

        var slug = NormalizeSlug(request.Slug);
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(slug))
        {
            return Result<TenantResponse>.Failure("Tenant name and slug are required.");
        }

        if (await tenantRepository.GetTenantBySlugAsync(slug, cancellationToken) is not null)
        {
            return Result<TenantResponse>.Failure("Tenant slug is already in use.");
        }

        var primaryDomain = NormalizeOptional(request.PrimaryDomain);
        if (primaryDomain is not null &&
            await tenantRepository.GetTenantByPrimaryDomainAsync(primaryDomain, cancellationToken) is not null)
        {
            return Result<TenantResponse>.Failure("Tenant primary domain is already in use.");
        }

        var tenant = new Tenant
        {
            Name = request.Name.Trim(),
            Slug = slug,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? request.Name.Trim() : request.DisplayName.Trim(),
            PrimaryDomain = primaryDomain,
            PlanId = NormalizeOptional(request.PlanId),
            Status = TenantStatus.Active
        };

        await tenantRepository.AddTenantAsync(tenant, cancellationToken);
        await auditLogger.LogAsync(new AuditLogEntry(
            currentUser.UserId,
            "TenantCreated",
            "Tenant",
            tenant.Id,
            "Tenant created.",
            Metadata: new Dictionary<string, object?>
            {
                ["slug"] = tenant.Slug
            },
            TenantId: tenant.Id), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result<TenantResponse>.Success(ToTenantResponse(tenant));
    }

    public async Task<Result<TenantResponse>> GetPlatformTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        if (!await IsPlatformAdminAsync(cancellationToken))
        {
            return Result<TenantResponse>.Failure("PlatformAdmin access is required.");
        }

        var tenant = await tenantRepository.GetTenantAsync(tenantId, cancellationToken);
        if (tenant is null)
        {
            return Result<TenantResponse>.Failure("Tenant not found.");
        }

        await auditLogger.LogAsync(new AuditLogEntry(
            currentUser.UserId,
            "PlatformAdminAccessedTenantDetail",
            "Tenant",
            tenant.Id,
            "PlatformAdmin accessed tenant detail.",
            TenantId: tenant.Id), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result<TenantResponse>.Success(ToTenantResponse(tenant));
    }

    public async Task<Result<TenantResponse>> UpdateTenantAsync(Guid tenantId, UpdateTenantRequest request, CancellationToken cancellationToken = default)
    {
        if (!await IsPlatformAdminAsync(cancellationToken))
        {
            return Result<TenantResponse>.Failure("PlatformAdmin access is required.");
        }

        var tenant = await tenantRepository.GetTenantAsync(tenantId, cancellationToken);
        if (tenant is null)
        {
            return Result<TenantResponse>.Failure("Tenant not found.");
        }

        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            tenant.Name = request.Name.Trim();
        }

        if (!string.IsNullOrWhiteSpace(request.DisplayName))
        {
            tenant.DisplayName = request.DisplayName.Trim();
        }

        if (request.PrimaryDomain is not null)
        {
            var primaryDomain = NormalizeOptional(request.PrimaryDomain);
            if (primaryDomain is not null)
            {
                var existing = await tenantRepository.GetTenantByPrimaryDomainAsync(primaryDomain, cancellationToken);
                if (existing is not null && existing.Id != tenant.Id)
                {
                    return Result<TenantResponse>.Failure("Tenant primary domain is already in use.");
                }
            }

            tenant.PrimaryDomain = primaryDomain;
        }

        if (request.PlanId is not null)
        {
            tenant.PlanId = NormalizeOptional(request.PlanId);
        }

        await auditLogger.LogAsync(new AuditLogEntry(
            currentUser.UserId,
            "TenantUpdated",
            "Tenant",
            tenant.Id,
            "Tenant updated.",
            TenantId: tenant.Id), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result<TenantResponse>.Success(ToTenantResponse(tenant));
    }

    public Task<Result> SuspendTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        return SetTenantStatusAsync(tenantId, TenantStatus.Suspended, cancellationToken);
    }

    public Task<Result> ActivateTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        return SetTenantStatusAsync(tenantId, TenantStatus.Active, cancellationToken);
    }

    public Task<Result> ArchiveTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        return SetTenantStatusAsync(tenantId, TenantStatus.Archived, cancellationToken);
    }

    public async Task<Result<CurrentTenantResponse>> GetCurrentTenantAsync(CancellationToken cancellationToken = default)
    {
        if (!currentTenant.IsAvailable)
        {
            return Result<CurrentTenantResponse>.Failure("Tenant is not available for this request.");
        }

        var tenant = await tenantRepository.GetTenantAsync(currentTenant.TenantId, cancellationToken);
        TenantUser? membership = null;
        if (TryGetUserId(out var userId))
        {
            membership = await tenantRepository.GetTenantUserAsync(currentTenant.TenantId, userId, cancellationToken);
        }

        return Result<CurrentTenantResponse>.Success(new CurrentTenantResponse(
            currentTenant.TenantId,
            currentTenant.TenantSlug,
            currentTenant.IsAvailable,
            currentTenant.IsPlatformScope,
            tenant?.DisplayName ?? tenant?.Name ?? currentTenant.TenantSlug,
            tenant?.Status,
            membership?.Status == TenantUserStatus.Active ? membership.Role : null,
            options.AppMode,
            options.AllowTenantSwitching && options.AppMode != AppMode.OnPremSingleTenant));
    }

    public async Task<Result<IReadOnlyList<TenantResponse>>> ListMyTenantsAsync(CancellationToken cancellationToken = default)
    {
        if (!TryGetUserId(out var userId))
        {
            return Result<IReadOnlyList<TenantResponse>>.Failure("Authentication is required.");
        }

        var memberships = await tenantRepository.ListUserTenantMembershipsAsync(userId, cancellationToken);
        var tenants = memberships
            .Where(membership => membership is { Status: TenantUserStatus.Active, Tenant.Status: TenantStatus.Active })
            .Select(membership => membership.Tenant!)
            .DistinctBy(tenant => tenant.Id)
            .Select(ToTenantResponse)
            .ToList();

        return Result<IReadOnlyList<TenantResponse>>.Success(tenants);
    }

    public async Task<Result<TenantResponse>> SwitchTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        if (!options.AllowTenantSwitching || options.AppMode == AppMode.OnPremSingleTenant)
        {
            return Result<TenantResponse>.Failure("Tenant switching is disabled.");
        }

        if (!TryGetUserId(out var userId))
        {
            return Result<TenantResponse>.Failure("Authentication is required.");
        }

        if (!await tenantAuthorization.CanSwitchTenantAsync(userId, tenantId, cancellationToken))
        {
            return Result<TenantResponse>.Failure("Tenant membership is required.");
        }

        var tenant = await tenantRepository.GetTenantAsync(tenantId, cancellationToken);
        return tenant is null || tenant.Status != TenantStatus.Active
            ? Result<TenantResponse>.Failure("Tenant is not available.")
            : Result<TenantResponse>.Success(ToTenantResponse(tenant));
    }

    public async Task<Result<IReadOnlyList<TenantUserResponse>>> ListCurrentTenantUsersAsync(CancellationToken cancellationToken = default)
    {
        var authorization = await RequireCurrentTenantManagementAsync(cancellationToken);
        if (!authorization.IsSuccess)
        {
            return ForwardFailure<IReadOnlyList<TenantUserResponse>>(authorization);
        }

        var users = await tenantRepository.ListTenantUsersAsync(currentTenant.TenantId, cancellationToken);
        return Result<IReadOnlyList<TenantUserResponse>>.Success(users.Select(ToTenantUserResponse).ToList());
    }

    public async Task<Result<TenantUserResponse>> AddCurrentTenantUserAsync(AddTenantUserRequest request, CancellationToken cancellationToken = default)
    {
        var authorization = await RequireCurrentTenantManagementAsync(cancellationToken);
        if (!authorization.IsSuccess)
        {
            return ForwardFailure<TenantUserResponse>(authorization);
        }

        var user = await tenantRepository.GetUserAsync(request.UserId, cancellationToken);
        if (user is null || user.DeletedAt.HasValue)
        {
            return Result<TenantUserResponse>.Failure("User not found.");
        }

        var existing = await tenantRepository.GetTenantUserAsync(currentTenant.TenantId, request.UserId, cancellationToken);
        if (existing is { Status: TenantUserStatus.Active })
        {
            return Result<TenantUserResponse>.Failure("User is already an active member of this tenant.");
        }

        var tenantUser = existing ?? new TenantUser
        {
            TenantId = currentTenant.TenantId,
            UserId = request.UserId,
            JoinedAt = DateTimeOffset.UtcNow,
            InvitedByUserId = currentUser.UserId
        };

        tenantUser.Role = request.Role;
        tenantUser.Status = TenantUserStatus.Active;

        if (existing is null)
        {
            await tenantRepository.AddTenantUserAsync(tenantUser, cancellationToken);
        }

        await auditLogger.LogAsync(new AuditLogEntry(
            currentUser.UserId,
            existing is null ? "TenantUserAdded" : "TenantUserReactivated",
            "TenantUser",
            tenantUser.Id,
            existing is null ? "Tenant user added." : "Tenant user reactivated.",
            Metadata: new Dictionary<string, object?>
            {
                ["targetUserId"] = tenantUser.UserId,
                ["role"] = tenantUser.Role.ToString()
            }), cancellationToken);
        await PublishAuthorizationChangeAsync(tenantUser.UserId, "granted", cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        tenantUser.User = user;
        return Result<TenantUserResponse>.Success(ToTenantUserResponse(tenantUser));
    }

    public async Task<Result<TenantUserResponse>> UpdateCurrentTenantUserAsync(Guid userId, UpdateTenantUserRequest request, CancellationToken cancellationToken = default)
    {
        var authorization = await RequireCurrentTenantManagementAsync(cancellationToken);
        if (!authorization.IsSuccess)
        {
            return ForwardFailure<TenantUserResponse>(authorization);
        }

        var tenantUser = await tenantRepository.GetTenantUserAsync(currentTenant.TenantId, userId, cancellationToken);
        if (tenantUser is null)
        {
            return Result<TenantUserResponse>.Failure("Tenant user not found.");
        }

        var previousRole = tenantUser.Role;
        var previousStatus = tenantUser.Status;
        var revokeSessions = false;
        if (request.Role.HasValue && tenantUser.Role != request.Role.Value)
        {
            tenantUser.Role = request.Role.Value;
            revokeSessions = true;
        }

        if (request.Status.HasValue)
        {
            tenantUser.Status = request.Status.Value;
            if (tenantUser.Status != TenantUserStatus.Active)
            {
                revokeSessions = true;
            }
        }

        if (tenantUser.Role != previousRole || tenantUser.Status != previousStatus)
        {
            await auditLogger.LogAsync(new AuditLogEntry(
                currentUser.UserId,
                tenantUser.Status != previousStatus ? "TenantUserStatusChanged" : "TenantUserRoleChanged",
                "TenantUser",
                tenantUser.Id,
                tenantUser.Status != previousStatus ? "Tenant user status changed." : "Tenant user role changed.",
                Metadata: new Dictionary<string, object?>
                {
                    ["targetUserId"] = tenantUser.UserId,
                    ["oldRole"] = previousRole.ToString(),
                    ["newRole"] = tenantUser.Role.ToString(),
                    ["oldStatus"] = previousStatus.ToString(),
                    ["newStatus"] = tenantUser.Status.ToString()
                }), cancellationToken);
        }

        if (revokeSessions)
        {
            await userSessions.RevokeUserSessionsAsync(userId, currentUser.UserId, "TenantMembershipChanged", cancellationToken: cancellationToken);
        }

        if (tenantUser.Role != previousRole || tenantUser.Status != previousStatus)
        {
            await PublishAuthorizationChangeAsync(
                userId,
                tenantUser.Status == TenantUserStatus.Active ? "membershipChanged" : "suspended",
                cancellationToken);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result<TenantUserResponse>.Success(ToTenantUserResponse(tenantUser));
    }

    public async Task<Result> RemoveCurrentTenantUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var authorization = await RequireCurrentTenantManagementAsync(cancellationToken);
        if (!authorization.IsSuccess)
        {
            return authorization;
        }

        var tenantUser = await tenantRepository.GetTenantUserAsync(currentTenant.TenantId, userId, cancellationToken);
        if (tenantUser is null)
        {
            return Result.Failure("Tenant user not found.");
        }

        tenantUser.Status = TenantUserStatus.Left;
        await auditLogger.LogAsync(new AuditLogEntry(
            currentUser.UserId,
            "TenantUserRemoved",
            "TenantUser",
            tenantUser.Id,
            "Tenant user removed.",
            Metadata: new Dictionary<string, object?> { ["targetUserId"] = tenantUser.UserId }), cancellationToken);
        await userSessions.RevokeUserSessionsAsync(userId, currentUser.UserId, "TenantMembershipRemoved", cancellationToken: cancellationToken);
        await PublishAuthorizationChangeAsync(userId, "revoked", cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    private async Task<Result> SetTenantStatusAsync(Guid tenantId, TenantStatus status, CancellationToken cancellationToken)
    {
        if (!await IsPlatformAdminAsync(cancellationToken))
        {
            return Result.Failure("PlatformAdmin access is required.");
        }

        var tenant = await tenantRepository.GetTenantAsync(tenantId, cancellationToken);
        if (tenant is null)
        {
            return Result.Failure("Tenant not found.");
        }

        tenant.Status = status;
        if (status == TenantStatus.Archived || status == TenantStatus.Deleted)
        {
            tenant.MarkDeleted(DateTimeOffset.UtcNow);
        }

        await auditLogger.LogAsync(new AuditLogEntry(
            currentUser.UserId,
            status switch
            {
                TenantStatus.Suspended => "TenantSuspended",
                TenantStatus.Active => "TenantActivated",
                TenantStatus.Archived => "TenantArchived",
                _ => "TenantStatusChanged"
            },
            "Tenant",
            tenant.Id,
            $"Tenant status changed to {status}.",
            TenantId: tenant.Id), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    private async Task<Result> RequireCurrentTenantManagementAsync(CancellationToken cancellationToken)
    {
        if (!currentTenant.IsAvailable)
        {
            const string message = "Tenant is not available for this request.";
            return Result.Failure(message, new ApplicationErrorDetail("ValidationFailed", message));
        }

        if (!TryGetUserId(out var userId))
        {
            const string message = "Authentication is required.";
            return Result.Failure(message, new ApplicationErrorDetail("AuthenticationRequired", message));
        }

        if (!await tenantAuthorization.CanManageTenantAsync(userId, currentTenant.TenantId, cancellationToken))
        {
            const string message = "Tenant Owner or Admin access is required.";
            return Result.Failure(message, new ApplicationErrorDetail("Forbidden", message));
        }

        return Result.Success();
    }

    private static Result<T> ForwardFailure<T>(Result result)
    {
        var message = result.Error ?? result.ErrorDetail?.Message ?? "Request failed.";
        return result.ErrorDetail is null
            ? Result<T>.Failure(message)
            : Result<T>.Failure(message, result.ErrorDetail);
    }

    private Task<bool> IsPlatformAdminAsync(CancellationToken cancellationToken)
    {
        return TryGetUserId(out var userId)
            ? tenantAuthorization.IsPlatformAdminAsync(userId, cancellationToken)
            : Task.FromResult(false);
    }

    private bool TryGetUserId(out Guid userId)
    {
        if (currentUser.IsAuthenticated && currentUser.UserId.HasValue)
        {
            userId = currentUser.UserId.Value;
            return true;
        }

        userId = Guid.Empty;
        return false;
    }

    private Task PublishAuthorizationChangeAsync(Guid userId, string change, CancellationToken cancellationToken)
    {
        return authorizationChanges?.PublishAsync(currentTenant.TenantId, userId, "tenant", currentTenant.TenantId, change, cancellationToken)
            ?? Task.CompletedTask;
    }

    private static TenantResponse ToTenantResponse(Tenant tenant)
    {
        return new TenantResponse(
            tenant.Id,
            tenant.Name,
            tenant.Slug,
            tenant.DisplayName,
            tenant.PrimaryDomain,
            tenant.Status,
            tenant.PlanId,
            tenant.CreatedAt,
            tenant.UpdatedAt,
            tenant.DeletedAt);
    }

    private static TenantUserResponse ToTenantUserResponse(TenantUser tenantUser)
    {
        return new TenantUserResponse(
            tenantUser.Id,
            tenantUser.TenantId,
            tenantUser.UserId,
            tenantUser.User?.DisplayName ?? string.Empty,
            tenantUser.User?.Email ?? string.Empty,
            tenantUser.Role,
            tenantUser.Status,
            tenantUser.JoinedAt,
            tenantUser.InvitedByUserId);
    }

    private static string NormalizeSlug(string slug)
    {
        return slug.Trim().ToLowerInvariant();
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
    }
}
