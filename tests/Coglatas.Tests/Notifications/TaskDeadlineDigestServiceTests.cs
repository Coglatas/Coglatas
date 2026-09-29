using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Notifications;
using Coglatas.Domain.Enums;

namespace Coglatas.Tests.Notifications;

[Trait("Scope", "TaskV1PR07C")]
public sealed class TaskDeadlineDigestServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FeatureDisabledAfterClaimReleasesClaimWithoutConsumingAttempt()
    {
        var repository = new FakeDigestRepository();
        var featureFlags = new FakeFeatureFlags(enabled: false);
        var diagnostics = new TaskDeadlineDigestDiagnostics();
        var scheduler = new TaskDeadlineDigestScheduler(
            repository,
            featureFlags,
            new FakeCurrentTenant(Guid.Empty, IsAvailable: false),
            diagnostics);
        var notifications = new FakeNotifications();
        var generator = new TaskDeadlineDigestGenerator(
            repository,
            notifications,
            featureFlags,
            diagnostics);

        var claims = await scheduler.ScheduleAndClaimAsync(
            "disabled-worker",
            Now,
            Settings());
        var generation = await generator.GenerateAsync(CreateClaim(), Now, candidatePageSize: 20);

        Assert.Empty(claims);
        Assert.Equal(TaskDeadlineDigestGenerationOutcome.FeatureDisabled, generation.Outcome);
        Assert.Equal(1, repository.ReleaseFeatureDisabledCallCount);
        Assert.Equal(0, notifications.CallCount);
        Assert.Equal(2, featureFlags.CallCount);
    }

    [Fact]
    public async Task SchedulerBoundsSchedulePagesAndClaimSettings()
    {
        var allCandidates = Enumerable.Range(0, 501)
            .Select(_ => new TaskDeadlineDigestScheduleCandidate(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "UTC",
                new TimeOnly(8, 0)))
            .ToArray();
        var claimed = CreateClaim();
        var repository = new FakeDigestRepository
        {
            ScheduleCandidates = (page, pageSize) => allCandidates
                .Skip(page * pageSize)
                .Take(pageSize)
                .ToArray(),
            Claims = [claimed]
        };
        var diagnostics = new TaskDeadlineDigestDiagnostics();
        var scheduler = new TaskDeadlineDigestScheduler(
            repository,
            new FakeFeatureFlags(enabled: true),
            new FakeCurrentTenant(claimed.TenantId, IsAvailable: true),
            diagnostics);

        var result = await scheduler.ScheduleAndClaimAsync(
            "bounded-worker",
            Now,
            new TaskDeadlineDigestRunSettings(
                SchedulePageSize: 900,
                ClaimBatchSize: 900,
                CandidatePageSize: 900,
                ClaimTimeout: TimeSpan.Zero,
                RetryDelay: TimeSpan.Zero));

        Assert.Equal([claimed], result);
        Assert.Equal([(0, 500), (1, 500)], repository.SchedulePageRequests);
        Assert.Equal([500, 1], repository.UpsertBatches.Select(batch => batch.Count));
        var claimRequest = Assert.Single(repository.ClaimRequests);
        Assert.Equal(100, claimRequest.BatchSize);
        Assert.Equal(TimeSpan.FromMinutes(2), claimRequest.ClaimTimeout);
        var snapshot = diagnostics.Snapshot();
        Assert.Equal(501, snapshot.Scheduled);
        Assert.Equal(1, snapshot.Claimed);
    }

    [Fact]
    public async Task RepeatedIdenticalScheduleDoesNotIncrementScheduledDiagnostic()
    {
        var tenantId = Guid.NewGuid();
        var repository = new FakeDigestRepository
        {
            ScheduleCandidates = (page, _) => page == 0
                ? [new TaskDeadlineDigestScheduleCandidate(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    "UTC",
                    new TimeOnly(8, 0))]
                : []
        };
        repository.UpsertResults.Enqueue(1);
        repository.UpsertResults.Enqueue(0);
        var diagnostics = new TaskDeadlineDigestDiagnostics();
        var scheduler = new TaskDeadlineDigestScheduler(
            repository,
            new FakeFeatureFlags(enabled: true),
            new FakeCurrentTenant(tenantId, IsAvailable: true),
            diagnostics);

        await scheduler.ScheduleAndClaimAsync("schedule-worker", Now, Settings());
        await scheduler.ScheduleAndClaimAsync("schedule-worker", Now.AddMinutes(1), Settings());

        Assert.Equal(1, diagnostics.Snapshot().Scheduled);
        Assert.Equal([1, 1], repository.UpsertBatches.Select(batch => batch.Count));
    }

    [Fact]
    public async Task SchedulerKeepsEachWorkspaceInItsEffectiveTimezone()
    {
        var tenantId = Guid.NewGuid();
        var utcWorkspace = Guid.NewGuid();
        var tokyoWorkspace = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var repository = new FakeDigestRepository
        {
            TenantTimeZoneId = "Asia/Tokyo",
            ScheduleCandidates = (page, _) => page == 0
                ?
                [
                    new TaskDeadlineDigestScheduleCandidate(
                        utcWorkspace,
                        userId,
                        "UTC",
                        new TimeOnly(8, 0)),
                    new TaskDeadlineDigestScheduleCandidate(
                        tokyoWorkspace,
                        userId,
                        null,
                        new TimeOnly(8, 0))
                ]
                : []
        };
        var scheduler = new TaskDeadlineDigestScheduler(
            repository,
            new FakeFeatureFlags(enabled: true),
            new FakeCurrentTenant(tenantId, IsAvailable: true),
            new TaskDeadlineDigestDiagnostics());
        var currentInstant = new DateTimeOffset(2026, 8, 3, 15, 30, 0, TimeSpan.Zero);

        await scheduler.ScheduleAndClaimAsync(
            "timezone-worker",
            currentInstant,
            Settings());

        var schedules = Assert.Single(repository.UpsertBatches);
        var utc = Assert.Single(schedules, item => item.WorkspaceId == utcWorkspace);
        Assert.Equal(new DateOnly(2026, 8, 3), utc.LocalDate);
        Assert.Equal(new DateTimeOffset(2026, 8, 3, 8, 0, 0, TimeSpan.Zero), utc.ScheduledForUtc);
        var tokyo = Assert.Single(schedules, item => item.WorkspaceId == tokyoWorkspace);
        Assert.Equal(new DateOnly(2026, 8, 4), tokyo.LocalDate);
        Assert.Equal(new DateTimeOffset(2026, 8, 3, 23, 0, 0, TimeSpan.Zero), tokyo.ScheduledForUtc);
        Assert.All(schedules, item => Assert.Equal(TaskDeadlineDigestPolicy.PolicyVersion, item.PolicyVersion));
    }

    [Fact]
    public async Task GeneratorEvaluatesCandidatesOnceOnNormalSuccess()
    {
        var claim = CreateClaim();
        var repository = ReadyGeneratorRepository(claim);
        repository.CandidatePages = (_, page, _) => page switch
        {
            0 =>
            [
                new TaskDeadlineDigestCandidate(Guid.NewGuid(), new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero)),
                new TaskDeadlineDigestCandidate(Guid.NewGuid(), new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero))
            ],
            1 =>
            [
                new TaskDeadlineDigestCandidate(Guid.NewGuid(), new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero)),
                new TaskDeadlineDigestCandidate(Guid.NewGuid(), new DateTimeOffset(2026, 8, 4, 8, 59, 0, TimeSpan.Zero))
            ],
            _ => []
        };
        var notifications = new FakeNotifications();
        var generator = CreateGenerator(repository, notifications);

        var result = await generator.GenerateAsync(claim, Now, candidatePageSize: 2);

        Assert.Equal(TaskDeadlineDigestGenerationOutcome.Succeeded, result.Outcome);
        Assert.Equal(new TaskDeadlineDigestCategoryCounts(1, 1, 1, 1), result.Counts);
        Assert.Equal(
            [(1, 0, 2), (1, 1, 2), (1, 2, 2)],
            repository.CandidatePageRequests);
        Assert.Equal(1, repository.CurrentContextCallCount);
        Assert.Equal(1, notifications.CallCount);
        Assert.Equal(1, repository.GenerationClaimFenceCallCount);
    }

    [Fact]
    public async Task GeneratorKeepsCandidatePagesBoundedInsideTransaction()
    {
        var claim = CreateClaim();
        var repository = ReadyGeneratorRepository(claim);
        repository.CandidatePages = (_, page, pageSize) =>
        {
            Assert.InRange(pageSize, 1, 500);
            return page == 0
                ? [new TaskDeadlineDigestCandidate(Guid.NewGuid(), Now.AddHours(1))]
                : [];
        };
        var notifications = new FakeNotifications();
        var generator = CreateGenerator(repository, notifications);

        var result = await generator.GenerateAsync(claim, Now, candidatePageSize: int.MaxValue);

        Assert.Equal(TaskDeadlineDigestGenerationOutcome.Succeeded, result.Outcome);
        Assert.Equal([(1, 0, 500)], repository.CandidatePageRequests);
        Assert.Equal(1, repository.CurrentContextCallCount);
        Assert.Equal(1, notifications.CallCount);
    }

    [Fact]
    public async Task GeneratorEvaluatesCandidatesOnceOnZeroCandidateSuccess()
    {
        var claim = CreateClaim();
        var repository = ReadyGeneratorRepository(claim);
        var notifications = new FakeNotifications();
        var diagnostics = new TaskDeadlineDigestDiagnostics();
        var generator = CreateGenerator(repository, notifications, diagnostics);

        var result = await generator.GenerateAsync(claim, Now, candidatePageSize: 20);

        Assert.Equal(TaskDeadlineDigestGenerationOutcome.SucceededWithoutCandidates, result.Outcome);
        Assert.Null(result.NotificationId);
        Assert.Equal(0, notifications.CallCount);
        Assert.Equal(1, repository.SaveChangesCallCount);
        Assert.Equal(1, repository.LastTransaction!.CommitCount);
        Assert.Null(Assert.Single(repository.SucceededRequests).NotificationId);
        Assert.Equal(1, diagnostics.Snapshot().SucceededWithoutCandidates);
        Assert.Equal(1, repository.CurrentContextCallCount);
        Assert.Equal([(1, 0, 20)], repository.CandidatePageRequests);
    }

    [Fact]
    public async Task GeneratorSuppressesWhenCurrentContextIsLost()
    {
        var claim = CreateClaim();
        var repository = ReadyGeneratorRepository(claim);
        repository.CurrentContexts = _ => null;
        var notifications = new FakeNotifications();
        var generator = CreateGenerator(repository, notifications);

        var result = await generator.GenerateAsync(claim, Now, candidatePageSize: 20);

        Assert.Equal(TaskDeadlineDigestGenerationOutcome.SucceededWithoutCandidates, result.Outcome);
        Assert.Equal(0, result.Counts.Total);
        Assert.Null(result.NotificationId);
        Assert.Equal(1, repository.CurrentContextCallCount);
        Assert.Empty(repository.CandidatePageRequests);
        Assert.Equal(0, notifications.CallCount);
        Assert.Null(Assert.Single(repository.SucceededRequests).NotificationId);
    }

    [Fact]
    public async Task GeneratorReevaluatesOnlyWhenCommitFenceRetryOccurs()
    {
        var claim = CreateClaim();
        var repository = ReadyGeneratorRepository(claim);
        repository.CandidatePages = (_, page, _) => page == 0
            ? [new TaskDeadlineDigestCandidate(Guid.NewGuid(), Now.AddHours(1))]
            : [];
        repository.FenceOutcomes.Enqueue(TaskDeadlineDigestGenerationFenceOutcome.Current);
        repository.FenceOutcomes.Enqueue(TaskDeadlineDigestGenerationFenceOutcome.CurrentStateChanged);
        repository.FenceOutcomes.Enqueue(TaskDeadlineDigestGenerationFenceOutcome.Current);
        repository.FenceOutcomes.Enqueue(TaskDeadlineDigestGenerationFenceOutcome.Current);
        var notifications = new FakeNotifications();
        var generator = CreateGenerator(repository, notifications);

        var result = await generator.GenerateAsync(claim, Now, candidatePageSize: 20);

        Assert.Equal(TaskDeadlineDigestGenerationOutcome.Succeeded, result.Outcome);
        Assert.Equal(2, repository.CurrentContextCallCount);
        Assert.Equal([(1, 0, 20), (2, 0, 20)], repository.CandidatePageRequests);
        Assert.Equal(1, notifications.CallCount);
        Assert.Equal(1, repository.ResetGenerationStateCallCount);
    }

    [Fact]
    public async Task TimezoneChangeCompletesStaleIdentityWithoutDuplicateNotification()
    {
        var localDate = new DateOnly(2026, 8, 4);
        var claim = CreateClaim(localDate: localDate);
        var repository = ReadyGeneratorRepository(claim);
        repository.CurrentContexts = _ => ContextFor(claim, workspaceTimeZoneId: "UTC", effectiveLocalTime: TimeOnly.MinValue);
        var notifications = new FakeNotifications();
        var generator = CreateGenerator(repository, notifications);
        var currentInstant = new DateTimeOffset(2026, 8, 3, 16, 0, 0, TimeSpan.Zero);

        var result = await generator.GenerateAsync(claim, currentInstant, candidatePageSize: 20);

        Assert.Equal(TaskDeadlineDigestGenerationOutcome.SucceededWithoutCandidates, result.Outcome);
        Assert.Equal(1, repository.CurrentContextCallCount);
        Assert.Empty(repository.CandidatePageRequests);
        Assert.Equal(0, notifications.CallCount);
        var completedIdentity = Assert.Single(repository.SucceededRequests);
        Assert.Equal(claim.JobId, completedIdentity.JobId);
        Assert.Null(completedIdentity.NotificationId);
    }

    [Fact]
    public async Task GeneratorStagesStableNotificationLogicalKey()
    {
        var workspaceId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var claim = CreateClaim(workspaceId: workspaceId);
        var repository = ReadyGeneratorRepository(claim);
        repository.CandidatePages = (_, page, _) => page == 0
            ? [new TaskDeadlineDigestCandidate(Guid.NewGuid(), Now.AddHours(1))]
            : [];
        var notificationId = Guid.NewGuid();
        var notifications = new FakeNotifications { NotificationId = notificationId };
        var generator = CreateGenerator(repository, notifications);

        var result = await generator.GenerateAsync(claim, Now, candidatePageSize: 20);

        Assert.Equal(TaskDeadlineDigestGenerationOutcome.Succeeded, result.Outcome);
        Assert.Equal(notificationId, result.NotificationId);
        var staged = Assert.Single(notifications.Requests);
        Assert.Equal(claim.UserId, staged.UserId);
        Assert.Equal(claim.JobId, staged.DigestJobId);
        Assert.Equal(
            "task-deadline-digest:workspace:00112233445566778899aabbccddeeff:date:2026-08-04:policy:1",
            staged.LogicalKey);
        Assert.True(repository.FenceCallCount >= 2);
        Assert.Equal(notificationId, Assert.Single(repository.SucceededRequests).NotificationId);
    }

    [Fact]
    public async Task FailureHandlerIsolatesClaimsAndTracksTerminalAndLostTransitions()
    {
        var first = CreateClaim(jobId: Guid.NewGuid());
        var second = CreateClaim(jobId: Guid.NewGuid());
        var lost = CreateClaim(jobId: Guid.NewGuid());
        var repository = new FakeDigestRepository();
        repository.FailureTransitions.Enqueue(new TaskDeadlineDigestTransition(true, false));
        repository.FailureTransitions.Enqueue(new TaskDeadlineDigestTransition(true, true));
        repository.FailureTransitions.Enqueue(new TaskDeadlineDigestTransition(false, false));
        var diagnostics = new TaskDeadlineDigestDiagnostics();
        var handler = new TaskDeadlineDigestFailureHandler(repository, diagnostics);

        var firstResult = await handler.FailAsync(
            first,
            "  DigestGenerationTimeout  ",
            Now,
            TimeSpan.Zero);
        var secondResult = await handler.FailAsync(
            second,
            TaskDeadlineDigestErrorCodes.PersistenceConflict,
            Now,
            TimeSpan.FromMinutes(5));
        var lostResult = await handler.FailAsync(
            lost,
            "restricted Task title must not become an error code",
            Now,
            TimeSpan.FromMinutes(2));

        Assert.Equal(new TaskDeadlineDigestTransition(true, false), firstResult);
        Assert.Equal(new TaskDeadlineDigestTransition(true, true), secondResult);
        Assert.Equal(new TaskDeadlineDigestTransition(false, false), lostResult);
        Assert.Equal([first.JobId, second.JobId, lost.JobId], repository.FailureRequests.Select(item => item.JobId));
        Assert.Equal(TaskDeadlineDigestErrorCodes.GenerationTimeout, repository.FailureRequests[0].ErrorCode);
        Assert.Equal(Now.AddMinutes(1), repository.FailureRequests[0].NextAttemptAt);
        Assert.Equal(TaskDeadlineDigestErrorCodes.PersistenceConflict, repository.FailureRequests[1].ErrorCode);
        Assert.Equal(Now.AddMinutes(5), repository.FailureRequests[1].NextAttemptAt);
        Assert.Equal(TaskDeadlineDigestErrorCodes.GenerationFailure, repository.FailureRequests[2].ErrorCode);
        var snapshot = diagnostics.Snapshot();
        Assert.Equal(2, snapshot.Failures);
        Assert.Equal(1, snapshot.TerminalFailures);
        Assert.Equal(1, snapshot.ClaimLosses);
    }

    [Fact]
    public async Task SchedulerPropagatesCancellationToRepository()
    {
        var tenantId = Guid.NewGuid();
        var repository = new FakeDigestRepository();
        var scheduler = new TaskDeadlineDigestScheduler(
            repository,
            new FakeFeatureFlags(enabled: true, honorCancellation: false),
            new FakeCurrentTenant(tenantId, IsAvailable: true),
            new TaskDeadlineDigestDiagnostics());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scheduler.ScheduleAndClaimAsync(
            "cancelled-worker",
            Now,
            Settings(),
            cancellation.Token));

        Assert.Equal(1, repository.CallCount);
        Assert.Equal(cancellation.Token, repository.LastCancellationToken);
    }

    [Fact]
    public async Task GeneratorPropagatesCancellationToRepository()
    {
        var claim = CreateClaim();
        var repository = ReadyGeneratorRepository(claim);
        var generator = new TaskDeadlineDigestGenerator(
            repository,
            new FakeNotifications(),
            new FakeFeatureFlags(enabled: true, honorCancellation: false),
            new TaskDeadlineDigestDiagnostics());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generator.GenerateAsync(
            claim,
            Now,
            candidatePageSize: 20,
            cancellation.Token));

        Assert.Equal(1, repository.CallCount);
        Assert.Equal(cancellation.Token, repository.LastCancellationToken);
    }

    private static TaskDeadlineDigestRunSettings Settings() => new(
        SchedulePageSize: 100,
        ClaimBatchSize: 20,
        CandidatePageSize: 100,
        ClaimTimeout: TimeSpan.FromMinutes(2),
        RetryDelay: TimeSpan.FromMinutes(1));

    private static TaskDeadlineDigestClaim CreateClaim(
        Guid? jobId = null,
        Guid? workspaceId = null,
        DateOnly? localDate = null) => new(
        jobId ?? Guid.NewGuid(),
        Guid.NewGuid(),
        workspaceId ?? Guid.NewGuid(),
        Guid.NewGuid(),
        localDate ?? new DateOnly(2026, 8, 4),
        TaskDeadlineDigestPolicy.PolicyVersion,
        Guid.NewGuid(),
        TaskDeadlineDigestAttemptTrigger.Automatic);

    private static TaskDeadlineDigestCurrentContext ContextFor(
        TaskDeadlineDigestClaim claim,
        string? workspaceTimeZoneId = "UTC",
        TimeOnly? effectiveLocalTime = null) => new(
        claim.TenantId,
        claim.WorkspaceId,
        claim.UserId,
        workspaceTimeZoneId,
        "UTC",
        effectiveLocalTime ?? new TimeOnly(8, 0));

    private static FakeDigestRepository ReadyGeneratorRepository(TaskDeadlineDigestClaim claim) => new()
    {
        Claimed = claim,
        CurrentContexts = _ => ContextFor(claim),
        CandidatePages = (_, _, _) => []
    };

    private static TaskDeadlineDigestGenerator CreateGenerator(
        FakeDigestRepository repository,
        FakeNotifications notifications,
        TaskDeadlineDigestDiagnostics? diagnostics = null) => new(
        repository,
        notifications,
        new FakeFeatureFlags(enabled: true),
        diagnostics ?? new TaskDeadlineDigestDiagnostics());

    private sealed record FakeCurrentTenant(Guid TenantId, bool IsAvailable) : ICurrentTenant
    {
        public string? TenantSlug => IsAvailable ? "task-v1-pr07c" : null;
        public bool IsPlatformScope => false;
    }

    private sealed class FakeFeatureFlags(bool enabled, bool honorCancellation = true) : IFeatureFlagService
    {
        public int CallCount { get; private set; }

        public Task<bool> IsEnabledAsync(
            string featureKey,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Assert.Equal(FeatureKeys.TasksNotificationsV1, featureKey);
            if (honorCancellation)
                cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(enabled);
        }

        public Task<Result> RequireEnabledAsync(
            string featureKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(enabled
                ? Result.Success()
                : Result.Failure($"Feature '{featureKey}' is disabled."));

        public Task<IReadOnlyList<string>> GetEnabledFeaturesAsync(
            Guid tenantId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(enabled ? [FeatureKeys.TasksNotificationsV1] : []);
    }

    private sealed class FakeNotifications : INotificationService
    {
        public Guid NotificationId { get; init; } = Guid.NewGuid();
        public int CallCount => Requests.Count;
        public List<NotificationRequest> Requests { get; } = [];

        public Task<Guid> StageTaskDeadlineDigestByLogicalKeyAsync(
            Guid userId,
            Guid digestJobId,
            string logicalKey,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(new NotificationRequest(userId, digestJobId, logicalKey));
            return Task.FromResult(NotificationId);
        }

        public Task NotifyAsync(
            Guid recipientUserId,
            string title,
            string? body,
            string sourceType,
            Guid sourceId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed record NotificationRequest(Guid UserId, Guid DigestJobId, string LogicalKey);

    private sealed class FakeDigestTransaction : ITaskDeadlineDigestTransaction
    {
        public int CommitCount { get; private set; }
        private int DisposeCount { get; set; }

        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CommitCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeDigestRepository : ITaskDeadlineDigestRepository
    {
        public int CallCount { get; private set; }
        public CancellationToken LastCancellationToken { get; private set; }
        public string? TenantTimeZoneId { get; init; } = "UTC";
        public Func<int, int, IReadOnlyList<TaskDeadlineDigestScheduleCandidate>> ScheduleCandidates { get; init; } = (_, _) => [];
        public IReadOnlyList<TaskDeadlineDigestClaim> Claims { get; init; } = [];
        public TaskDeadlineDigestClaim? Claimed { get; set; }
        public Func<int, TaskDeadlineDigestCurrentContext?> CurrentContexts { get; set; } = _ => null;
        public Func<int, int, int, IReadOnlyList<TaskDeadlineDigestCandidate>> CandidatePages { get; set; } = (_, _, _) => [];
        private bool MarkSucceededResult { get; set; } = true;
        private bool ReleaseFeatureDisabledResult { get; set; } = true;
        public Queue<TaskDeadlineDigestTransition> FailureTransitions { get; } = new();
        public Queue<int> UpsertResults { get; } = new();
        public Queue<TaskDeadlineDigestGenerationFenceOutcome> FenceOutcomes { get; } = new();
        public List<(int Page, int PageSize)> SchedulePageRequests { get; } = [];
        public List<IReadOnlyList<TaskDeadlineDigestScheduleWrite>> UpsertBatches { get; } = [];
        public List<ClaimRequest> ClaimRequests { get; } = [];
        public List<(int Evaluation, int Page, int PageSize)> CandidatePageRequests { get; } = [];
        public List<SucceededRequest> SucceededRequests { get; } = [];
        public List<FailureRequest> FailureRequests { get; } = [];
        public int CurrentContextCallCount { get; private set; }
        public int FenceCallCount { get; private set; }
        public int GenerationClaimFenceCallCount { get; private set; }
        public int ReleaseFeatureDisabledCallCount { get; private set; }
        public int ResetGenerationStateCallCount { get; private set; }
        public int SaveChangesCallCount { get; private set; }
        public FakeDigestTransaction? LastTransaction { get; private set; }

        public Task<IReadOnlyList<Guid>> ListActiveTenantIdsAsync(
            int page,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            Touch(cancellationToken);
            return Task.FromResult<IReadOnlyList<Guid>>([]);
        }

        public Task<string?> GetTenantTimeZoneIdAsync(
            Guid tenantId,
            CancellationToken cancellationToken = default)
        {
            Touch(cancellationToken);
            return Task.FromResult(TenantTimeZoneId);
        }

        public Task<IReadOnlyList<TaskDeadlineDigestScheduleCandidate>> ListScheduleCandidatesAsync(
            int page,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            Touch(cancellationToken);
            SchedulePageRequests.Add((page, pageSize));
            return Task.FromResult(ScheduleCandidates(page, pageSize));
        }

        public Task<int> UpsertSchedulesAsync(
            IReadOnlyCollection<TaskDeadlineDigestScheduleWrite> schedules,
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            Touch(cancellationToken);
            UpsertBatches.Add(schedules.ToArray());
            return Task.FromResult(UpsertResults.Count > 0 ? UpsertResults.Dequeue() : schedules.Count);
        }

        public Task<IReadOnlyList<TaskDeadlineDigestClaim>> ClaimDueAsync(
            string claimOwner,
            DateTimeOffset now,
            int batchSize,
            TimeSpan claimTimeout,
            CancellationToken cancellationToken = default)
        {
            Touch(cancellationToken);
            ClaimRequests.Add(new ClaimRequest(claimOwner, batchSize, claimTimeout));
            return Task.FromResult(Claims);
        }

        public Task<TaskDeadlineDigestClaim?> GetClaimedAsync(
            Guid jobId,
            Guid claimToken,
            bool forUpdate,
            CancellationToken cancellationToken = default)
        {
            Touch(cancellationToken);
            return Task.FromResult(
                Claimed is not null &&
                Claimed.JobId == jobId &&
                Claimed.ClaimToken == claimToken
                    ? Claimed
                    : null);
        }

        public Task<TaskDeadlineDigestClaim?> AcquireGenerationClaimFenceAsync(
            TaskDeadlineDigestClaim claim,
            CancellationToken cancellationToken = default)
        {
            Touch(cancellationToken);
            GenerationClaimFenceCallCount++;
            return Task.FromResult(
                Claimed is not null && Claimed == claim
                    ? Claimed
                    : null);
        }

        public Task<TaskDeadlineDigestCurrentContext?> GetCurrentContextAsync(
            Guid jobId,
            Guid claimToken,
            CancellationToken cancellationToken = default)
        {
            Touch(cancellationToken);
            CurrentContextCallCount++;
            return Task.FromResult(CurrentContexts(CurrentContextCallCount));
        }

        public Task<IReadOnlyList<TaskDeadlineDigestCandidate>> ListCurrentCandidatesAsync(
            Guid jobId,
            Guid claimToken,
            DateTimeOffset deadlineBeforeUtc,
            int page,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            Touch(cancellationToken);
            CandidatePageRequests.Add((CurrentContextCallCount, page, pageSize));
            return Task.FromResult(CandidatePages(CurrentContextCallCount, page, pageSize));
        }

        public Task<TaskDeadlineDigestGenerationFenceOutcome> AcquireGenerationFenceAsync(
            TaskDeadlineDigestClaim claim,
            TaskDeadlineDigestCurrentContext? evaluatedContext,
            IReadOnlyCollection<TaskDeadlineDigestCandidate> evaluatedCandidates,
            CancellationToken cancellationToken = default)
        {
            Touch(cancellationToken);
            FenceCallCount++;
            return Task.FromResult(
                FenceOutcomes.Count > 0
                    ? FenceOutcomes.Dequeue()
                    : TaskDeadlineDigestGenerationFenceOutcome.Current);
        }

        public Task<ITaskDeadlineDigestTransaction> BeginGenerationTransactionAsync(
            CancellationToken cancellationToken = default)
        {
            Touch(cancellationToken);
            LastTransaction = new FakeDigestTransaction();
            return Task.FromResult<ITaskDeadlineDigestTransaction>(LastTransaction);
        }

        public Task<bool> MarkSucceededAsync(
            Guid jobId,
            Guid claimToken,
            Guid? notificationId,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken = default)
        {
            Touch(cancellationToken);
            SucceededRequests.Add(new SucceededRequest(jobId, claimToken, notificationId, completedAt));
            return Task.FromResult(MarkSucceededResult);
        }

        public Task<bool> DeferAsync(
            Guid jobId,
            Guid claimToken,
            DateTimeOffset scheduledForUtc,
            DateTimeOffset deferredAt,
            CancellationToken cancellationToken = default)
        {
            Touch(cancellationToken);
            return Task.FromResult(true);
        }

        public Task<bool> ReleaseFeatureDisabledClaimAsync(
            Guid jobId,
            Guid claimToken,
            DateTimeOffset releasedAt,
            CancellationToken cancellationToken = default)
        {
            Touch(cancellationToken);
            ReleaseFeatureDisabledCallCount++;
            return Task.FromResult(ReleaseFeatureDisabledResult);
        }

        public Task<TaskDeadlineDigestTransition> MarkFailureAsync(
            Guid jobId,
            Guid claimToken,
            string errorCode,
            DateTimeOffset failedAt,
            DateTimeOffset nextAttemptAt,
            CancellationToken cancellationToken = default)
        {
            Touch(cancellationToken);
            FailureRequests.Add(new FailureRequest(jobId, claimToken, errorCode, failedAt, nextAttemptAt));
            return Task.FromResult(
                FailureTransitions.Count > 0
                    ? FailureTransitions.Dequeue()
                    : new TaskDeadlineDigestTransition(true, false));
        }

        public Task<TaskDeadlineDigestRestartOutcome> RestartFailedAsync(
            Guid jobId,
            Guid actorUserId,
            string reason,
            DateTimeOffset requestedAt,
            CancellationToken cancellationToken = default)
        {
            Touch(cancellationToken);
            return Task.FromResult(TaskDeadlineDigestRestartOutcome.Restarted);
        }

        public Task<TaskDeadlineDigestStoreDiagnostics> GetDiagnosticsAsync(
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            Touch(cancellationToken);
            return Task.FromResult(new TaskDeadlineDigestStoreDiagnostics(0, 0, 0, 0, null, null));
        }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            Touch(cancellationToken);
            SaveChangesCallCount++;
            return Task.FromResult(1);
        }

        public void ResetGenerationState() => ResetGenerationStateCallCount++;

        private void Touch(CancellationToken cancellationToken)
        {
            CallCount++;
            LastCancellationToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private sealed record ClaimRequest(string ClaimOwner, int BatchSize, TimeSpan ClaimTimeout);
    private sealed record SucceededRequest(
        Guid JobId,
        Guid ClaimToken,
        Guid? NotificationId,
        DateTimeOffset CompletedAt);
    private sealed record FailureRequest(
        Guid JobId,
        Guid ClaimToken,
        string ErrorCode,
        DateTimeOffset FailedAt,
        DateTimeOffset NextAttemptAt);
}
