using System.Text.Json;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Files;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;

namespace Coglatas.Application.TenantAdministration;

public sealed class TenantAdministrationService(
    ITenantPlanRepository tenantPlans,
    ITenantRepository tenants,
    IFeatureFlagService featureFlags,
    IQuotaService quotaService,
    ICurrentTenant currentTenant,
    ICurrentUser currentUser,
    IClock clock,
    IAuditLogger auditLogger,
    IUnitOfWork unitOfWork,
    IFileObjectService fileObjects) : ITenantAdministrationService
{
    public async Task<Result<PlatformOverviewResponse>> GetPlatformOverviewAsync(CancellationToken cancellationToken = default)
    {
        if (!IsPlatformAdmin())
        {
            return Result<PlatformOverviewResponse>.Failure("PlatformAdmin access is required.");
        }

        var tenantList = await tenants.ListTenantsAsync(cancellationToken);
        var tenantUsage = new List<TenantUsageResponse>();
        foreach (var tenant in tenantList)
        {
            tenantUsage.Add(ToUsageResponse(await quotaService.GetCurrentUsageAsync(tenant.Id, cancellationToken)));
        }

        return Result<PlatformOverviewResponse>.Success(new PlatformOverviewResponse(
            tenantList.Count,
            tenantList.Count(tenant => tenant.Status == TenantStatus.Active),
            tenantList.Count(tenant => tenant.Status == TenantStatus.Suspended),
            tenantUsage.Sum(usage => usage.TotalUserCount),
            tenantUsage.Sum(usage => usage.StorageUsedBytes),
            tenantUsage.Sum(usage => usage.ProjectCount),
            tenantUsage));
    }

    public async Task<Result<PlatformUsageResponse>> GetPlatformUsageAsync(CancellationToken cancellationToken = default)
    {
        if (!IsPlatformAdmin())
        {
            return Result<PlatformUsageResponse>.Failure("PlatformAdmin access is required.");
        }

        var tenantList = await tenants.ListTenantsAsync(cancellationToken);
        var tenantUsage = new List<TenantUsageResponse>();
        foreach (var tenant in tenantList)
        {
            tenantUsage.Add(ToUsageResponse(await quotaService.GetCurrentUsageAsync(tenant.Id, cancellationToken)));
        }

        return Result<PlatformUsageResponse>.Success(new PlatformUsageResponse(
            tenantUsage,
            tenantUsage.Sum(usage => usage.ActiveUserCount),
            tenantUsage.Sum(usage => usage.TotalUserCount),
            tenantUsage.Sum(usage => usage.ProjectCount),
            tenantUsage.Sum(usage => usage.TaskCount),
            tenantUsage.Sum(usage => usage.FileCount),
            tenantUsage.Sum(usage => usage.StorageUsedBytes),
            tenantUsage.Sum(usage => usage.ApiRequestCount)));
    }

    public async Task<Result<TenantOverviewResponse>> GetCurrentTenantOverviewAsync(CancellationToken cancellationToken = default)
    {
        if (!await CanViewCurrentTenantAdministrationAsync(cancellationToken))
        {
            return Result<TenantOverviewResponse>.Failure("TenantAdmin access is required.");
        }

        var usage = await quotaService.GetCurrentUsageAsync(currentTenant.TenantId, cancellationToken);
        var features = await featureFlags.GetEnabledFeaturesAsync(currentTenant.TenantId, cancellationToken);
        var subscription = await tenantPlans.GetActiveSubscriptionAsync(currentTenant.TenantId, cancellationToken);

        return Result<TenantOverviewResponse>.Success(new TenantOverviewResponse(
            currentTenant.TenantId,
            usage.ActiveUserCount,
            usage.StorageUsedBytes,
            usage.ProjectCount,
            usage.TaskCount,
            usage.FileCount,
            features,
            subscription is null ? null : ToSubscriptionResponse(subscription)));
    }

    public async Task<Result<TenantSettingsResponse>> GetCurrentTenantSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (!await CanViewCurrentTenantAdministrationAsync(cancellationToken))
        {
            return Result<TenantSettingsResponse>.Failure("TenantAdmin access is required.");
        }

        var settings = await tenantPlans.GetOrCreateTenantSettingsAsync(currentTenant.TenantId, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result<TenantSettingsResponse>.Success(ToSettingsResponse(settings));
    }

    public async Task<Result<TenantSettingsResponse>> UpdateCurrentTenantSettingsAsync(UpdateTenantSettingsRequest request, CancellationToken cancellationToken = default)
    {
        if (!await CanManageCurrentTenantAdministrationAsync(cancellationToken))
        {
            return Result<TenantSettingsResponse>.Failure("TenantAdmin access is required.");
        }

        var validation = ValidateSettings(request);
        if (!validation.IsSuccess)
        {
            return Result<TenantSettingsResponse>.Failure(validation.Error!);
        }

        if (request.LogoFileId.HasValue)
        {
            var logo = await fileObjects.GetFileObjectAsync(request.LogoFileId.Value, cancellationToken);
            if (!logo.IsSuccess || logo.Value is null ||
                logo.Value.Status != nameof(FileObjectStatus.Active) || logo.Value.DeletedAt.HasValue ||
                logo.Value.WorkspaceId.HasValue || logo.Value.GroupId.HasValue || logo.Value.ProjectId.HasValue)
            {
                return Result<TenantSettingsResponse>.Failure("Logo file is not available.");
            }
        }

        var settings = await tenantPlans.GetOrCreateTenantSettingsAsync(currentTenant.TenantId, cancellationToken);
        ApplySettings(settings, request);
        await auditLogger.LogAsync(new AuditLogEntry(currentUser.UserId, "TenantSettingsChanged", "TenantSettings", settings.Id, "Tenant settings changed."), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result<TenantSettingsResponse>.Success(ToSettingsResponse(settings));
    }

    public async Task<Result<TenantFeaturesResponse>> GetCurrentTenantFeaturesAsync(CancellationToken cancellationToken = default)
    {
        if (!currentTenant.IsAvailable)
        {
            return Result<TenantFeaturesResponse>.Failure("A tenant context is required.");
        }

        var enabled = await featureFlags.GetEnabledFeaturesAsync(currentTenant.TenantId, cancellationToken);
        return Result<TenantFeaturesResponse>.Success(new TenantFeaturesResponse(currentTenant.TenantId, enabled));
    }

    public async Task<Result<TenantUsageResponse>> GetCurrentTenantUsageAsync(CancellationToken cancellationToken = default)
    {
        if (!await CanViewCurrentTenantAdministrationAsync(cancellationToken))
        {
            return Result<TenantUsageResponse>.Failure("TenantAdmin access is required.");
        }

        return Result<TenantUsageResponse>.Success(ToUsageResponse(await quotaService.GetCurrentUsageAsync(currentTenant.TenantId, cancellationToken)));
    }

    public async Task<Result<TenantUsageResponse>> GetPlatformTenantUsageAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        if (!IsPlatformAdmin())
        {
            return Result<TenantUsageResponse>.Failure("PlatformAdmin access is required.");
        }

        return Result<TenantUsageResponse>.Success(ToUsageResponse(await quotaService.GetCurrentUsageAsync(tenantId, cancellationToken)));
    }

    public async Task<Result<IReadOnlyList<PlanResponse>>> ListPlansAsync(CancellationToken cancellationToken = default)
    {
        if (!IsPlatformAdmin())
        {
            return Result<IReadOnlyList<PlanResponse>>.Failure("PlatformAdmin access is required.");
        }

        var plans = await tenantPlans.ListPlansAsync(cancellationToken);
        return Result<IReadOnlyList<PlanResponse>>.Success(plans.Select(ToPlanResponse).ToList());
    }

    public async Task<Result<PlanResponse>> CreatePlanAsync(UpsertPlanRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsPlatformAdmin())
        {
            return Result<PlanResponse>.Failure("PlatformAdmin access is required.");
        }

        var validation = ValidatePlan(request, requireName: true);
        if (!validation.IsSuccess)
        {
            return Result<PlanResponse>.Failure(validation.Error!);
        }

        var plan = new Plan();
        ApplyPlan(plan, request);
        await tenantPlans.AddPlanAsync(plan, cancellationToken);
        await auditLogger.LogAsync(new AuditLogEntry(currentUser.UserId, "PlanCreated", "Plan", plan.Id, "Plan created."), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result<PlanResponse>.Success(ToPlanResponse(plan));
    }

    public async Task<Result<PlanResponse>> UpdatePlanAsync(Guid planId, UpsertPlanRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsPlatformAdmin())
        {
            return Result<PlanResponse>.Failure("PlatformAdmin access is required.");
        }

        var validation = ValidatePlan(request, requireName: false);
        if (!validation.IsSuccess)
        {
            return Result<PlanResponse>.Failure(validation.Error!);
        }

        var plan = await tenantPlans.GetPlanAsync(planId, cancellationToken);
        if (plan is null)
        {
            return Result<PlanResponse>.Failure("Plan not found.");
        }

        ApplyPlan(plan, request);
        await auditLogger.LogAsync(new AuditLogEntry(currentUser.UserId, "PlanUpdated", "Plan", plan.Id, "Plan updated."), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result<PlanResponse>.Success(ToPlanResponse(plan));
    }

    public async Task<Result> ArchivePlanAsync(Guid planId, CancellationToken cancellationToken = default)
    {
        if (!IsPlatformAdmin())
        {
            return Result.Failure("PlatformAdmin access is required.");
        }

        var plan = await tenantPlans.GetPlanAsync(planId, cancellationToken);
        if (plan is null)
        {
            return Result.Failure("Plan not found.");
        }

        plan.Status = PlanStatus.Archived;
        await auditLogger.LogAsync(new AuditLogEntry(currentUser.UserId, "PlanArchived", "Plan", plan.Id, "Plan archived."), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result<SubscriptionResponse>> GetTenantSubscriptionAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        if (!IsPlatformAdmin())
        {
            return Result<SubscriptionResponse>.Failure("PlatformAdmin access is required.");
        }

        var subscription = await tenantPlans.GetActiveSubscriptionAsync(tenantId, cancellationToken);
        return subscription is null
            ? Result<SubscriptionResponse>.Failure("Subscription not found.")
            : Result<SubscriptionResponse>.Success(ToSubscriptionResponse(subscription));
    }

    public async Task<Result<SubscriptionResponse>> UpdateTenantSubscriptionAsync(Guid tenantId, UpdateSubscriptionRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsPlatformAdmin())
        {
            return Result<SubscriptionResponse>.Failure("PlatformAdmin access is required.");
        }

        var plan = await tenantPlans.GetPlanAsync(request.PlanId, cancellationToken);
        if (plan is null)
        {
            return Result<SubscriptionResponse>.Failure("Plan not found.");
        }

        var subscription = await tenantPlans.GetActiveSubscriptionAsync(tenantId, cancellationToken);
        if (subscription is null)
        {
            subscription = new Subscription
            {
                TenantId = tenantId,
                StartedAt = request.StartedAt ?? clock.UtcNow
            };
            await tenantPlans.AddSubscriptionAsync(subscription, cancellationToken);
        }

        subscription.PlanId = request.PlanId;
        subscription.Plan = plan;
        subscription.Status = request.Status;
        subscription.StartedAt = request.StartedAt ?? subscription.StartedAt;
        subscription.EndsAt = request.EndsAt;
        subscription.TrialEndsAt = request.TrialEndsAt;

        await auditLogger.LogAsync(new AuditLogEntry(currentUser.UserId, "SubscriptionChanged", "Subscription", subscription.Id, "Tenant subscription changed."), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result<SubscriptionResponse>.Success(ToSubscriptionResponse(subscription));
    }

    private async Task<bool> CanViewCurrentTenantAdministrationAsync(CancellationToken cancellationToken)
    {
        return await CanManageCurrentTenantAdministrationAsync(cancellationToken);
    }

    private async Task<bool> CanManageCurrentTenantAdministrationAsync(CancellationToken cancellationToken)
    {
        if (!currentTenant.IsAvailable || !currentUser.IsAuthenticated || !currentUser.UserId.HasValue)
        {
            return false;
        }

        if (IsPlatformAdmin())
        {
            return true;
        }

        var membership = await tenants.GetTenantUserAsync(currentTenant.TenantId, currentUser.UserId.Value, cancellationToken);
        return membership is { Status: TenantUserStatus.Active, Role: TenantUserRole.Owner or TenantUserRole.Admin };
    }

    private bool IsPlatformAdmin()
    {
        return currentUser is { IsAuthenticated: true, SystemRole: SystemRole.PlatformAdmin };
    }

    private static Result ValidateSettings(UpdateTenantSettingsRequest request)
    {
        if (request.DisplayName is { Length: > 160 })
        {
            return Result.Failure("DisplayName must be 160 characters or fewer.");
        }

        if (request.ThemeColor is { Length: > 40 })
        {
            return Result.Failure("ThemeColor must be 40 characters or fewer.");
        }

        if (request.StorageQuotaBytes is < 0 || request.UserLimit is < 0 || request.ProjectLimit is < 0 || request.FileUploadLimitBytes is < 0)
        {
            return Result.Failure("Quota limits cannot be negative.");
        }

        if (!IsValidJsonObject(request.FeatureFlagsJson) || !IsValidJsonObject(request.NotificationSettingsJson))
        {
            return Result.Failure("Settings JSON must be valid JSON objects.");
        }

        return Result.Success();
    }

    private static Result ValidatePlan(UpsertPlanRequest request, bool requireName)
    {
        if (requireName && string.IsNullOrWhiteSpace(request.Name))
        {
            return Result.Failure("Plan name is required.");
        }

        if (request.MaxUsers is < 0 || request.MaxStorageBytes is < 0 || request.MaxProjects is < 0 ||
            request.MaxExternalGuests is < 0 || request.MaxApiRequestsPerDay is < 0 || request.PriceMonthly is < 0)
        {
            return Result.Failure("Plan limits and price cannot be negative.");
        }

        if (!IsValidJsonArray(request.EnabledFeaturesJson))
        {
            return Result.Failure("EnabledFeaturesJson must be a valid JSON array.");
        }

        return Result.Success();
    }

    private static void ApplySettings(TenantSettings settings, UpdateTenantSettingsRequest request)
    {
        if (request.DisplayName is not null)
        {
            settings.DisplayName = request.DisplayName.Trim();
        }

        settings.LogoFileId = request.LogoFileId ?? settings.LogoFileId;
        settings.ThemeColor = request.ThemeColor?.Trim() ?? settings.ThemeColor;
        settings.DefaultLocale = request.DefaultLocale?.Trim() ?? settings.DefaultLocale;
        settings.TimeZone = request.TimeZone?.Trim() ?? settings.TimeZone;
        settings.InvitationMode = request.InvitationMode ?? settings.InvitationMode;
        settings.StorageQuotaBytes = request.StorageQuotaBytes ?? settings.StorageQuotaBytes;
        settings.UserLimit = request.UserLimit ?? settings.UserLimit;
        settings.ProjectLimit = request.ProjectLimit ?? settings.ProjectLimit;
        settings.FileUploadLimitBytes = request.FileUploadLimitBytes ?? settings.FileUploadLimitBytes;
        settings.FeatureFlagsJson = request.FeatureFlagsJson ?? settings.FeatureFlagsJson;
        settings.NotificationSettingsJson = request.NotificationSettingsJson ?? settings.NotificationSettingsJson;
    }

    private static void ApplyPlan(Plan plan, UpsertPlanRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            plan.Name = request.Name.Trim();
        }

        plan.Description = request.Description?.Trim() ?? plan.Description;
        plan.MaxUsers = request.MaxUsers ?? plan.MaxUsers;
        plan.MaxStorageBytes = request.MaxStorageBytes ?? plan.MaxStorageBytes;
        plan.MaxProjects = request.MaxProjects ?? plan.MaxProjects;
        plan.MaxExternalGuests = request.MaxExternalGuests ?? plan.MaxExternalGuests;
        plan.MaxApiRequestsPerDay = request.MaxApiRequestsPerDay ?? plan.MaxApiRequestsPerDay;
        plan.EnabledFeaturesJson = request.EnabledFeaturesJson ?? plan.EnabledFeaturesJson;
        plan.PriceMonthly = request.PriceMonthly ?? plan.PriceMonthly;
        plan.Status = request.Status ?? plan.Status;
    }

    private static bool IsValidJsonObject(string? json) => IsValidJson(json, JsonValueKind.Object);

    private static bool IsValidJsonArray(string? json) => IsValidJson(json, JsonValueKind.Array);

    private static bool IsValidJson(string? json, JsonValueKind expectedKind)
    {
        if (json is null)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == expectedKind;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static TenantSettingsResponse ToSettingsResponse(TenantSettings settings)
    {
        return new TenantSettingsResponse(
            settings.Id,
            settings.TenantId,
            settings.DisplayName,
            settings.LogoFileId,
            settings.ThemeColor,
            settings.DefaultLocale,
            settings.TimeZone,
            settings.InvitationMode,
            settings.StorageQuotaBytes,
            settings.UserLimit,
            settings.ProjectLimit,
            settings.FileUploadLimitBytes,
            settings.FeatureFlagsJson,
            settings.NotificationSettingsJson,
            settings.CreatedAt,
            settings.UpdatedAt);
    }

    private static TenantUsageResponse ToUsageResponse(TenantUsageSnapshot usage)
    {
        return new TenantUsageResponse(usage.TenantId, usage.ActiveUserCount, usage.TotalUserCount, usage.ProjectCount, usage.TaskCount, usage.FileCount, usage.StorageUsedBytes, usage.ApiRequestCount);
    }

    private static PlanResponse ToPlanResponse(Plan plan)
    {
        return new PlanResponse(plan.Id, plan.Name, plan.Description, plan.MaxUsers, plan.MaxStorageBytes, plan.MaxProjects, plan.MaxExternalGuests, plan.MaxApiRequestsPerDay, plan.EnabledFeaturesJson, plan.PriceMonthly, plan.Status, plan.CreatedAt, plan.UpdatedAt);
    }

    private static SubscriptionResponse ToSubscriptionResponse(Subscription subscription)
    {
        return new SubscriptionResponse(subscription.Id, subscription.TenantId, subscription.PlanId, subscription.Plan?.Name, subscription.Status, subscription.StartedAt, subscription.EndsAt, subscription.TrialEndsAt, subscription.CreatedAt, subscription.UpdatedAt);
    }
}
