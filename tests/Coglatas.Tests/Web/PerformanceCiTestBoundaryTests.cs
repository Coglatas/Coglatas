using Coglatas.Web.Testing;
using Coglatas.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
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
                builder.Configure(_ => { });
                new PerformanceCiHostingStartup().Configure(builder);
            })
            .Build();
        Assert.Equal(expected, host.Services.GetService<PerformanceDbCapture>() is not null);
    }
}
