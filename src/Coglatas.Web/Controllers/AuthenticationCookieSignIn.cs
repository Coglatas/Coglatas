using System.Security.Claims;
using Coglatas.Application.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Coglatas.Web.Controllers;

internal static class AuthenticationCookieSignIn
{
    internal static Task SignInAsync(HttpContext httpContext, LoginResponse user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.UserId.ToString()),
            new(ClaimTypes.Name, user.DisplayName),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Role, user.SystemRole.ToString()),
            new("system_role", user.SystemRole.ToString()),
            new("session_id", user.SessionId.ToString())
        };
        claims.AddRange(user.Capabilities.Select(capability => new Claim("capability", capability)));

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);
        var properties = new AuthenticationProperties
        {
            IsPersistent = true,
            ExpiresUtc = user.ExpiresAt
        };

        return httpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            properties);
    }
}
