using Coglatas.Web.Testing;
using Coglatas.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Coglatas.Tests.Web;

public sealed class PerformanceCiTestBoundaryTests
{
    [Theory]
    [InlineData("Test", true)]
    [InlineData("test", true)]
    [InlineData("Development", false)]
    [InlineData("Staging", false)]
    [InlineData("Production", false)]
    public void PerformanceFixtureIsRestrictedToAnExplicitTestEnvironmentOptIn(
        string environmentName,
        bool expected)
    {
        Assert.Equal(expected, PerformanceCiTestBoundary.IsEnabled(environmentName, requested: true));
    }

    [Fact]
    public void PerformanceFixtureRemainsDisabledWithoutExplicitOptIn()
    {
        Assert.False(PerformanceCiTestBoundary.IsEnabled("Test", requested: false));
    }
    [Theory]
    [InlineData("Test", true, true, true)]
    [InlineData("Test", false, true, false)]
    [InlineData("Test", true, false, false)]
    [InlineData("Production", true, true, false)]
    [InlineData("Development", true, true, false)]
    public void DbInstrumentationRequiresBothExplicitOptInsAndTheTestEnvironment(
        string environmentName, bool fixtureRequested, bool captureRequested, bool expected)
    {
        using var host = new HostBuilder()
            .UseEnvironment(environmentName)
            .ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["COGLATAS_PERFORMANCE_CI_FIXTURE_ENABLED"] = fixtureRequested.ToString(),
                ["COGLATAS_PERFORMANCE_DB_CAPTURE_ENABLED"] = captureRequested.ToString()
            }))
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment(environmentName).UseKestrel().Configure(_ => { });
                new PerformanceCiHostingStartup().Configure(builder);
            })
            .Build();
        Assert.Equal(expected, host.Services.GetService<PerformanceDbCapture>() is not null);
    }
    [Fact]
    public async Task CaptureEvidenceCompletesAfterTheResponseClientHasDisconnected()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"perf05-capture-{Guid.NewGuid():N}");
        var id = Guid.NewGuid().ToString("N");
        using var host = new HostBuilder()
            .UseEnvironment("Test")
            .ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["COGLATAS_PERFORMANCE_CI_FIXTURE_ENABLED"] = "true",
                ["COGLATAS_PERFORMANCE_DB_CAPTURE_ENABLED"] = "true",
                ["COGLATAS_PERFORMANCE_DB_EVIDENCE_PATH"] = directory
            }))
            .ConfigureWebHost(builder =>
            {
                builder.UseKestrel().Configure(_ => { });
                new PerformanceCiHostingStartup().Configure(builder);
            })
            .Build();
        try
        {
            var filter = host.Services.GetServices<IStartupFilter>()
                .Single(candidate => candidate.GetType().Name == "PerformanceDbStartupFilter");
            var app = new ApplicationBuilder(host.Services);
            filter.Configure(pipeline => pipeline.Run(context =>
            {
                using var source = new ActivitySource("Npgsql");
                using (var activity = source.StartActivity())
                {
                    Assert.NotNull(activity);
                    activity.SetTag("db.query.text", "SELECT 'protected-body' FROM task_items LIMIT 5");
                }
                context.Response.StatusCode = 200;
                context.RequestAborted = new CancellationToken(canceled: true);
                return Task.CompletedTask;
            }))(app);
            using var scope = host.Services.CreateScope();
            var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            context.Request.Headers["X-Performance-Capture"] = id;
            await app.Build()(context);
            var evidence = await File.ReadAllTextAsync(Path.Combine(directory, id + ".json"));
            Assert.DoesNotContain("protected-body", evidence);
            using var document = JsonDocument.Parse(evidence);
            Assert.Equal(1, document.RootElement.GetProperty("commandCount").GetInt32());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
