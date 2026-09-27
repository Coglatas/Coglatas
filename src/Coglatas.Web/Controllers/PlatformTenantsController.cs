using Coglatas.Application.Common;
using Coglatas.Application.Tenancy;
using Coglatas.Web.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Coglatas.Web.Controllers;

[ApiController]
[Authorize(Roles = "PlatformAdmin,SystemAdmin")]
[EnableRateLimiting(HttpSecurityPolicy.AdminRateLimitPolicy)]
[Route("api/platform/tenants")]
public sealed class PlatformTenantsController(ITenantService tenantService) : ApiResultControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        return ToActionResult(await tenantService.ListPlatformTenantsAsync(cancellationToken));
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateTenantRequest request, CancellationToken cancellationToken)
    {
        return ToActionResult(await tenantService.CreateTenantAsync(request, cancellationToken));
    }

    [HttpGet("{tenantId:guid}")]
    public async Task<IActionResult> Get(Guid tenantId, CancellationToken cancellationToken)
    {
        return ToActionResult(await tenantService.GetPlatformTenantAsync(tenantId, cancellationToken));
    }

    [HttpPatch("{tenantId:guid}")]
    public async Task<IActionResult> Update(Guid tenantId, UpdateTenantRequest request, CancellationToken cancellationToken)
    {
        return ToActionResult(await tenantService.UpdateTenantAsync(tenantId, request, cancellationToken));
    }

    [HttpPost("{tenantId:guid}/suspend")]
    public async Task<IActionResult> Suspend(Guid tenantId, CancellationToken cancellationToken)
    {
        return ToStatusResult(await tenantService.SuspendTenantAsync(tenantId, cancellationToken));
    }

    [HttpPost("{tenantId:guid}/activate")]
    public async Task<IActionResult> Activate(Guid tenantId, CancellationToken cancellationToken)
    {
        return ToStatusResult(await tenantService.ActivateTenantAsync(tenantId, cancellationToken));
    }

    [HttpPost("{tenantId:guid}/archive")]
    public async Task<IActionResult> Archive(Guid tenantId, CancellationToken cancellationToken)
    {
        return ToStatusResult(await tenantService.ArchiveTenantAsync(tenantId, cancellationToken));
    }

    private IActionResult ToStatusResult(Result result)
    {
        return result.IsSuccess ? Ok(new { status = "OK" }) : BadRequest(new { error = result.Error });
    }
}
