using Coglatas.Application.Auth;
using Coglatas.Web.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Coglatas.Web.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(IAuthService authService) : ControllerBase
{
    [HttpPost("login")]
    [EnableRateLimiting(HttpSecurityPolicy.LoginRateLimitPolicy)]
    public async Task<ActionResult<LoginResponse>> Login(
        LoginRequest request,
        CancellationToken cancellationToken)
    {
        var result = await authService.LoginAsync(request, cancellationToken);
        if (!result.IsSuccess || result.Value is null)
        {
            return Unauthorized(new { error = result.Error });
        }

        await AuthenticationCookieSignIn.SignInAsync(HttpContext, result.Value);
        return Ok(result.Value);
    }

    [HttpPost("logout")]
    [Authorize]
    [EnableRateLimiting(HttpSecurityPolicy.AuthenticationMutationRateLimitPolicy)]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        await authService.LogoutAsync(cancellationToken);
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Ok(new { status = "OK" });
    }

    [HttpPost("register-by-invite")]
    [EnableRateLimiting(HttpSecurityPolicy.InviteRateLimitPolicy)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LoginResponse>> RegisterByInvite(
        RegisterByInviteRequest request,
        CancellationToken cancellationToken)
    {
        var result = await authService.RegisterByInviteAsync(request, cancellationToken);
        if (!result.IsSuccess || result.Value is null)
        {
            return Problem(
                title: "Invite registration failed.",
                detail: string.IsNullOrWhiteSpace(result.Error) ? "Invite is invalid." : result.Error,
                statusCode: StatusCodes.Status404NotFound);
        }

        await AuthenticationCookieSignIn.SignInAsync(HttpContext, result.Value);
        return Ok(result.Value);
    }

    [HttpPost("change-password")]
    [Authorize]
    [EnableRateLimiting(HttpSecurityPolicy.AuthenticationMutationRateLimitPolicy)]
    public async Task<IActionResult> ChangePassword(
        ChangePasswordRequest request,
        CancellationToken cancellationToken)
    {
        var result = await authService.ChangePasswordAsync(request, cancellationToken);
        if (!result.IsSuccess)
        {
            return BadRequest(new { error = result.Error });
        }

        return Ok(new { status = "OK" });
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<CurrentUserResponse>> Me(CancellationToken cancellationToken)
    {
        var result = await authService.GetCurrentUserAsync(cancellationToken);
        if (!result.IsSuccess || result.Value is null)
        {
            return Unauthorized(new { error = result.Error });
        }

        return Ok(result.Value);
    }

    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken cancellationToken)
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return Ok(new { isAuthenticated = false });
        }

        var result = await authService.GetCurrentUserAsync(cancellationToken);
        if (!result.IsSuccess || result.Value is null)
        {
            return Ok(new { isAuthenticated = false });
        }

        return Ok(new { isAuthenticated = true, user = result.Value });
    }
}
