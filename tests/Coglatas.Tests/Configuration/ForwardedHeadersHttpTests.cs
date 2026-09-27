using System.Net;
using System.Net.Http.Json;
using Coglatas.Web.Configuration;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Coglatas.Tests.Configuration;

public sealed class ForwardedHeadersHttpTests
{
    [Fact]
    public async Task AsymmetricForwardedHeadersRemainRejectedByDefault()
    {
        await using var app = await ProxyProbeApp.CreateAsync(requireHeaderSymmetry: true);

        using var response = await app.Client.SendAsync(CreateAsymmetricRequest("/scheme"));
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<ProbeResponse>();

        Assert.Equal("http", payload?.Scheme);
        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task AsymmetricTrustedTunnelHeadersPreserveHttpsSecureCsrfCookieAndHstsWhenSymmetryIsDisabled()
    {
        await using var app = await ProxyProbeApp.CreateAsync(requireHeaderSymmetry: false);

        using var response = await app.Client.SendAsync(CreateAsymmetricRequest("/csrf"));
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<ProbeResponse>();

        Assert.Equal("https", payload?.Scheme);
        Assert.Equal("portal.example.com", payload?.Host);
        Assert.False(string.IsNullOrWhiteSpace(payload?.Token));
        Assert.True(
            response.Headers.TryGetValues("Set-Cookie", out var cookies) &&
            cookies.Any(cookie =>
                cookie.Contains(".Coglatas.Csrf.ProxyProbe=", StringComparison.Ordinal) &&
                cookie.Contains("secure", StringComparison.OrdinalIgnoreCase)),
            response.Headers.ToString());
        Assert.True(
            response.Headers.TryGetValues("Strict-Transport-Security", out var hstsValues) &&
            hstsValues.Any(value => value.Contains("max-age=", StringComparison.OrdinalIgnoreCase)),
            response.Headers.ToString());
    }

    private static HttpRequestMessage CreateAsymmetricRequest(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation(
            "X-Forwarded-For",
            "198.51.100.42, 203.0.113.20");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Host", "portal.example.com");
        return request;
    }

    private sealed record ProbeResponse(string Scheme, string Host, string? Token);

    private sealed class ProxyProbeApp : IAsyncDisposable
    {
        private ProxyProbeApp(WebApplication app, HttpClient client)
        {
            App = app;
            Client = client;
        }

        private WebApplication App { get; }
        public HttpClient Client { get; }

        public static async Task<ProxyProbeApp> CreateAsync(bool requireHeaderSymmetry)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Production
            });
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ForwardedHeadersConfiguration.TrustForwardedHeadersKey] = "true",
                [ForwardedHeadersConfiguration.RequireHeaderSymmetryKey] = requireHeaderSymmetry.ToString(),
                [$"{ForwardedHeadersConfiguration.TrustedProxiesKey}:0"] = "127.0.0.1"
            });

            builder.Services.Configure<ForwardedHeadersOptions>(options =>
                ForwardedHeadersConfiguration.Configure(options, builder.Configuration));
            builder.Services.AddAntiforgery(options =>
            {
                options.Cookie.Name = ".Coglatas.Csrf.ProxyProbe";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            });

            var app = builder.Build();
            app.UseForwardedHeaders();
            app.UseHsts();
            app.MapGet("/scheme", (HttpContext context) =>
                Results.Json(new ProbeResponse(
                    context.Request.Scheme,
                    context.Request.Host.Value ?? string.Empty,
                    null)));
            app.MapGet("/csrf", (HttpContext context, IAntiforgery antiforgery) =>
            {
                var tokens = antiforgery.GetAndStoreTokens(context);
                return Results.Json(new ProbeResponse(
                    context.Request.Scheme,
                    context.Request.Host.Value ?? string.Empty,
                    tokens.RequestToken));
            });

            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses.Single()
                ?? throw new InvalidOperationException("Proxy probe server address was not available.");

            return new ProxyProbeApp(
                app,
                new HttpClient(new HttpClientHandler
                {
                    UseCookies = false
                })
                {
                    BaseAddress = new Uri(address)
                });
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.DisposeAsync();
        }
    }
}
