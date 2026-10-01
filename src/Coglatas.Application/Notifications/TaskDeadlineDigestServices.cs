using Coglatas.Application.Common.Interfaces;

namespace Coglatas.Application.Notifications;

public sealed record TaskDeadlineDigestRunSettings(
    int SchedulePageSize,
    int ClaimBatchSize,
    int CandidatePageSize,
    TimeSpan ClaimTimeout,
    TimeSpan RetryDelay)
{
    public int SafeSchedulePageSize => Math.Clamp(SchedulePageSize, 1, 500);
    public int SafeClaimBatchSize => Math.Clamp(ClaimBatchSize, 1, 100);
    public int SafeCandidatePageSize => Math.Clamp(CandidatePageSize, 1, 500);
    public TimeSpan SafeClaimTimeout => ClaimTimeout <= TimeSpan.Zero ? TimeSpan.FromMinutes(2) : ClaimTimeout;
    public TimeSpan SafeRetryDelay => RetryDelay <= TimeSpan.Zero ? TimeSpan.FromMinutes(1) : RetryDelay;
}

public interface ITaskDeadlineDigestScheduler
{
    Task<IReadOnlyList<TaskDeadlineDigestClaim>> ScheduleAndClaimAsync(
        string claimOwner,
        DateTimeOffset now,
        TaskDeadlineDigestRunSettings settings,
        CancellationToken cancellationToken = default);
}

public interface ITaskDeadlineDigestGenerator
{
    Task<TaskDeadlineDigestGenerationResult> GenerateAsync(
        TaskDeadlineDigestClaim claim,
        DateTimeOffset now,
        int candidatePageSize,
        CancellationToken cancellationToken = default);
}

public interface ITaskDeadlineDigestFailureHandler
{
    Task<TaskDeadlineDigestTransition> FailAsync(
        TaskDeadlineDigestClaim claim,
        string safeErrorCode,
        DateTimeOffset now,
        TimeSpan retryDelay,
        CancellationToken cancellationToken = default);
}

public enum TaskDeadlineDigestGenerationOutcome
{
    Succeeded = 0,
    SucceededWithoutCandidates = 1,
    Deferred = 2,
    ClaimLost = 3,
    FeatureDisabled = 4
}

public sealed record TaskDeadlineDigestCategoryCounts(int ThreeDays, int OneDay, int Today, int Overdue)
{
    public int Total => ThreeDays + OneDay + Today + Overdue;
}

public sealed record TaskDeadlineDigestGenerationResult(
    TaskDeadlineDigestGenerationOutcome Outcome,
    TaskDeadlineDigestCategoryCounts Counts,
    Guid? NotificationId = null);

public sealed record TaskDeadlineDigestCounterSnapshot(
    long Scheduled,
    long Claimed,
    long Succeeded,
    long SucceededWithoutCandidates,
    long Failures,
    long TerminalFailures,
    long ClaimLosses,
    long InvalidTimeZones,
    long InvalidPreferences,
    long OperatorRestarts);

public sealed class TaskDeadlineDigestDiagnostics
{
    private long scheduled;
    private long claimed;
    private long succeeded;
    private long succeededWithoutCandidates;
    private long failures;
    private long terminalFailures;
    private long claimLosses;
    private long invalidTimeZones;
    private long invalidPreferences;
    private long operatorRestarts;

    public void RecordScheduled(int count) => Interlocked.Add(ref scheduled, Math.Max(0, count));
    public void RecordClaimed(int count) => Interlocked.Add(ref claimed, Math.Max(0, count));
    public void RecordSucceeded(bool hadCandidates)
    {
        Interlocked.Increment(ref succeeded);
        if (!hadCandidates)
            Interlocked.Increment(ref succeededWithoutCandidates);
    }

    public void RecordFailure(bool terminal)
    {
        Interlocked.Increment(ref failures);
        if (terminal)
            Interlocked.Increment(ref terminalFailures);
    }

    public void RecordClaimLoss() => Interlocked.Increment(ref claimLosses);
    public void RecordInvalidTimeZone() => Interlocked.Increment(ref invalidTimeZones);
    public void RecordInvalidPreference() => Interlocked.Increment(ref invalidPreferences);
    public void RecordOperatorRestart() => Interlocked.Increment(ref operatorRestarts);

    public TaskDeadlineDigestCounterSnapshot Snapshot() => new(
        Interlocked.Read(ref scheduled),
        Interlocked.Read(ref claimed),
        Interlocked.Read(ref succeeded),
        Interlocked.Read(ref succeededWithoutCandidates),
        Interlocked.Read(ref failures),
        Interlocked.Read(ref terminalFailures),
        Interlocked.Read(ref claimLosses),
        Interlocked.Read(ref invalidTimeZones),
        Interlocked.Read(ref invalidPreferences),
        Interlocked.Read(ref operatorRestarts));
}

public sealed class TaskDeadlineDigestScheduler(
    ITaskDeadlineDigestRepository repository,
    IFeatureFlagService featureFlags,
    ICurrentTenant currentTenant,
    TaskDeadlineDigestDiagnostics diagnostics) : ITaskDeadlineDigestScheduler
{
    public async Task<IReadOnlyList<TaskDeadlineDigestClaim>> ScheduleAndClaimAsync(
        string claimOwner,
        DateTimeOffset now,
        TaskDeadlineDigestRunSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (!await featureFlags.IsEnabledAsync(FeatureKeys.TasksNotificationsV1, cancellationToken))
            return [];
        if (!currentTenant.IsAvailable)
            throw new InvalidOperationException("A tenant scope is required for Task deadline digest scheduling.");

        var tenantTimeZoneId = await repository.GetTenantTimeZoneIdAsync(
            currentTenant.TenantId,
            cancellationToken);
        for (var page = 0; ; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidates = await repository.ListScheduleCandidatesAsync(
                page,
                settings.SafeSchedulePageSize,
                cancellationToken);
            if (candidates.Count == 0)
                break;

            var schedules = new List<TaskDeadlineDigestScheduleWrite>(candidates.Count);
            foreach (var candidate in candidates)
            {
                if (!TaskDeadlineDigestPolicy.IsValidLocalTime(candidate.EffectiveLocalTime))
                {
                    diagnostics.RecordInvalidPreference();
                    continue;
                }

                var zone = ResolveTimeZone(candidate.WorkspaceTimeZoneId, tenantTimeZoneId);
                if (zone.HadInvalidIdentity)
                    diagnostics.RecordInvalidTimeZone();
                var schedule = TaskDeadlineDigestPolicy.ResolveSchedule(
                    now,
                    candidate.EffectiveLocalTime,
                    zone.TimeZone);
                schedules.Add(new TaskDeadlineDigestScheduleWrite(
                    Guid.NewGuid(),
                    candidate.WorkspaceId,
                    candidate.UserId,
                    schedule.LocalDate,
                    TaskDeadlineDigestPolicy.PolicyVersion,
                    schedule.DueAtUtc));
            }

            diagnostics.RecordScheduled(await repository.UpsertSchedulesAsync(
                schedules,
                now,
                cancellationToken));
            if (candidates.Count < settings.SafeSchedulePageSize)
                break;
        }

        var claims = await repository.ClaimDueAsync(
            claimOwner,
            now,
            settings.SafeClaimBatchSize,
            settings.SafeClaimTimeout,
            cancellationToken);
        diagnostics.RecordClaimed(claims.Count);
        return claims;
    }

    internal static TaskDeadlineDigestTimeZoneResolution ResolveTimeZone(
        string? workspaceTimeZoneId,
        string? tenantTimeZoneId)
    {
        if (TryResolve(workspaceTimeZoneId, out var workspaceTimeZone))
            return new TaskDeadlineDigestTimeZoneResolution(workspaceTimeZone!, false);

        var invalidWorkspace = !string.IsNullOrWhiteSpace(workspaceTimeZoneId);
        if (TryResolve(tenantTimeZoneId, out var tenantTimeZone))
            return new TaskDeadlineDigestTimeZoneResolution(tenantTimeZone!, invalidWorkspace);

        var invalidTenant = !string.IsNullOrWhiteSpace(tenantTimeZoneId) &&
                            !string.Equals(tenantTimeZoneId.Trim(), "UTC", StringComparison.OrdinalIgnoreCase);
        return new TaskDeadlineDigestTimeZoneResolution(TimeZoneInfo.Utc, invalidWorkspace || invalidTenant);
    }

    private static bool TryResolve(string? timeZoneId, out TimeZoneInfo? timeZone)
    {
        timeZone = null;
        if (string.IsNullOrWhiteSpace(timeZoneId))
            return false;
        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId.Trim());
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }
}

public sealed record TaskDeadlineDigestTimeZoneResolution(
    TimeZoneInfo TimeZone,
    bool HadInvalidIdentity);

/// <summary>
/// Safe application-layer marker for a transaction conflict that the
/// infrastructure has classified as retryable. Provider exception types and
/// SQLSTATE values deliberately remain in Infrastructure.
/// </summary>
public sealed class TaskDeadlineDigestRetryablePersistenceConflictException()
    : Exception("Task deadline digest persistence conflicted with concurrent state.");

public sealed class TaskDeadlineDigestGenerator(
    ITaskDeadlineDigestRepository repository,
    INotificationService notifications,
    IFeatureFlagService featureFlags,
    TaskDeadlineDigestDiagnostics diagnostics) : ITaskDeadlineDigestGenerator
{
    private static readonly TaskDeadlineDigestCategoryCounts EmptyCounts = new(0, 0, 0, 0);
    private const int MaximumGenerationTransactionAttempts = 3;

    public async Task<TaskDeadlineDigestGenerationResult> GenerateAsync(
        TaskDeadlineDigestClaim claim,
        DateTimeOffset now,
        int candidatePageSize,
        CancellationToken cancellationToken = default)
    {
        if (!await featureFlags.IsEnabledAsync(FeatureKeys.TasksNotificationsV1, cancellationToken))
            return await ReleaseFeatureDisabledClaimAsync(claim, EmptyCounts, now, cancellationToken);

        for (var transactionAttempt = 0;
             transactionAttempt < MaximumGenerationTransactionAttempts;
             transactionAttempt++)
        {
            var retry = false;
            try
            {
                await using (var transaction = await repository.BeginGenerationTransactionAsync(cancellationToken))
                {
                    // Claim ownership is the first generation-transaction
                    // fence. Holding Job then Attempt through commit keeps an
                    // expired-claim scanner from reclaiming this Job while a
                    // same-recipient generation waits on the User row below.
                    var fencedClaim = await repository.AcquireGenerationClaimFenceAsync(
                        claim,
                        cancellationToken);
                    if (fencedClaim is null)
                    {
                        diagnostics.RecordClaimLoss();
                        return new TaskDeadlineDigestGenerationResult(
                            TaskDeadlineDigestGenerationOutcome.ClaimLost,
                            EmptyCounts);
                    }

                    // Candidate pages are evaluated exactly once in a normal
                    // generation attempt. The repository fence immediately
                    // locks and validates each evaluated page before its count
                    // is accepted, so no pre-transaction throwaway build can
                    // survive into a visible digest.
                    var evaluation = await EvaluateAsync(claim, now, candidatePageSize, cancellationToken);
                    switch (evaluation.FenceOutcome)
                    {
                        case TaskDeadlineDigestGenerationFenceOutcome.ClaimLost:
                            diagnostics.RecordClaimLoss();
                            return new TaskDeadlineDigestGenerationResult(
                                TaskDeadlineDigestGenerationOutcome.ClaimLost,
                                evaluation.Counts);
                        case TaskDeadlineDigestGenerationFenceOutcome.CurrentStateChanged:
                            retry = true;
                            break;
                    }

                    if (!retry && !await featureFlags.IsEnabledAsync(FeatureKeys.TasksNotificationsV1, cancellationToken))
                    {
                        return await ReleaseFeatureDisabledClaimAsync(
                            claim,
                            evaluation.Counts,
                            now,
                            cancellationToken,
                            transaction);
                    }

                    if (!retry && evaluation.DeferUntilUtc.HasValue)
                    {
                        if (!await repository.DeferAsync(
                                claim.JobId,
                                claim.ClaimToken,
                                evaluation.DeferUntilUtc.Value,
                                now,
                                cancellationToken))
                        {
                            diagnostics.RecordClaimLoss();
                            return new TaskDeadlineDigestGenerationResult(
                                TaskDeadlineDigestGenerationOutcome.ClaimLost,
                                evaluation.Counts);
                        }

                        await transaction.CommitAsync(cancellationToken);
                        return new TaskDeadlineDigestGenerationResult(
                            TaskDeadlineDigestGenerationOutcome.Deferred,
                            evaluation.Counts);
                    }

                    if (!retry)
                    {
                        Guid? notificationId = null;
                        if (evaluation.Counts.Total > 0)
                        {
                            notificationId = await notifications.StageTaskDeadlineDigestByLogicalKeyAsync(
                                claim.UserId,
                                claim.JobId,
                                TaskDeadlineDigestPolicy.BuildNotificationLogicalKey(
                                    claim.WorkspaceId,
                                    claim.LocalDate,
                                    claim.PolicyVersion),
                                cancellationToken);
                        }

                        if (!await repository.MarkSucceededAsync(
                                claim.JobId,
                                claim.ClaimToken,
                                notificationId,
                                now,
                                cancellationToken))
                        {
                            diagnostics.RecordClaimLoss();
                            return new TaskDeadlineDigestGenerationResult(
                                TaskDeadlineDigestGenerationOutcome.ClaimLost,
                                evaluation.Counts);
                        }

                        await repository.SaveChangesAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                        diagnostics.RecordSucceeded(evaluation.Counts.Total > 0);
                        return new TaskDeadlineDigestGenerationResult(
                            evaluation.Counts.Total > 0
                                ? TaskDeadlineDigestGenerationOutcome.Succeeded
                                : TaskDeadlineDigestGenerationOutcome.SucceededWithoutCandidates,
                            evaluation.Counts,
                            notificationId);
                    }
                }
            }
            catch (TaskDeadlineDigestRetryablePersistenceConflictException)
            {
                retry = true;
            }

            repository.ResetGenerationState();
            if (retry && transactionAttempt + 1 < MaximumGenerationTransactionAttempts)
                continue;

            throw new TaskDeadlineDigestRetryablePersistenceConflictException();
        }

        throw new TaskDeadlineDigestRetryablePersistenceConflictException();
    }

    private async Task<TaskDeadlineDigestEvaluation> EvaluateAsync(
        TaskDeadlineDigestClaim claim,
        DateTimeOffset now,
        int candidatePageSize,
        CancellationToken cancellationToken)
    {
        var context = await repository.GetCurrentContextAsync(
            claim.JobId,
            claim.ClaimToken,
            cancellationToken);
        var contextFence = await repository.AcquireGenerationFenceAsync(
            claim,
            context,
            [],
            cancellationToken);
        if (contextFence != TaskDeadlineDigestGenerationFenceOutcome.Current)
        {
            return new TaskDeadlineDigestEvaluation(EmptyCounts, null, contextFence);
        }

        if (context is null)
            return new TaskDeadlineDigestEvaluation(EmptyCounts, null, TaskDeadlineDigestGenerationFenceOutcome.Current);
        if (!TaskDeadlineDigestPolicy.IsValidLocalTime(context.EffectiveLocalTime))
        {
            diagnostics.RecordInvalidPreference();
            return new TaskDeadlineDigestEvaluation(EmptyCounts, null, TaskDeadlineDigestGenerationFenceOutcome.Current);
        }

        var zone = TaskDeadlineDigestScheduler.ResolveTimeZone(
            context.WorkspaceTimeZoneId,
            context.TenantTimeZoneId);
        if (zone.HadInvalidIdentity)
            diagnostics.RecordInvalidTimeZone();
        if (TaskDeadlineDigestPolicy.ResolveLocalDate(now, zone.TimeZone) != claim.LocalDate)
        {
            // A timezone change can make an old local-date row stale. Complete
            // it without a visible result; the current local date has its own
            // unique ledger identity.
            return new TaskDeadlineDigestEvaluation(EmptyCounts, null, TaskDeadlineDigestGenerationFenceOutcome.Current);
        }

        var currentDueAt = TaskDeadlineDigestPolicy.ResolveDueAtUtc(
            claim.LocalDate,
            context.EffectiveLocalTime,
            zone.TimeZone);
        if (currentDueAt > now)
            return new TaskDeadlineDigestEvaluation(EmptyCounts, currentDueAt, TaskDeadlineDigestGenerationFenceOutcome.Current);

        var deadlineBeforeUtc = TaskDeadlineDigestPolicy.ResolveDueAtUtc(
            claim.LocalDate.AddDays(4),
            TimeOnly.MinValue,
            zone.TimeZone);
        var threeDays = 0;
        var oneDay = 0;
        var today = 0;
        var overdue = 0;
        var safePageSize = Math.Clamp(candidatePageSize, 1, 500);
        for (var page = 0; ; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidates = await repository.ListCurrentCandidatesAsync(
                claim.JobId,
                claim.ClaimToken,
                deadlineBeforeUtc,
                page,
                safePageSize,
                cancellationToken);
            var pageFence = await repository.AcquireGenerationFenceAsync(
                claim,
                context,
                candidates,
                cancellationToken);
            if (pageFence != TaskDeadlineDigestGenerationFenceOutcome.Current)
            {
                return new TaskDeadlineDigestEvaluation(EmptyCounts, null, pageFence);
            }

            foreach (var candidate in candidates)
            {
                switch (TaskDeadlineDigestPolicy.Classify(candidate.DeadlineAt, now, zone.TimeZone))
                {
                    case TaskDeadlineDigestCategory.DeadlineInThreeLocalDays:
                        threeDays++;
                        break;
                    case TaskDeadlineDigestCategory.DeadlineInOneLocalDay:
                        oneDay++;
                        break;
                    case TaskDeadlineDigestCategory.DueToday:
                        today++;
                        break;
                    case TaskDeadlineDigestCategory.Overdue:
                        overdue++;
                        break;
                }
            }

            if (candidates.Count < safePageSize)
                break;
        }

        return new TaskDeadlineDigestEvaluation(
            new TaskDeadlineDigestCategoryCounts(threeDays, oneDay, today, overdue),
            null,
            TaskDeadlineDigestGenerationFenceOutcome.Current);
    }

    private async Task<TaskDeadlineDigestGenerationResult> ReleaseFeatureDisabledClaimAsync(
        TaskDeadlineDigestClaim claim,
        TaskDeadlineDigestCategoryCounts counts,
        DateTimeOffset releasedAt,
        CancellationToken cancellationToken,
        ITaskDeadlineDigestTransaction? transaction = null)
    {
        if (!await repository.ReleaseFeatureDisabledClaimAsync(
                claim.JobId,
                claim.ClaimToken,
                releasedAt,
                cancellationToken))
        {
            diagnostics.RecordClaimLoss();
            return new TaskDeadlineDigestGenerationResult(
                TaskDeadlineDigestGenerationOutcome.ClaimLost,
                counts);
        }

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        return new TaskDeadlineDigestGenerationResult(
            TaskDeadlineDigestGenerationOutcome.FeatureDisabled,
            counts);
    }

    private sealed record TaskDeadlineDigestEvaluation(
        TaskDeadlineDigestCategoryCounts Counts,
        DateTimeOffset? DeferUntilUtc,
        TaskDeadlineDigestGenerationFenceOutcome FenceOutcome);
}

public sealed class TaskDeadlineDigestFailureHandler(
    ITaskDeadlineDigestRepository repository,
    TaskDeadlineDigestDiagnostics diagnostics) : ITaskDeadlineDigestFailureHandler
{
    public async Task<TaskDeadlineDigestTransition> FailAsync(
        TaskDeadlineDigestClaim claim,
        string safeErrorCode,
        DateTimeOffset now,
        TimeSpan retryDelay,
        CancellationToken cancellationToken = default)
    {
        var code = TaskDeadlineDigestErrorCodes.Normalize(safeErrorCode);
        var transition = await repository.MarkFailureAsync(
            claim.JobId,
            claim.ClaimToken,
            code,
            now,
            now + (retryDelay <= TimeSpan.Zero ? TimeSpan.FromMinutes(1) : retryDelay),
            cancellationToken);
        if (transition.Changed)
            diagnostics.RecordFailure(transition.Terminal);
        else
            diagnostics.RecordClaimLoss();
        return transition;
    }
}

public static class TaskDeadlineDigestErrorCodes
{
    public const string GenerationFailure = "DigestGenerationFailure";
    public const string GenerationTimeout = "DigestGenerationTimeout";
    public const string PersistenceConflict = "DigestPersistenceConflict";

    public static string FromException(Exception exception) => exception switch
    {
        TimeoutException => GenerationTimeout,
        TaskDeadlineDigestRetryablePersistenceConflictException => PersistenceConflict,
        _ when exception.GetType().Name.Contains("Concurrency", StringComparison.Ordinal) => PersistenceConflict,
        _ => GenerationFailure
    };

    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return GenerationFailure;
        var trimmed = value.Trim();
        return trimmed switch
        {
            GenerationFailure => GenerationFailure,
            GenerationTimeout => GenerationTimeout,
            PersistenceConflict => PersistenceConflict,
            _ => GenerationFailure
        };
    }
}
