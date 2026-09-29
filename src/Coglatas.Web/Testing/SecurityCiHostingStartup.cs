using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Persistence;

[assembly: HostingStartup(typeof(Coglatas.Web.Testing.SecurityCiHostingStartup))]

namespace Coglatas.Web.Testing;

/// <summary>
/// Registers the SEC-02 fixture initializer without adding another normal
/// application startup path. The entry assembly is scanned for HostingStartup
/// attributes by ASP.NET Core; this implementation remains a no-op unless the
/// explicit Test-only boundary is enabled.
/// </summary>
public sealed class SecurityCiHostingStartup : IHostingStartup
{
    public void Configure(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureServices((context, services) =>
        {
            var requested =
                context.Configuration.GetValue<bool>("SecurityCiFixture:Enabled") ||
                context.Configuration.GetValue<bool>("COGLATAS_SECURITY_CI_FIXTURE_ENABLED");
            if (!SecurityCiTestBoundary.IsEnabled(context.HostingEnvironment.EnvironmentName, requested))
            {
                return;
            }

            services.AddHostedService<SecurityCiFixtureHostedService>();
        });
    }
}

/// <summary>
/// Seeds before ordinary hosted-service StartAsync execution. Using the lifecycle
/// Starting phase prevents the HTTP server from accepting scanner traffic before
/// the synthetic authorization graph and file canaries exist.
/// </summary>
internal sealed class SecurityCiFixtureHostedService(
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
            configuration.GetValue<bool>("SecurityCiFixture:Enabled") ||
            configuration.GetValue<bool>("COGLATAS_SECURITY_CI_FIXTURE_ENABLED");
        if (!SecurityCiTestBoundary.IsEnabled(environment.EnvironmentName, requested))
        {
            throw new InvalidOperationException(
                "SEC-02 fixture hosted service was activated outside its Test-only boundary.");
        }

        var password =
            configuration["SecurityCiFixture:Password"] ??
            configuration["COGLATAS_SECURITY_CI_PASSWORD"];
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                "SEC-02 fixture is enabled but SecurityCiFixture:Password/COGLATAS_SECURITY_CI_PASSWORD is missing.");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyOptions>();
        if (tenancy.AppMode == AppMode.OnPremSingleTenant ||
            tenancy.TenantResolutionStrategy != TenantResolutionStrategy.HeaderForDevelopmentOnly ||
            !tenancy.AllowDevelopmentHeaderTenantResolution)
        {
            throw new InvalidOperationException(
                "SEC-02 requires multi-tenant mode with explicit Test-only header tenant resolution.");
        }

        var currentTenant = scope.ServiceProvider.GetRequiredService<ICurrentTenantAccessor>();
        currentTenant.SetPlatformScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await SecurityCiFixtureSeed.SeedAsync(
            dbContext,
            scope.ServiceProvider.GetRequiredService<IPasswordHasher>(),
            scope.ServiceProvider.GetRequiredService<IFileStorageService>(),
            password,
            cancellationToken);
        await SecurityCiAuthorizationMatrixSeed.SeedAsync(dbContext, cancellationToken);

        _seeded = true;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
