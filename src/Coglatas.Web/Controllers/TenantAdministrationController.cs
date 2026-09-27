using Coglatas.Application.TenantAdministration;
using Coglatas.Application.Tenancy;
using Coglatas.Web.Configuration;
using Coglatas.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Coglatas.Web.Controllers;

[ApiController]
[Authorize]
[EnableRateLimiting(HttpSecurityPolicy.AdminRateLimitPolicy)]
public sealed class TenantAdministrationController(
    ITenantAdministrationService tenantAdministration,
    ITenantService tenantService) : ApiResultControllerBase
{
    [HttpGet("api/tenant/overview")]
    public async Task<IActionResult> GetTenantOverview(CancellationToken cancellationToken)
    {
        return ToActionResult(await tenantAdministration.GetCurrentTenantOverviewAsync(cancellationToken));
    }

    [HttpGet("api/tenant/settings")]
    public async Task<IActionResult> GetSettings(CancellationToken cancellationToken)
    {
        return ToActionResult(await tenantAdministration.GetCurrentTenantSettingsAsync(cancellationToken));
    }

    [HttpPatch("api/tenant/settings")]
    public async Task<IActionResult> UpdateSettings(UpdateTenantSettingsRequest request, CancellationToken cancellationToken)
    {
        return ToActionResult(await tenantAdministration.UpdateCurrentTenantSettingsAsync(request, cancellationToken));
    }

    [HttpGet("api/tenant/features")]
    public async Task<IActionResult> GetFeatures(CancellationToken cancellationToken)
    {
        return ToActionResult(await tenantAdministration.GetCurrentTenantFeaturesAsync(cancellationToken));
    }

    [HttpGet("api/tenant/usage")]
    public async Task<IActionResult> GetUsage(CancellationToken cancellationToken)
    {
        return ToActionResult(await tenantAdministration.GetCurrentTenantUsageAsync(cancellationToken));
    }

    [HttpGet("api/tenant/users")]
    public async Task<IActionResult> ListTenantUsers(CancellationToken cancellationToken)
    {
        return ToActionResult(await tenantService.ListCurrentTenantUsersAsync(cancellationToken));
    }

    [HttpPost("api/tenant/users/invite")]
    public async Task<IActionResult> InviteTenantUser(InviteTenantUserRequest request, CancellationToken cancellationToken)
    {
        return ToActionResult(await tenantService.AddCurrentTenantUserAsync(new AddTenantUserRequest(request.UserId, request.Role), cancellationToken));
    }

    [HttpPatch("api/tenant/users/{userId:guid}")]
    public async Task<IActionResult> UpdateTenantUser(Guid userId, UpdateTenantUserRequest request, CancellationToken cancellationToken)
    {
        return ToActionResult(await tenantService.UpdateCurrentTenantUserAsync(userId, request, cancellationToken));
    }

    [HttpPost("api/tenant/users/{userId:guid}/suspend")]
    public async Task<IActionResult> SuspendTenantUser(Guid userId, CancellationToken cancellationToken)
    {
        return ToActionResult(await tenantService.UpdateCurrentTenantUserAsync(userId, new UpdateTenantUserRequest(null, TenantUserStatus.Suspended), cancellationToken));
    }

    [HttpPost("api/tenant/users/{userId:guid}/activate")]
    public async Task<IActionResult> ActivateTenantUser(Guid userId, CancellationToken cancellationToken)
    {
        return ToActionResult(await tenantService.UpdateCurrentTenantUserAsync(userId, new UpdateTenantUserRequest(null, TenantUserStatus.Active), cancellationToken));
    }

    [HttpDelete("api/tenant/users/{userId:guid}")]
    public async Task<IActionResult> DeleteTenantUser(Guid userId, CancellationToken cancellationToken)
    {
        return OkOrBad(await tenantService.RemoveCurrentTenantUserAsync(userId, cancellationToken));
    }

    [HttpGet("api/platform/overview")]
    [Authorize(Roles = "PlatformAdmin,SystemAdmin")]
    public async Task<IActionResult> GetPlatformOverview(CancellationToken cancellationToken)
    {
        return ToActionResult(await tenantAdministration.GetPlatformOverviewAsync(cancellationToken));
    }

    [HttpGet("api/platform/plans")]
    [Authorize(Roles = "PlatformAdmin,SystemAdmin")]
    public async Task<IActionResult> ListPlans(CancellationToken cancellationToken)
    {
        return ToActionResult(await tenantAdministration.ListPlansAsync(cancellationToken));
    }

    [HttpPost("api/platform/plans")]
    [Authorize(Roles = "PlatformAdmin,SystemAdmin")]
    public async Task<IActionResult> CreatePlan(UpsertPlanRequest request, CancellationToken cancellationToken)
    {
        return ToActionResult(await tenantAdministration.CreatePlanAsync(request, cancellationToken));
    }

    [HttpPatch("api/platform/plans/{planId:guid}")]
    [Authorize(Roles = "PlatformAdmin,SystemAdmin")]
    public async Task<IActionResult> UpdatePlan(Guid planId, UpsertPlanRequest request, CancellationToken cancellationToken)
    {
        return ToActionResult(await tenantAdministration.UpdatePlanAsync(planId, request, cancellationToken));
    }

    [HttpDelete("api/platform/plans/{planId:guid}")]
    [Authorize(Roles = "PlatformAdmin,SystemAdmin")]
    public async Task<IActionResult> ArchivePlan(Guid planId, CancellationToken cancellationToken)
    {
        return OkOrBad(await tenantAdministration.ArchivePlanAsync(planId, cancellationToken));
    }

    [HttpGet("api/platform/tenants/{tenantId:guid}/subscription")]
    [Authorize(Roles = "PlatformAdmin,SystemAdmin")]
    public async Task<IActionResult> GetSubscription(Guid tenantId, CancellationToken cancellationToken)
    {
        return ToActionResult(await tenantAdministration.GetTenantSubscriptionAsync(tenantId, cancellationToken));
    }

    [HttpPatch("api/platform/tenants/{tenantId:guid}/subscription")]
    [Authorize(Roles = "PlatformAdmin,SystemAdmin")]
    public async Task<IActionResult> UpdateSubscription(Guid tenantId, UpdateSubscriptionRequest request, CancellationToken cancellationToken)
    {
        return ToActionResult(await tenantAdministration.UpdateTenantSubscriptionAsync(tenantId, request, cancellationToken));
    }

    [HttpGet("api/platform/tenants/{tenantId:guid}/usage")]
    [Authorize(Roles = "PlatformAdmin,SystemAdmin")]
    public async Task<IActionResult> GetPlatformUsage(Guid tenantId, CancellationToken cancellationToken)
    {
        return ToActionResult(await tenantAdministration.GetPlatformTenantUsageAsync(tenantId, cancellationToken));
    }

    [HttpGet("api/platform/usage")]
    [Authorize(Roles = "PlatformAdmin,SystemAdmin")]
    public async Task<IActionResult> GetPlatformUsage(CancellationToken cancellationToken)
    {
        return ToActionResult(await tenantAdministration.GetPlatformUsageAsync(cancellationToken));
    }

}
