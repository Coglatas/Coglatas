using Coglatas.Application.Common;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Tenancy;
using Coglatas.Web.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Coglatas.Web.Controllers;

[ApiController]
[Authorize]
[Route("api/tenants")]
public sealed class TenantsController(
    ITenantService tenantService,
    IOptions<TenancyOptions> tenancyOptions,
    IOptions<SecurityOptions> securityOptions) : ApiResultControllerBase
{
    [HttpGet("current")]
    public async Task<IActionResult> Current(CancellationToken cancellationToken)
    {
        return ToActionResult(await tenantService.GetCurrentTenantAsync(cancellationToken));
    }

    [HttpGet("my")]
    public async Task<IActionResult> My(CancellationToken cancellationToken)
    {
        return ToActionResult(await tenantService.ListMyTenantsAsync(cancellationToken));
    }

    [HttpPost("switch")]
    public async Task<IActionResult> Switch(SwitchTenantRequest request, CancellationToken cancellationToken)
    {
        var result = await tenantService.SwitchTenantAsync(request.TenantId, cancellationToken);
        if (!result.IsSuccess || result.Value is null)
        {
            return BadRequest(new { error = result.Error });
        }

        Response.Cookies.Append(
            tenancyOptions.Value.TenantCookieName,
            result.Value.Slug,
            TenantCookiePolicy.Build(HttpContext, securityOptions.Value));

        return Ok(result.Value);
    }

    [HttpGet("current/users")]
    public async Task<IActionResult> CurrentUsers(CancellationToken cancellationToken)
    {
        return ToActionResult(await tenantService.ListCurrentTenantUsersAsync(cancellationToken));
    }

    [HttpPost("current/users")]
    public async Task<IActionResult> AddCurrentUser(AddTenantUserRequest request, CancellationToken cancellationToken)
    {
        return ToActionResult(await tenantService.AddCurrentTenantUserAsync(request, cancellationToken));
    }

    [HttpPatch("current/users/{userId:guid}")]
    public async Task<IActionResult> UpdateCurrentUser(Guid userId, UpdateTenantUserRequest request, CancellationToken cancellationToken)
    {
        return ToActionResult(await tenantService.UpdateCurrentTenantUserAsync(userId, request, cancellationToken));
    }

    [HttpDelete("current/users/{userId:guid}")]
    public async Task<IActionResult> RemoveCurrentUser(Guid userId, CancellationToken cancellationToken)
    {
        return ToStatusResult(await tenantService.RemoveCurrentTenantUserAsync(userId, cancellationToken));
    }

    private IActionResult ToStatusResult(Result result)
    {
        return result.IsSuccess ? Ok(new { status = "OK" }) : BadRequest(new { error = result.Error });
    }
}
