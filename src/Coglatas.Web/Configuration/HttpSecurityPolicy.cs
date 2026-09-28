using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Coglatas.Infrastructure.Files;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.RateLimiting;

namespace Coglatas.Web.Configuration;

/// <summary>
/// Server-owned canonical HTTP/browser security policy. Browser-facing
/// middleware and endpoint attributes should consume this policy rather than
/// duplicating header, cookie, origin, payload, or abuse-control decisions.
/// </summary>
public static class HttpSecurityPolicy
{
    public const string CorsPolicyName = "canonical-browser-origin";
    public const string LoginRateLimitPolicy = "login";
    public const string InviteRateLimitPolicy = "invite";
    public const string FileUploadRateLimitPolicy = "file-upload";
    public const string ApiTokenRateLimitPolicy = "api-token";
    public const string SearchRateLimitPolicy = "search";
    public const string AuthenticationMutationRateLimitPolicy = "auth-mutation";
    public const string TenantExportRateLimitPolicy = "tenant-export";
    public const string AdminRateLimitPolicy = "admin-security";

    public static readonly TimeSpan AuthenticationCookieLifetime = TimeSpan.FromHours(8);
    public static readonly TimeSpan HstsMaxAge = TimeSpan.FromDays(180);

    public const string ReferrerPolicy = "strict-origin-when-cross-origin";
    public const string PermissionsPolicy = "camera=(), microphone=(), geolocation=()";
    public const string FrameOptions = "DENY";
    public const string NoStoreCacheControl = "no-store, no-cache, max-age=0";

    public static void ConfigureAuthenticationCookie(
        CookieAuthenticationOptions options,
        SecurityOptions security)
    {
        options.Cookie.Name = ".Coglatas.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = security.CookieSecurePolicy;
        options.ExpireTimeSpan = AuthenticationCookieLifetime;
        options.SlidingExpiration = true;
    }

    public static void ConfigureAntiforgery(
        AntiforgeryOptions options,
        SecurityOptions security)
    {
        options.HeaderName = SecurityOptions.CsrfHeaderName;
        options.Cookie.Name = ".Coglatas.Csrf";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = security.CookieSecurePolicy;
    }

    public static void ConfigureHsts(HstsOptions options)
    {
        // IncludeSubDomains/preload are deliberately false. On-prem and school
        // deployments must not claim authority over sibling applications.
        options.MaxAge = HstsMaxAge;
        options.IncludeSubDomains = false;
        options.Preload = false;
    }

    public static void ConfigureCors(CorsOptions options, SecurityOptions security)
    {
        var origins = GetCanonicalCorsOrigins(security.AllowedCorsOrigins);
        options.AddPolicy(CorsPolicyName, policy =>
        {
            if (origins.Count == 0)
            {
                policy.SetIsOriginAllowed(_ => false);
                return;
            }

            policy.WithOrigins(origins.ToArray())
                .AllowAnyHeader()
                .AllowAnyMethod();

            if (security.AllowCorsCredentials)
            {
                policy.AllowCredentials();
            }
        });
    }

    public static void ConfigureRateLimiting(RateLimiterOptions options)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = async (rejection, cancellationToken) =>
        {
            if (rejection.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
            {
                var seconds = Math.Max(1L, (long)Math.Ceiling(retryAfter.TotalSeconds));
                rejection.HttpContext.Response.Headers["Retry-After"] =
                    seconds.ToString(CultureInfo.InvariantCulture);
            }

            rejection.HttpContext.Response.ContentType = "application/json";
            await rejection.HttpContext.Response.WriteAsJsonAsync(new
            {
                requestId = rejection.HttpContext.TraceIdentifier,
                error = new
                {
                    code = "RateLimitExceeded",
                    message = "Too many requests. Retry after the indicated interval."
                }
            }, cancellationToken);
        };

        AddFixedWindowPolicy(options, LoginRateLimitPolicy, 10, TimeSpan.FromMinutes(1));
        AddFixedWindowPolicy(options, InviteRateLimitPolicy, 10, TimeSpan.FromMinutes(1));
        AddFixedWindowPolicy(options, FileUploadRateLimitPolicy, 20, TimeSpan.FromMinutes(1));
        AddFixedWindowPolicy(options, ApiTokenRateLimitPolicy, 30, TimeSpan.FromMinutes(1));
        AddFixedWindowPolicy(options, SearchRateLimitPolicy, 60, TimeSpan.FromMinutes(1));
        AddFixedWindowPolicy(options, AuthenticationMutationRateLimitPolicy, 20, TimeSpan.FromMinutes(1));
        AddFixedWindowPolicy(options, TenantExportRateLimitPolicy, 6, TimeSpan.FromMinutes(10));
        AddFixedWindowPolicy(options, AdminRateLimitPolicy, 120, TimeSpan.FromMinutes(1));
    }

    public static string GetRateLimitPartitionKey(HttpContext context)
    {
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (context.User.Identity?.IsAuthenticated == true &&
            Guid.TryParse(userId, out var parsedUserId) &&
            parsedUserId != Guid.Empty)
        {
            return $"user:{parsedUserId:D}";
        }

        // Never read X-Forwarded-For directly here. UseForwardedHeaders may
        // update RemoteIpAddress only after the request came through an
        // explicitly trusted proxy boundary.
        return context.Connection.RemoteIpAddress is { } remoteIp
            ? $"ip:{remoteIp}"
            : "ip:unknown";
    }

    public static string BuildContentSecurityPolicy(HttpRequest request)
    {
        var requestHost = request.Host.ToUriComponent();
        var websocketSources = string.IsNullOrWhiteSpace(requestHost)
            ? string.Empty
            : $" ws://{requestHost} wss://{requestHost}";

        // Angular currently needs inline runtime styles. Keep this as the one
        // reviewed unsafe CSP exception; scripts remain self-only and eval-free.
        return "default-src 'self'; " +
               "script-src 'self'; " +
               "style-src 'self' 'unsafe-inline'; " +
               "img-src 'self' data: https:; " +
               "font-src 'self' data:; " +
               $"connect-src 'self'{websocketSources}; " +
               "base-uri 'self'; " +
               "frame-ancestors 'none'; " +
               "object-src 'none'; " +
               "form-action 'self'";
    }

    public static bool ShouldPreventCaching(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            return true;
        }

        if (context.Request.Path.StartsWithSegments("/api/auth") ||
            context.Request.Path.StartsWithSegments("/api/security"))
        {
            return true;
        }

        return context.Response.Headers.ContainsKey("Set-Cookie");
    }

    public static bool IsApprovedRedirectLocation(HttpRequest request, string? location)
    {
        if (string.IsNullOrWhiteSpace(location) ||
            location.Any(character => char.IsControl(character) || character == '\\'))
        {
            return false;
        }

        if (location.StartsWith("/", StringComparison.Ordinal))
        {
            return !location.StartsWith("//", StringComparison.Ordinal);
        }

        if (!Uri.TryCreate(location, UriKind.Absolute, out var target) ||
            !string.Equals(target.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(target.UserInfo) ||
            !string.Equals(target.Host, request.Host.Host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (request.Host.Port is null)
        {
            return target.IsDefaultPort || target.Port == 443;
        }

        if (request.Host.Port == 80)
        {
            return target.Port == 443;
        }

        return target.Port == request.Host.Port;
    }

    public static bool IsRedirectStatusCode(int statusCode) =>
        statusCode is StatusCodes.Status301MovedPermanently or
            StatusCodes.Status302Found or
            StatusCodes.Status303SeeOther or
            StatusCodes.Status307TemporaryRedirect or
            StatusCodes.Status308PermanentRedirect;

    public static IReadOnlyList<string> GetConfigurationErrors(
        SecurityOptions security,
        FileStorageOptions fileStorage,
        IConfiguration configuration,
        bool isProduction)
    {
        var errors = new List<string>();

        if (security.MaxRequestBodySizeBytes <= 0)
        {
            errors.Add("Security:MaxRequestBodySizeBytes must be positive.");
        }

        if (security.MaxMultipartBodySizeBytes <= 0)
        {
            errors.Add("Security:MaxMultipartBodySizeBytes must be positive.");
        }
        else if (security.MaxRequestBodySizeBytes > 0 &&
                 security.MaxMultipartBodySizeBytes > security.MaxRequestBodySizeBytes)
        {
            errors.Add("Security:MaxMultipartBodySizeBytes must not exceed Security:MaxRequestBodySizeBytes.");
        }

        if (fileStorage.MaxFileSizeBytes > 0 &&
            security.MaxMultipartBodySizeBytes > 0 &&
            security.MaxMultipartBodySizeBytes <= fileStorage.MaxFileSizeBytes)
        {
            errors.Add("Security:MaxMultipartBodySizeBytes must be greater than FileStorage:MaxFileSizeBytes so multipart framing fits inside the HTTP limit.");
        }

        foreach (var origin in security.AllowedCorsOrigins)
        {
            if (!TryParseOrigin(origin, out var parsedOrigin))
            {
                errors.Add("Security:AllowedCorsOrigins must contain only explicit http/https origins without wildcards, paths, query strings, fragments, or credentials.");
                continue;
            }

            if (isProduction &&
                (!string.Equals(parsedOrigin.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                 parsedOrigin.IsLoopback))
            {
                errors.Add("Production Security:AllowedCorsOrigins must use HTTPS and must not contain localhost/loopback development origins.");
            }
        }

        if (isProduction)
        {
            if (!security.EnableCsrfProtection)
            {
                errors.Add("Security:EnableCsrfProtection must be true in production.");
            }

            if (!security.EnableRateLimiting)
            {
                errors.Add("Security:EnableRateLimiting must be true in production.");
            }

            var allowedHosts = (configuration["AllowedHosts"] ?? string.Empty)
                .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (allowedHosts.Length == 0 || allowedHosts.Any(host => string.Equals(host, "*", StringComparison.Ordinal)))
            {
                errors.Add("AllowedHosts must be an explicit production host allowlist; wildcard '*' is not permitted.");
            }
        }

        return errors.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static void AddFixedWindowPolicy(
        RateLimiterOptions options,
        string policyName,
        int permitLimit,
        TimeSpan window)
    {
        options.AddPolicy<string>(policyName, context =>
            RateLimitPartition.GetFixedWindowLimiter(
                $"{policyName}:{GetRateLimitPartitionKey(context)}",
                _ => new FixedWindowRateLimiterOptions
                {
                    AutoReplenishment = true,
                    PermitLimit = permitLimit,
                    Window = window,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 0
                }));
    }

    private static IReadOnlyList<string> GetCanonicalCorsOrigins(IEnumerable<string>? configuredOrigins)
    {
        var origins = new List<string>();
        foreach (var configuredOrigin in configuredOrigins ?? [])
        {
            if (TryParseOrigin(configuredOrigin, out var origin))
            {
                origins.Add(origin.GetLeftPart(UriPartial.Authority));
            }
        }

        return origins.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool TryParseOrigin(string? value, out Uri origin)
    {
        origin = null!;
        if (string.IsNullOrWhiteSpace(value) ||
            value.Contains('*') ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(parsed.Host) ||
            !string.IsNullOrEmpty(parsed.UserInfo) ||
            !string.IsNullOrEmpty(parsed.Query) ||
            !string.IsNullOrEmpty(parsed.Fragment) ||
            parsed.AbsolutePath != "/")
        {
            return false;
        }

        origin = parsed;
        return true;
    }
}
