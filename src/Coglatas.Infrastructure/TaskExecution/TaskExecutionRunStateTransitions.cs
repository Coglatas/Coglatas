using Coglatas.Application.Common.Interfaces;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Persistence;

namespace Coglatas.Infrastructure.TaskExecution;

internal static class TaskExecutionRunStateTransitions
{
    internal static async Task AdvanceToRunningAsync(
        TaskExecutionRun run,
        AppDbContext dbContext,
        IClock clock,
        Func<TaskExecutionRun, string, CancellationToken, Task> auditLifecycleAsync,
        CancellationToken cancellationToken)
    {
        if (run.Status == TaskExecutionRunStatus.Accepted)
        {
            run.Status = TaskExecutionRunStatus.Queued;
            run.QueuedAtUtc = clock.UtcNow;
            run.VersionNo++;
            await auditLifecycleAsync(run, "TaskExecutionRunQueued", cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        if (run.Status == TaskExecutionRunStatus.Queued)
        {
            run.Status = TaskExecutionRunStatus.Running;
            run.StartedAtUtc = clock.UtcNow;
            run.VersionNo++;
            await auditLifecycleAsync(run, "TaskExecutionRunStarted", cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}
