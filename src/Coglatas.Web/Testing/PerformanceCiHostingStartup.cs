using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Persistence;

[assembly: HostingStartup(typeof(Coglatas.Web.Testing.PerformanceCiHostingStartup))]

namespace Coglatas.Web.Testing;

/// <summary>
/// Registers PERF-02 fixture initialization without adding a normal Program.cs
/// startup path. The hosting startup is a no-op unless the explicit Test-only
/// performance boundary is enabled.
/// </summary>
public sealed class PerformanceCiHostingStartup : IHostingStartup
{
    public void Configure(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureServices((context, services) =>
        {
            var requested =
                context.Configuration.GetValue<bool>("PerformanceCiFixture:Enabled") ||
                context.Configuration.GetValue<bool>("COGLATAS_PERFORMANCE_CI_FIXTURE_ENABLED");
            if (!PerformanceCiTestBoundary.IsEnabled(context.HostingEnvironment.EnvironmentName, requested))
            {
                return;
            }

            services.AddHostedService<PerformanceCiFixtureHostedService>();
        });
    }
}

/// <summary>
/// Builds and verifies the deterministic fixture before Kestrel accepts benchmark
/// traffic. Any migration, target, manifest, or cardinality mismatch aborts startup.
/// </summary>
internal sealed class PerformanceCiFixtureHostedService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    IHostEnvironment environment) : IHostedLifecycleService
{
    private bool _seeded;

    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        if (_seeded)
        {
            return;
        }

        var requested =
            configuration.GetValue<bool>("PerformanceCiFixture:Enabled") ||
            configuration.GetValue<bool>("COGLATAS_PERFORMANCE_CI_FIXTURE_ENABLED");
        if (!PerformanceCiTestBoundary.IsEnabled(environment.EnvironmentName, requested))
        {
            throw new InvalidOperationException(
                "PERF-02 fixture hosted service was activated outside its Test-only boundary.");
        }

        var profile =
            configuration["PerformanceCiFixture:Profile"] ??
            configuration["COGLATAS_PERFORMANCE_PROFILE"];
        var password =
            configuration["PerformanceCiFixture:Password"] ??
            configuration["COGLATAS_PERFORMANCE_PASSWORD"];
        var manifestPath =
            configuration["PerformanceCiFixture:DatasetsPath"] ??
            configuration["COGLATAS_PERFORMANCE_DATASETS_PATH"];
        var evidencePath =
            configuration["PerformanceCiFixture:EvidencePath"] ??
            configuration["COGLATAS_PERFORMANCE_FIXTURE_EVIDENCE_PATH"];

        if (profile is not ("small" or "medium" or "large"))
        {
            throw new InvalidOperationException(
                "PERF-02 requires COGLATAS_PERFORMANCE_PROFILE=small|medium|large.");
        }
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("PERF-02 requires COGLATAS_PERFORMANCE_PASSWORD.");
        }
        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
        {
            throw new InvalidOperationException(
                "PERF-02 dataset manifest is missing or unreadable.");
        }
        if (string.IsNullOrWhiteSpace(evidencePath))
        {
            throw new InvalidOperationException("PERF-02 fixture evidence path is required.");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyOptions>();
        if (tenancy.AppMode != AppMode.SaaS ||
            tenancy.TenantResolutionStrategy != TenantResolutionStrategy.HeaderForDevelopmentOnly ||
            !tenancy.AllowDevelopmentHeaderTenantResolution)
        {
            throw new InvalidOperationException(
                "PERF-02 requires the isolated Test SaaS/header tenant resolver profile.");
        }

        var currentTenant = scope.ServiceProvider.GetRequiredService<ICurrentTenantAccessor>();
        currentTenant.SetPlatformScope();

        await PerformanceCiFixtureSeed.SeedAsync(
            scope.ServiceProvider.GetRequiredService<AppDbContext>(),
            scope.ServiceProvider.GetRequiredService<IPasswordHasher>(),
            manifestPath,
            profile,
            password,
            evidencePath,
            cancellationToken);

        _seeded = true;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
