using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Coglatas.Tests.OpenApi;

public sealed class RequestLineLimitHttpTests
{
    [Theory]
    [InlineData("/api/me/tasks")]
    [InlineData("/api/me/tasks/counts")]
    public async Task Oversized_query_returns_empty_414_before_application_pipeline(string path)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        var pipelineEntered = false;
        app.Run(_ =>
        {
            pipelineEntered = true;
            return Task.CompletedTask;
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses.Single()
            ?? throw new InvalidOperationException("Request-line probe server address was not available.");
        using var client = new HttpClient { BaseAddress = new Uri(address) };

        using var response = await client.GetAsync(path + "?StageCategory=" + new string('x', 9_000));

        Assert.Equal(HttpStatusCode.RequestUriTooLong, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Null(response.Content.Headers.ContentType);
        Assert.False(pipelineEntered);
    }
}
