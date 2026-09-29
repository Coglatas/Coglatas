using System.Net;
using System.Security.Claims;
using Coglatas.Web.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Coglatas.Tests.Auth;

public sealed class SecurityHeadersMiddlewareTests
{
    [Fact]
    [Trait("Scope", "SEC-13")]
    public async Task ContentSecurityPolicyAllowsOnlyTheCurrentHostForWebSockets()
    {
        using var response = await InvokeAsync(host: "portal.example.test:8443");

        var policy = GetRequiredHeader(response, "Content-Security-Policy");
        Assert.NotEmpty(policy);
        Assert.Contains("connect-src 'self' ws://portal.example.test:8443 wss://portal.example.test:8443;", policy);
        Assert.DoesNotContain("connect-src 'self' https:", policy);
        Assert.DoesNotContain("connect-src 'self' ws: wss:", policy);
        Assert.DoesNotContain("'unsafe-eval'", policy);
        Assert.DoesNotContain("script-src 'self' 'unsafe-inline'", policy);
        Assert.DoesNotContain('*', policy);
    }

    [Fact]
    [Trait("Scope", "SEC-13")]
    public async Task ResponseContainsCanonicalBrowserSecurityHeaders()
    {
        using var response = await InvokeAsync();

        Assert.Equal("nosniff", GetRequiredHeader(response, "X-Content-Type-Options"));
        Assert.Equal("DENY", GetRequiredHeader(response, "X-Frame-Options"));
        Assert.Equal("strict-origin-when-cross-origin", GetRequiredHeader(response, "Referrer-Policy"));
        Assert.Equal("camera=(), microphone=(), geolocation=()", GetRequiredHeader(response, "Permissions-Policy"));
        Assert.Contains("frame-ancestors 'none'", GetRequiredHeader(response, "Content-Security-Policy"));
    }

    [Fact]
    [Trait("Scope", "SEC-13")]
    public async Task AuthenticatedResponseIsExplicitlyNoStore()
    {
        using var response = await InvokeAsync(configureContext: context =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())],
                "test"));
        });

        Assert.Equal("no-store, no-cache, max-age=0", GetRequiredHeader(response, "Cache-Control"));
        Assert.Equal("no-cache", GetRequiredHeader(response, "Pragma"));
        Assert.Equal("0", GetRequiredHeader(response, "Expires"));
    }

    [Fact]
    [Trait("Scope", "SEC-13")]
    public async Task CookieSettingResponseIsExplicitlyNoStore()
    {
        using var response = await InvokeAsync(configureResponse: httpResponse =>
        {
            httpResponse.Headers["Set-Cookie"] = ".Coglatas.Auth=value; path=/; secure; httponly";
        });

        Assert.Equal("no-store, no-cache, max-age=0", GetRequiredHeader(response, "Cache-Control"));
    }

    [Fact]
    [Trait("Scope", "SEC-13")]
    public async Task ExternalRedirectIsRejectedBeforeHeadersAreSent()
    {
        using var response = await InvokeAsync(configureResponse: httpResponse =>
        {
            httpResponse.StatusCode = StatusCodes.Status302Found;
            httpResponse.Headers.Location = "https://attacker.example/collect";
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(response.Headers.Contains("Location"));
        Assert.Equal("no-store, no-cache, max-age=0", GetRequiredHeader(response, "Cache-Control"));
    }

    [Fact]
    [Trait("Scope", "SEC-13")]
    public async Task SameHostHttpsUpgradeRedirectRemainsAllowed()
    {
        using var response = await InvokeAsync(
            host: "portal.example.test:80",
            configureResponse: httpResponse =>
            {
                httpResponse.StatusCode = StatusCodes.Status307TemporaryRedirect;
                httpResponse.Headers.Location = "https://portal.example.test/app/";
            });

        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.Equal("https://portal.example.test/app/", GetRequiredHeader(response, "Location"));
    }

    private static async Task<HttpResponseMessage> InvokeAsync(
        string host = "portal.example.test",
        Action<HttpContext>? configureContext = null,
        Action<HttpResponse>? configureResponse = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            configureContext?.Invoke(context);
            await next(context);
        });
        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.Run(context =>
        {
            configureResponse?.Invoke(context.Response);
            return Task.CompletedTask;
        });

        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses.Single()
                ?? throw new InvalidOperationException("Test server address was not available.");
            using var handler = new HttpClientHandler();
            handler.AllowAutoRedirect = false;
            // ReSharper disable once ShortLivedHttpClient
            using var client = new HttpClient(handler);
            client.BaseAddress = new Uri(address);
            using var request = new HttpRequestMessage(HttpMethod.Get, "/");
            request.Headers.Host = host;

            return await client.SendAsync(request);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    private static string GetRequiredHeader(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values))
        {
            return values.Single();
        }

        Assert.True(response.Content.Headers.TryGetValues(name, out values), $"Missing required header: {name}");
        return values.Single();
    }

    [Fact]
    [Trait("Scope", "FCI-07")]
    public async Task TaskDetailReadIsExplicitlyNonCacheable()
    {
        var context = new DefaultHttpContext
        {
            Request = { Method = HttpMethods.Get, Path = $"/api/tasks/{Guid.NewGuid():D}" }
        };
        var middleware = new SecurityHeadersMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        Assert.Equal("no-store, max-age=0", context.Response.Headers.CacheControl.ToString());
        Assert.Equal("no-cache", context.Response.Headers.Pragma.ToString());
        Assert.Equal("0", context.Response.Headers.Expires.ToString());
    }

    [Theory]
    [InlineData("/api/tasks")]
    [InlineData("/api/tasks/not-a-guid")]
    [InlineData("/api/tasks/00000000-0000-0000-0000-000000000001/activity")]
    [Trait("Scope", "FCI-07")]
    public async Task TaskNoStorePolicyDoesNotLeakToOtherTaskRoutes(string path)
    {
        var context = new DefaultHttpContext
        {
            Request = { Method = HttpMethods.Get, Path = path }
        };
        var middleware = new SecurityHeadersMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        Assert.False(context.Response.Headers.ContainsKey("Cache-Control"));
        Assert.False(context.Response.Headers.ContainsKey("Pragma"));
        Assert.False(context.Response.Headers.ContainsKey("Expires"));
    }
}
