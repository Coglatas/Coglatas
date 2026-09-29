
namespace Coglatas.Web.Configuration;

/// <summary>
/// Builds the tenant-selection cookie from the same secure-cookie policy used by
/// the rest of the web security boundary.
/// </summary>
public static class TenantCookiePolicy
{
    public static CookieOptions Build(HttpContext context, SecurityOptions securityOptions)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(securityOptions);

        return new CookieBuilder
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            SecurePolicy = securityOptions.CookieSecurePolicy,
            IsEssential = true
        }.Build(context);
    }
}
