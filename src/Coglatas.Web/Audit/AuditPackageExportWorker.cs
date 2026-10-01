using Coglatas.Application.Audit;
using Coglatas.Application.Common.Interfaces;

namespace Coglatas.Web.Audit;

public sealed class AuditPackageExportWorkerOptions
{
    public int PollSeconds { get; set; } = 5;
    public int TenantBatchSize { get; set; } = 50;
    public int JobBatchSize { get; set; } = 10;
    public int StaleProcessingMinutes { get; set; } = 10;
}

public sealed class AuditPackageExportWorker(
    IServiceScopeFactory scopeFactory,
    Microsoft.Extensions.Options.IOptions<AuditPackageExportWorkerOptions> options,
    ILogger<AuditPackageExportWorker> logger) : BackgroundService
{
    private readonly AuditPackageExportWorkerOptions _settings = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(Math.Clamp(_settings.PollSeconds, 1, 60));
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
                logger.LogError(exception, "Audit package export worker cycle failed.");
            }

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var staleBefore = DateTimeOffset.UtcNow.AddMinutes(-Math.Clamp(_settings.StaleProcessingMinutes, 1, 120));
        IReadOnlyList<Guid> tenantIds;
        await using (var platformScope = scopeFactory.CreateAsyncScope())
        {
            var tenant = platformScope.ServiceProvider.GetService<ICurrentTenantAccessor>();
            var processor = platformScope.ServiceProvider.GetService<IAuditPackageExportProcessor>();
            if (tenant is null || processor is null)
            {
                return;
            }

            tenant.SetPlatformScope();
            tenantIds = await processor.ListQueuedTenantIdsAsync(
                Math.Clamp(_settings.TenantBatchSize, 1, 100),
                staleBefore,
                cancellationToken);
        }

        foreach (var tenantId in tenantIds)
        {
            await using var tenantScope = scopeFactory.CreateAsyncScope();
            var tenant = tenantScope.ServiceProvider.GetRequiredService<ICurrentTenantAccessor>();
            var processor = tenantScope.ServiceProvider.GetRequiredService<IAuditPackageExportProcessor>();
            tenant.SetTenant(tenantId, "audit-package-export");

            await processor.RecoverStaleRunningAsync(
                staleBefore,
                DateTimeOffset.UtcNow,
                cancellationToken);
            var jobIds = await processor.ListQueuedJobIdsAsync(
                Math.Clamp(_settings.JobBatchSize, 1, 25),
                cancellationToken);
            foreach (var jobId in jobIds)
            {
                try
                {
                    await processor.ProcessAsync(jobId, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    logger.LogError(
                        exception,
                        "Audit package export job processing failed for job {JobId}.",
                        jobId);
                }
            }
        }
    }
}
