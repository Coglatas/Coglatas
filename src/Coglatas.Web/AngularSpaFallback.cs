using Coglatas.Web.Models;

namespace Coglatas.Web;

public static class AngularSpaFallback
{
    public const string BuildMarkerFileName = "angular-app.marker";
    public const string AppRequestPath = "/app";

    private static readonly PathString[] BackendOwnedPrefixes =
    [
        new("/api"),
        new("/health"),
        new("/healthz"),
        new("/metrics"),
        new("/swagger"),
        new("/hangfire"),
        new("/signin-google"),
        new("/auth/callback"),
        new("/favicon.ico")
    ];

    public static bool IsApiPath(PathString path) =>
        path.StartsWithSegments(new PathString("/api"), StringComparison.OrdinalIgnoreCase);

    public static bool IsBackendOwnedPath(PathString path) =>
        BackendOwnedPrefixes.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));

    public static bool IsAppPath(PathString path) =>
        path.Equals(new PathString(AppRequestPath), StringComparison.OrdinalIgnoreCase) ||
        path.StartsWithSegments(new PathString(AppRequestPath), StringComparison.OrdinalIgnoreCase);

    public static bool IsAngularIndexPath(PathString path) =>
        path.Equals(new PathString($"{AppRequestPath}/index.html"), StringComparison.OrdinalIgnoreCase);

    public static bool HasAngularBuild(string webRootPath) =>
        File.Exists(Path.Combine(webRootPath, "index.html")) &&
        File.Exists(Path.Combine(webRootPath, BuildMarkerFileName));

    public static void MapEndpointFallback(IApplicationBuilder app, string webRootPath)
    {
        // Handle only otherwise-empty 404 responses instead of registering a
        // catch-all route. A catch-all route participates in HTTP method
        // matching and contaminates the Allow header of real API 405 responses.
        app.UseStatusCodePages(async statusCodeContext =>
        {
            var context = statusCodeContext.HttpContext;
            if (context.Response.StatusCode == StatusCodes.Status404NotFound)
            {
                await HandleAsync(context, webRootPath);
            }
        });
    }

    public static bool CanServeAngularFallback(HttpRequest request, string webRootPath) =>
        (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)) &&
        HasAngularBuild(webRootPath) &&
        IsAppPath(request.Path) &&
        !IsBackendOwnedPath(request.Path) &&
        !LooksLikeStaticAssetPath(request.Path);

    public static async Task HandleAsync(HttpContext context, string webRootPath)
    {
        if (IsApiPath(context.Request.Path))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(
                new ErrorResponse("NotFound", "Endpoint not found.", context.TraceIdentifier));
            return;
        }

        if (!CanServeAngularFallback(context.Request, webRootPath))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        ApplySpaHtmlHeaders(context.Response);
        if (HttpMethods.IsHead(context.Request.Method))
        {
            return;
        }

        await context.Response.SendFileAsync(Path.Combine(webRootPath, "index.html"));
    }

    public static void ApplyStaticFileHeaders(HttpResponse response, PathString requestPath)
    {
        var path = requestPath.Value ?? string.Empty;
        if (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            ApplySpaHtmlHeaders(response);
            return;
        }

        if (path.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
        {
            response.ContentType = "text/javascript; charset=utf-8";
            ApplyImmutableCacheHeaders(response);
            return;
        }

        if (path.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
        {
            response.ContentType = "text/css; charset=utf-8";
            ApplyImmutableCacheHeaders(response);
            return;
        }

        if (IsImmutableAssetPath(path))
        {
            ApplyImmutableCacheHeaders(response);
        }
    }

    private static void ApplySpaHtmlHeaders(HttpResponse response)
    {
        response.ContentType = "text/html; charset=utf-8";
        response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        response.Headers.Pragma = "no-cache";
        response.Headers.Expires = "0";
    }

    private static void ApplyImmutableCacheHeaders(HttpResponse response)
    {
        response.Headers.CacheControl = "public, max-age=31536000, immutable";
    }

    private static bool LooksLikeStaticAssetPath(PathString path)
    {
        var value = path.Value;
        if (string.IsNullOrWhiteSpace(value) || value == "/")
        {
            return false;
        }

        var lastSegment = value[(value.LastIndexOf('/') + 1)..];
        return Path.HasExtension(lastSegment);
    }

    private static bool IsImmutableAssetPath(string path) =>
        path.EndsWith(".woff2", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".avif", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase);
}
