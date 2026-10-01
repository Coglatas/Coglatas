using Coglatas.Application.Announcements;
using Coglatas.Application.Common.Interfaces;
using Microsoft.Extensions.Options;

namespace Coglatas.Web.Notifications;

public sealed class AnnouncementPublisherWorkerOptions
{
    public int PollSeconds { get; set; } = 30;
    public int TenantPageSize { get; set; } = 25;
    public int ClaimBatchSize { get; set; } = 10;
    public int ClaimTimeoutSeconds { get; set; } = 120;
    public int RetrySeconds { get; set; } = 300;
}

/// <summary>
/// Small in-process due-time publisher. PostgreSQL remains the coordination
/// boundary: every host has its own process loop, but only a fenced draft
/// lease may advance the one durable Scheduled -> Published transition.
/// </summary>
public sealed class AnnouncementPublisherWorker(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    IOptions<AnnouncementPublisherWorkerOptions> options,
    ILogger<AnnouncementPublisherWorker> logger) : BackgroundService
{
    private readonly string _claimOwner = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Never put tenant, draft, scope, or exception details in the
                // operational log. The durable Scheduled row remains the
                // authoritative retry boundary.
                logger.LogError("Announcement publication worker cycle failed.");
            }

            await Task.Delay(
                TimeSpan.FromSeconds(Math.Max(1, options.Value.PollSeconds)),
                stoppingToken);
        }
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var tenantPageSize = Math.Clamp(options.Value.TenantPageSize, 1, 100);
        for (var page = 0; ; page++)
        {
            IReadOnlyList<Guid> tenantIds;
            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                scope.ServiceProvider.GetRequiredService<ICurrentTenantAccessor>().SetPlatformScope();
                tenantIds = await scope.ServiceProvider
                    .GetRequiredService<IAnnouncementPublicationProcessor>()
                    .ListActiveTenantIdsAsync(page, tenantPageSize, cancellationToken);
            }

            foreach (var tenantId in tenantIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ProcessTenantAsync(tenantId, cancellationToken);
            }

            if (tenantIds.Count < tenantPageSize)
            {
                break;
            }
        }
    }

    private async Task ProcessTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        IReadOnlyList<AnnouncementPublicationClaim> claims;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ICurrentTenantAccessor>().SetTenant(tenantId, "announcement-publisher");
            claims = await scope.ServiceProvider
                .GetRequiredService<IAnnouncementPublicationProcessor>()
                .ClaimDueAsync(
                    _claimOwner,
                    clock.UtcNow,
                    Math.Clamp(options.Value.ClaimBatchSize, 1, 50),
                    TimeSpan.FromSeconds(Math.Max(1, options.Value.ClaimTimeoutSeconds)),
                    cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            logger.LogWarning("Announcement publication tenant claim failed.");
            return;
        }

        foreach (var claim in claims)
        {
            await ProcessClaimAsync(claim, cancellationToken);
        }
    }

    private async Task ProcessClaimAsync(AnnouncementPublicationClaim claim, CancellationToken cancellationToken)
    {
        var retryDelay = TimeSpan.FromSeconds(Math.Max(1, options.Value.RetrySeconds));
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ICurrentTenantAccessor>().SetTenant(claim.TenantId, "announcement-publisher");
            await scope.ServiceProvider
                .GetRequiredService<IAnnouncementPublicationProcessor>()
                .ProcessAsync(claim, clock.UtcNow, retryDelay, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            logger.LogWarning("Announcement publication processing failed; a safe retry was requested.");
            await RecordFailureAsync(claim, retryDelay, cancellationToken);
        }
    }

    private async Task RecordFailureAsync(
        AnnouncementPublicationClaim claim,
        TimeSpan retryDelay,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ICurrentTenantAccessor>().SetTenant(claim.TenantId, "announcement-publisher-failure");
            await scope.ServiceProvider
                .GetRequiredService<IAnnouncementPublicationProcessor>()
                .RecordFailureAsync(claim, clock.UtcNow, retryDelay, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            logger.LogError("Announcement publication failure could not be recorded.");
        }
    }
}
