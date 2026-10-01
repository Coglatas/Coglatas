using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Notifications;
using Microsoft.Extensions.Options;

namespace Coglatas.Web.Notifications;

public sealed class TaskDeadlineDigestWorkerOptions
{
    public int PollSeconds { get; set; } = 60;
    public int TenantPageSize { get; set; } = 25;
    public int SchedulePageSize { get; set; } = 100;
    public int ClaimBatchSize { get; set; } = 20;
    public int CandidatePageSize { get; set; } = 100;
    public int ClaimTimeoutSeconds { get; set; } = 120;
    public int RetrySeconds { get; set; } = 60;

    public TaskDeadlineDigestRunSettings ToRunSettings() => new(
        SchedulePageSize,
        ClaimBatchSize,
        CandidatePageSize,
        TimeSpan.FromSeconds(Math.Max(1, ClaimTimeoutSeconds)),
        TimeSpan.FromSeconds(Math.Max(1, RetrySeconds)));
}

public sealed class TaskDeadlineDigestWorker(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    IOptions<TaskDeadlineDigestWorkerOptions> options,
    ILogger<TaskDeadlineDigestWorker> logger) : BackgroundService
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
            catch (Exception exception)
            {
                logger.LogError(
                    "Task deadline digest worker cycle failed with {ErrorCode}.",
                    TaskDeadlineDigestErrorCodes.FromException(exception));
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
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<Guid> tenantIds;
            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                scope.ServiceProvider.GetRequiredService<ICurrentTenantAccessor>().SetPlatformScope();
                tenantIds = await scope.ServiceProvider
                    .GetRequiredService<ITaskDeadlineDigestRepository>()
                    .ListActiveTenantIdsAsync(page, tenantPageSize, cancellationToken);
            }

            foreach (var tenantId in tenantIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ProcessTenantAsync(tenantId, cancellationToken);
            }

            if (tenantIds.Count < tenantPageSize)
                break;
        }
    }

    private async Task ProcessTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        IReadOnlyList<TaskDeadlineDigestClaim> claims;
        var now = clock.UtcNow;
        var runSettings = options.Value.ToRunSettings();
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ICurrentTenantAccessor>().SetTenant(tenantId, "task-deadline-digest");
            claims = await scope.ServiceProvider
                .GetRequiredService<ITaskDeadlineDigestScheduler>()
                .ScheduleAndClaimAsync(_claimOwner, now, runSettings, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Task deadline digest tenant cycle failed with {ErrorCode}.",
                TaskDeadlineDigestErrorCodes.FromException(exception));
            return;
        }

        // ClaimDueAsync already bounds this collection to the configured claim
        // batch. Start every leased claim immediately so a slow recipient does
        // not leave the rest of the batch idle until their leases expire.
        await Task.WhenAll(claims.Select(claim => ProcessClaimAsync(
            claim,
            runSettings.SafeCandidatePageSize,
            runSettings.SafeRetryDelay,
            cancellationToken)));
    }

    private async Task ProcessClaimAsync(
        TaskDeadlineDigestClaim claim,
        int candidatePageSize,
        TimeSpan retryDelay,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var generationScope = scopeFactory.CreateAsyncScope();
            generationScope.ServiceProvider.GetRequiredService<ICurrentTenantAccessor>()
                .SetTenant(claim.TenantId, "task-deadline-digest");
            await generationScope.ServiceProvider
                .GetRequiredService<ITaskDeadlineDigestGenerator>()
                .GenerateAsync(claim, clock.UtcNow, candidatePageSize, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var errorCode = TaskDeadlineDigestErrorCodes.FromException(exception);
            logger.LogWarning(
                "Task deadline digest generation failed with {ErrorCode}.",
                errorCode);
            await RecordFailureAsync(claim, errorCode, retryDelay, cancellationToken);
        }
    }

    private async Task RecordFailureAsync(
        TaskDeadlineDigestClaim claim,
        string errorCode,
        TimeSpan retryDelay,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ICurrentTenantAccessor>()
                .SetTenant(claim.TenantId, "task-deadline-digest-failure");
            await scope.ServiceProvider.GetRequiredService<ITaskDeadlineDigestFailureHandler>()
                .FailAsync(claim, errorCode, clock.UtcNow, retryDelay, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Never attach the original failure or recipient/resource IDs to
            // this operational log. Claim expiry provides the fenced recovery.
            logger.LogError("Task deadline digest failure recording did not complete.");
        }
    }
}
