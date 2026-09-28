using System.Net;
using System.Net.Http.Json;
using Coglatas.Web.Configuration;
using Coglatas.Web.Middleware;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Coglatas.Tests.Auth;

public sealed class Sec13ProductionHttpTests
{
    [Fact]
    [Trait("Scope", "SEC-13")]
    public async Task ProductionLikeHttpsResponseContainsHstsAndCanonicalSecurityHeaders()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production
        });
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddHsts(HttpSecurityPolicy.ConfigureHsts);

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Request.Scheme = Uri.UriSchemeHttps;
            context.Request.Host = new HostString("portal.example.test");
            await next(context);
        });
        app.UseHsts();
        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.MapGet("/secure", () => Results.Ok(new { status = "OK" }));

        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses.Single()
                ?? throw new InvalidOperationException("Test server address was not available.");
            // ReSharper disable once ShortLivedHttpClient
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            using var response = await client.GetAsync("/secure");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var hsts = GetRequiredHeader(response, "Strict-Transport-Security");
            Assert.Contains(
                $"max-age={(long)HttpSecurityPolicy.HstsMaxAge.TotalSeconds}",
                hsts,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal("nosniff", GetRequiredHeader(response, "X-Content-Type-Options"));
            Assert.Equal("DENY", GetRequiredHeader(response, "X-Frame-Options"));
            Assert.Equal(
                HttpSecurityPolicy.ReferrerPolicy,
                GetRequiredHeader(response, "Referrer-Policy"));
            Assert.Equal(
                HttpSecurityPolicy.PermissionsPolicy,
                GetRequiredHeader(response, "Permissions-Policy"));

            var csp = GetRequiredHeader(response, "Content-Security-Policy");
            Assert.Contains("default-src 'self'", csp, StringComparison.Ordinal);
            Assert.Contains("script-src 'self'", csp, StringComparison.Ordinal);
            Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
            Assert.DoesNotContain("'unsafe-eval'", csp, StringComparison.Ordinal);
            Assert.DoesNotContain("script-src 'self' 'unsafe-inline'", csp, StringComparison.Ordinal);
            Assert.DoesNotContain('*', csp);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    [Trait("Scope", "SEC-13")]
    public async Task InvalidAndCrossCookieContextCsrfTokensAreRejected()
    {
        var security = new SecurityOptions
        {
            EnableCsrfProtection = true,
            CookieSecurePolicy = CookieSecurePolicy.SameAsRequest
        };
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.Configure<SecurityOptions>(options =>
        {
            options.EnableCsrfProtection = true;
            options.CookieSecurePolicy = CookieSecurePolicy.SameAsRequest;
        });
        builder.Services.AddAntiforgery(options => HttpSecurityPolicy.ConfigureAntiforgery(options, security));

        var mutationCalls = 0;
        var app = builder.Build();
        app.UseMiddleware<CsrfProtectionMiddleware>();
        app.MapGet("/csrf", (HttpContext context, IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(context);
            return Results.Ok(new CsrfPayload(tokens.RequestToken));
        });
        app.MapPost("/api/mutate", () =>
        {
            mutationCalls++;
            return Results.NoContent();
        });

        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses.Single()
                ?? throw new InvalidOperationException("Test server address was not available.");
            var baseAddress = new Uri(address);
            using var ownerHandler = new HttpClientHandler
            {
                UseCookies = true,
                CookieContainer = new CookieContainer()
            };
            // ReSharper disable once ShortLivedHttpClient
            using var ownerClient = new HttpClient(ownerHandler) { BaseAddress = baseAddress };
            using var csrfResponse = await ownerClient.GetAsync("/csrf");
            csrfResponse.EnsureSuccessStatusCode();
            var csrf = await csrfResponse.Content.ReadFromJsonAsync<CsrfPayload>();
            var csrfToken = csrf?.Token;
            Assert.False(string.IsNullOrWhiteSpace(csrfToken));

            using var invalidRequest = new HttpRequestMessage(HttpMethod.Post, "/api/mutate")
            {
                Content = JsonContent.Create(new { })
            };
            invalidRequest.Headers.TryAddWithoutValidation(
                SecurityOptions.CsrfHeaderName,
                "not-a-valid-antiforgery-token");
            using var invalidResponse = await ownerClient.SendAsync(invalidRequest);
            Assert.Equal(HttpStatusCode.Forbidden, invalidResponse.StatusCode);

            using var otherHandler = new HttpClientHandler
            {
                UseCookies = true,
                CookieContainer = new CookieContainer()
            };
            // ReSharper disable once ShortLivedHttpClient
            using var otherClient = new HttpClient(otherHandler) { BaseAddress = baseAddress };
            using var mismatchRequest = new HttpRequestMessage(HttpMethod.Post, "/api/mutate")
            {
                Content = JsonContent.Create(new { })
            };
            mismatchRequest.Headers.TryAddWithoutValidation(SecurityOptions.CsrfHeaderName, csrfToken!);
            using var mismatchResponse = await otherClient.SendAsync(mismatchRequest);

            Assert.Equal(HttpStatusCode.Forbidden, mismatchResponse.StatusCode);
            Assert.Equal(0, mutationCalls);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    private static string GetRequiredHeader(HttpResponseMessage response, string name)
    {
        Assert.True(response.Headers.TryGetValues(name, out var values), $"Missing required header: {name}");
        return values.Single();
    }

    private sealed record CsrfPayload(string? Token);
}
