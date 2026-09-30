using System.Collections.Concurrent;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Notifications;
using Coglatas.Domain.Enums;
using Coglatas.Web.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Coglatas.Tests.Notifications;

[Trait("Scope", "TaskV1PR07C")]
public sealed class TaskDeadlineDigestWorkerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TenantAndClaimFailuresDoNotPreventLaterClaimProcessing()
    {
        var failedTenantId = Guid.NewGuid();
        var processingTenantId = Guid.NewGuid();
        var failedClaim = CreateClaim(processingTenantId);
        var succeedingClaim = CreateClaim(processingTenantId);
        var state = new WorkerState();
        state.TenantIds.AddRange([failedTenantId, processingTenantId]);
        state.SchedulerFailures[failedTenantId] = new InvalidOperationException("tenant-private-failure");
        state.ClaimsByTenant[processingTenantId] = [failedClaim, succeedingClaim];
        state.GenerationFailures[failedClaim.JobId] = new TimeoutException("user-private-failure");
        await using var provider = BuildProvider(state);
        var logger = new RecordingLogger<TaskDeadlineDigestWorker>();
        var worker = CreateWorker(provider, logger, new TaskDeadlineDigestWorkerOptions
        {
            TenantPageSize = 10
        });

        await worker.RunOnceAsync();

        Assert.Equal([failedTenantId, processingTenantId], state.SchedulerCalls.Select(call => call.TenantId));
        Assert.Equal(
            new[] { failedClaim.JobId, succeedingClaim.JobId }.Order(),
            state.GenerationCalls.Select(call => call.JobId).Order());
        Assert.Equal([failedClaim.JobId], state.FailureCalls.Select(call => call.JobId));
        Assert.Equal([succeedingClaim.JobId], state.SucceededGenerationJobIds);
    }

    [Fact]
    public async Task CancellationDuringGenerationPropagatesAndStopsNotYetStartedClaims()
    {
        var tenantId = Guid.NewGuid();
        var cancellingClaim = CreateClaim(tenantId);
        var laterClaim = CreateClaim(tenantId);
        using var cancellation = new CancellationTokenSource();
        var state = new WorkerState
        {
            CancelDuringGenerationJobId = cancellingClaim.JobId,
            CancellationSource = cancellation
        };
        state.TenantIds.Add(tenantId);
        state.ClaimsByTenant[tenantId] = [cancellingClaim, laterClaim];
        await using var provider = BuildProvider(state);
        var logger = new RecordingLogger<TaskDeadlineDigestWorker>();
        var worker = CreateWorker(provider, logger, new TaskDeadlineDigestWorkerOptions());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => worker.RunOnceAsync(cancellation.Token));

        Assert.Equal([cancellingClaim.JobId], state.GenerationCalls.Select(call => call.JobId));
        Assert.Empty(state.FailureCalls);
        Assert.Empty(state.SucceededGenerationJobIds);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task ClaimedBatchBeginsGenerationConcurrently()
    {
        var tenantId = Guid.NewGuid();
        var claims = Enumerable.Range(0, 4).Select(_ => CreateClaim(tenantId)).ToArray();
        var generationStartGate = new GenerationStartGate(claims.Length);
        var state = new WorkerState
        {
            GenerationStartGate = generationStartGate
        };
        state.TenantIds.Add(tenantId);
        state.ClaimsByTenant[tenantId] = claims;
        await using var provider = BuildProvider(state);
        var logger = new RecordingLogger<TaskDeadlineDigestWorker>();
        var worker = CreateWorker(provider, logger, new TaskDeadlineDigestWorkerOptions
        {
            ClaimBatchSize = claims.Length
        });

        await worker.RunOnceAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(claims.Length, generationStartGate.ArrivalCount);
        Assert.Equal(
            claims.Select(claim => claim.JobId).Order(),
            state.GenerationCalls.Select(call => call.JobId).Order());
        Assert.Equal(
            claims.Select(claim => claim.JobId).Order(),
            state.SucceededGenerationJobIds.Order());
        Assert.Empty(state.FailureCalls);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task TenantAndSchedulePagingUseTheirConfiguredUpperBounds()
    {
        var tenantIds = Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToArray();
        var scheduleHeavyTenantId = tenantIds[0];
        var state = new WorkerState();
        state.TenantIds.AddRange(tenantIds);
        state.ScheduleCandidatesByTenant[scheduleHeavyTenantId] = Enumerable.Range(0, 501)
            .Select(_ => new TaskDeadlineDigestScheduleCandidate(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "UTC",
                new TimeOnly(8, 0)))
            .ToArray();
        await using var provider = BuildProvider(state, useRealScheduler: true);
        var logger = new RecordingLogger<TaskDeadlineDigestWorker>();
        var worker = CreateWorker(provider, logger, new TaskDeadlineDigestWorkerOptions
        {
            TenantPageSize = 999,
            SchedulePageSize = 999,
            ClaimBatchSize = 999,
            ClaimTimeoutSeconds = 0
        });

        await worker.RunOnceAsync();

        Assert.Equal([(0, 100), (1, 100)], state.TenantPageRequests);
        Assert.Equal(
            [(scheduleHeavyTenantId, 0, 500), (scheduleHeavyTenantId, 1, 500)],
            state.SchedulePageRequests.Where(request => request.TenantId == scheduleHeavyTenantId));
        Assert.Equal(
            [(scheduleHeavyTenantId, 500), (scheduleHeavyTenantId, 1)],
            state.UpsertRequests);
        Assert.Equal(101, state.ClaimRequests.Count);
        Assert.All(state.ClaimRequests, request =>
        {
            Assert.Equal(100, request.BatchSize);
            Assert.Equal(TimeSpan.FromSeconds(1), request.ClaimTimeout);
        });
    }

    [Fact]
    public async Task FailureLogsContainSafeCodesButNoExceptionMessagesOrResourceIds()
    {
        var failedTenantId = Guid.NewGuid();
        var processingTenantId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var claim = CreateClaim(processingTenantId);
        var state = new WorkerState();
        state.TenantIds.AddRange([failedTenantId, processingTenantId]);
        state.SchedulerFailures[failedTenantId] = new InvalidOperationException(
            $"private-title tenant={failedTenantId}");
        state.ClaimsByTenant[processingTenantId] = [claim];
        state.GenerationFailures[claim.JobId] = new TimeoutException(
            $"private-comment user={claim.UserId} workspace={claim.WorkspaceId} task={taskId}");
        state.FailureHandlerFailures[claim.JobId] = new InvalidOperationException(
            $"failure-store claim={claim.ClaimToken} job={claim.JobId}");
        await using var provider = BuildProvider(state);
        var logger = new RecordingLogger<TaskDeadlineDigestWorker>();
        var worker = CreateWorker(provider, logger, new TaskDeadlineDigestWorkerOptions
        {
            TenantPageSize = 10
        });

        await worker.RunOnceAsync();

        Assert.Equal(3, logger.Entries.Count);
        Assert.All(logger.Entries, entry => Assert.Null(entry.Exception));
        var errorCodes = logger.Entries
            .SelectMany(entry => entry.Properties)
            .Where(property => property.Key == "ErrorCode")
            .Select(property => Assert.IsType<string>(property.Value))
            .ToArray();
        Assert.Equal(
            [TaskDeadlineDigestErrorCodes.GenerationFailure, TaskDeadlineDigestErrorCodes.GenerationTimeout],
            errorCodes);
        Assert.All(
            logger.Entries.SelectMany(entry => entry.Properties)
                .Where(property => property.Key != "{OriginalFormat}"),
            property => Assert.Equal("ErrorCode", property.Key));

        var renderedLogs = string.Join(
            Environment.NewLine,
            logger.Entries.SelectMany(entry =>
                entry.Properties.Select(property => $"{entry.Message}|{property.Key}={property.Value}")));
        Assert.Contains(TaskDeadlineDigestErrorCodes.GenerationFailure, renderedLogs, StringComparison.Ordinal);
        Assert.Contains(TaskDeadlineDigestErrorCodes.GenerationTimeout, renderedLogs, StringComparison.Ordinal);
        Assert.DoesNotContain("private-title", renderedLogs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-comment", renderedLogs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("failure-store", renderedLogs, StringComparison.OrdinalIgnoreCase);
        foreach (var sensitiveId in new[]
                 {
                     failedTenantId,
                     processingTenantId,
                     claim.UserId,
                     claim.WorkspaceId,
                     taskId,
                     claim.JobId,
                     claim.ClaimToken
                 })
        {
            Assert.DoesNotContain(sensitiveId.ToString(), renderedLogs, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static TaskDeadlineDigestWorker CreateWorker(
        ServiceProvider provider,
        ILogger<TaskDeadlineDigestWorker> logger,
        TaskDeadlineDigestWorkerOptions options) => new(
        provider.GetRequiredService<IServiceScopeFactory>(),
        new FixedClock(Now),
        Options.Create(options),
        logger);

    private static ServiceProvider BuildProvider(WorkerState state, bool useRealScheduler = false)
    {
        var services = new ServiceCollection();
        services.AddSingleton(state);
        services.AddSingleton<TaskDeadlineDigestDiagnostics>();
        services.AddSingleton<IFeatureFlagService, EnabledFeatureFlags>();
        services.AddScoped<CurrentTenantService>();
        services.AddScoped<ICurrentTenantAccessor>(serviceProvider =>
            serviceProvider.GetRequiredService<CurrentTenantService>());
        services.AddScoped<ICurrentTenant>(serviceProvider =>
            serviceProvider.GetRequiredService<CurrentTenantService>());
        services.AddScoped<ITaskDeadlineDigestRepository, WorkerRepository>();
        if (useRealScheduler)
            services.AddScoped<ITaskDeadlineDigestScheduler, TaskDeadlineDigestScheduler>();
        else
            services.AddScoped<ITaskDeadlineDigestScheduler, WorkerScheduler>();
        services.AddScoped<ITaskDeadlineDigestGenerator, WorkerGenerator>();
        services.AddScoped<ITaskDeadlineDigestFailureHandler, WorkerFailureHandler>();
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static TaskDeadlineDigestClaim CreateClaim(Guid tenantId) => new(
        Guid.NewGuid(),
        tenantId,
        Guid.NewGuid(),
        Guid.NewGuid(),
        new DateOnly(2026, 8, 4),
        TaskDeadlineDigestPolicy.PolicyVersion,
        Guid.NewGuid(),
        TaskDeadlineDigestAttemptTrigger.Automatic);

    private sealed record FixedClock(DateTimeOffset UtcNow) : IClock;

    private sealed class EnabledFeatureFlags : IFeatureFlagService
    {
        public Task<bool> IsEnabledAsync(
            string featureKey,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(FeatureKeys.TasksNotificationsV1, featureKey);
            return Task.FromResult(true);
        }

        public Task<Result> RequireEnabledAsync(
            string featureKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

        public Task<IReadOnlyList<string>> GetEnabledFeaturesAsync(
            Guid tenantId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([FeatureKeys.TasksNotificationsV1]);
    }

    private sealed class WorkerScheduler(WorkerState state, ICurrentTenant currentTenant)
        : ITaskDeadlineDigestScheduler
    {
        public Task<IReadOnlyList<TaskDeadlineDigestClaim>> ScheduleAndClaimAsync(
            string claimOwner,
            DateTimeOffset now,
            TaskDeadlineDigestRunSettings settings,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tenantId = currentTenant.TenantId;
            state.SchedulerCalls.Add(new SchedulerCall(tenantId, settings));
            if (state.SchedulerFailures.TryGetValue(tenantId, out var exception))
                throw exception;
            return Task.FromResult(
                state.ClaimsByTenant.TryGetValue(tenantId, out var claims)
                    ? claims
                    : (IReadOnlyList<TaskDeadlineDigestClaim>)[]);
        }
    }

    private sealed class WorkerGenerator(WorkerState state, ICurrentTenant currentTenant)
        : ITaskDeadlineDigestGenerator
    {
        public async Task<TaskDeadlineDigestGenerationResult> GenerateAsync(
            TaskDeadlineDigestClaim claim,
            DateTimeOffset now,
            int candidatePageSize,
            CancellationToken cancellationToken = default)
        {
            state.GenerationCalls.Enqueue(new GenerationCall(currentTenant.TenantId, claim.JobId));
            if (state.GenerationStartGate is not null)
                await state.GenerationStartGate.ArriveAsync(cancellationToken);
            if (state.CancelDuringGenerationJobId == claim.JobId)
                state.CancellationSource!.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            if (state.GenerationFailures.TryGetValue(claim.JobId, out var exception))
                throw exception;
            state.SucceededGenerationJobIds.Enqueue(claim.JobId);
            return new TaskDeadlineDigestGenerationResult(
                TaskDeadlineDigestGenerationOutcome.Succeeded,
                new TaskDeadlineDigestCategoryCounts(0, 0, 1, 0),
                Guid.NewGuid());
        }
    }

    private sealed class WorkerFailureHandler(WorkerState state, ICurrentTenant currentTenant)
        : ITaskDeadlineDigestFailureHandler
    {
        public Task<TaskDeadlineDigestTransition> FailAsync(
            TaskDeadlineDigestClaim claim,
            string safeErrorCode,
            DateTimeOffset now,
            TimeSpan retryDelay,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            state.FailureCalls.Enqueue(new FailureCall(currentTenant.TenantId, claim.JobId, safeErrorCode));
            if (state.FailureHandlerFailures.TryGetValue(claim.JobId, out var exception))
                throw exception;
            return Task.FromResult(new TaskDeadlineDigestTransition(true, false));
        }
    }

    private sealed class WorkerRepository(WorkerState state, ICurrentTenant currentTenant)
        : ITaskDeadlineDigestRepository
    {
        public Task<IReadOnlyList<Guid>> ListActiveTenantIdsAsync(
            int page,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            state.TenantPageRequests.Add((page, pageSize));
            return Task.FromResult<IReadOnlyList<Guid>>(state.TenantIds
                .Skip(page * pageSize)
                .Take(pageSize)
                .ToArray());
        }

        public Task<string?> GetTenantTimeZoneIdAsync(
            Guid tenantId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<string?>("UTC");
        }

        public Task<IReadOnlyList<TaskDeadlineDigestScheduleCandidate>> ListScheduleCandidatesAsync(
            int page,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tenantId = currentTenant.TenantId;
            state.SchedulePageRequests.Add((tenantId, page, pageSize));
            var candidates = state.ScheduleCandidatesByTenant.TryGetValue(tenantId, out var configured)
                ? configured
                : (IReadOnlyList<TaskDeadlineDigestScheduleCandidate>)[];
            return Task.FromResult<IReadOnlyList<TaskDeadlineDigestScheduleCandidate>>(candidates
                .Skip(page * pageSize)
                .Take(pageSize)
                .ToArray());
        }

        public Task<int> UpsertSchedulesAsync(
            IReadOnlyCollection<TaskDeadlineDigestScheduleWrite> schedules,
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            state.UpsertRequests.Add((currentTenant.TenantId, schedules.Count));
            return Task.FromResult(schedules.Count);
        }

        public Task<IReadOnlyList<TaskDeadlineDigestClaim>> ClaimDueAsync(
            string claimOwner,
            DateTimeOffset now,
            int batchSize,
            TimeSpan claimTimeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tenantId = currentTenant.TenantId;
            state.ClaimRequests.Add(new ClaimRequest(tenantId, batchSize, claimTimeout));
            return Task.FromResult(
                state.ClaimsByTenant.TryGetValue(tenantId, out var claims)
                    ? claims
                    : (IReadOnlyList<TaskDeadlineDigestClaim>)[]);
        }

        public Task<TaskDeadlineDigestClaim?> GetClaimedAsync(
            Guid jobId,
            Guid claimToken,
            bool forUpdate,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TaskDeadlineDigestClaim?> AcquireGenerationClaimFenceAsync(
            TaskDeadlineDigestClaim claim,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TaskDeadlineDigestCurrentContext?> GetCurrentContextAsync(
            Guid jobId,
            Guid claimToken,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TaskDeadlineDigestCandidate>> ListCurrentCandidatesAsync(
            Guid jobId,
            Guid claimToken,
            DateTimeOffset deadlineBeforeUtc,
            int page,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TaskDeadlineDigestGenerationFenceOutcome> AcquireGenerationFenceAsync(
            TaskDeadlineDigestClaim claim,
            TaskDeadlineDigestCurrentContext? evaluatedContext,
            IReadOnlyCollection<TaskDeadlineDigestCandidate> evaluatedCandidates,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ITaskDeadlineDigestTransaction> BeginGenerationTransactionAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> MarkSucceededAsync(
            Guid jobId,
            Guid claimToken,
            Guid? notificationId,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeferAsync(
            Guid jobId,
            Guid claimToken,
            DateTimeOffset scheduledForUtc,
            DateTimeOffset deferredAt,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> ReleaseFeatureDisabledClaimAsync(
            Guid jobId,
            Guid claimToken,
            DateTimeOffset releasedAt,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TaskDeadlineDigestTransition> MarkFailureAsync(
            Guid jobId,
            Guid claimToken,
            string errorCode,
            DateTimeOffset failedAt,
            DateTimeOffset nextAttemptAt,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TaskDeadlineDigestRestartOutcome> RestartFailedAsync(
            Guid jobId,
            Guid actorUserId,
            string reason,
            DateTimeOffset requestedAt,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TaskDeadlineDigestStoreDiagnostics> GetDiagnosticsAsync(
            DateTimeOffset now,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void ResetGenerationState()
        {
        }
    }

    private sealed class WorkerState
    {
        public List<Guid> TenantIds { get; } = [];
        public Dictionary<Guid, IReadOnlyList<TaskDeadlineDigestScheduleCandidate>> ScheduleCandidatesByTenant { get; } = [];
        public Dictionary<Guid, IReadOnlyList<TaskDeadlineDigestClaim>> ClaimsByTenant { get; } = [];
        public Dictionary<Guid, Exception> SchedulerFailures { get; } = [];
        public Dictionary<Guid, Exception> GenerationFailures { get; } = [];
        public Dictionary<Guid, Exception> FailureHandlerFailures { get; } = [];
        public List<(int Page, int PageSize)> TenantPageRequests { get; } = [];
        public List<(Guid TenantId, int Page, int PageSize)> SchedulePageRequests { get; } = [];
        public List<(Guid TenantId, int Count)> UpsertRequests { get; } = [];
        public List<ClaimRequest> ClaimRequests { get; } = [];
        public List<SchedulerCall> SchedulerCalls { get; } = [];
        public ConcurrentQueue<GenerationCall> GenerationCalls { get; } = [];
        public ConcurrentQueue<FailureCall> FailureCalls { get; } = [];
        public ConcurrentQueue<Guid> SucceededGenerationJobIds { get; } = [];
        public Guid? CancelDuringGenerationJobId { get; init; }
        public CancellationTokenSource? CancellationSource { get; init; }
        public GenerationStartGate? GenerationStartGate { get; init; }
    }

    private sealed record SchedulerCall(Guid TenantId, TaskDeadlineDigestRunSettings Settings);
    private sealed record GenerationCall(Guid TenantId, Guid JobId);
    private sealed record FailureCall(Guid TenantId, Guid JobId, string ErrorCode);
    private sealed record ClaimRequest(Guid TenantId, int BatchSize, TimeSpan ClaimTimeout);

    private sealed class GenerationStartGate(int expectedArrivals)
    {
        private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivalCount;

        public int ArrivalCount => Volatile.Read(ref _arrivalCount);

        public async Task ArriveAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _arrivalCount) == expectedArrivals)
                _allArrived.TrySetResult();
            await _allArrived.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NoopScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.ToDictionary(value => value.Key, value => value.Value)
                : new Dictionary<string, object?>();
            Entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), exception, properties));
        }
    }

    private sealed record LogEntry(
        LogLevel Level,
        string Message,
        Exception? Exception,
        IReadOnlyDictionary<string, object?> Properties);

    private sealed class NoopScope : IDisposable
    {
        public static NoopScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
