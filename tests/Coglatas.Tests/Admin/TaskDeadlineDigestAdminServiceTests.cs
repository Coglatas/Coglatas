using Coglatas.Application.Admin;
using Coglatas.Application.Auth;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Notifications;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;

namespace Coglatas.Tests.Admin;

[Trait("Scope", "TaskV1PR07C")]
public sealed class TaskDeadlineDigestAdminServiceTests
{
    [Fact]
    public async Task RestartRequiresCurrentActiveSystemAdmin()
    {
        var fixture = Fixture.Create(SystemRole.User);

        var result = await fixture.Service.RestartTaskDeadlineDigestAsync(
            Guid.NewGuid(),
            new RestartTaskDeadlineDigestRequest("operator restart"));

        Assert.False(result.IsSuccess);
        Assert.Equal("SystemAdmin access is required.", result.Error);
        Assert.Equal(0, fixture.Digests.RestartCalls);
        Assert.Equal(0, fixture.Diagnostics.Snapshot().OperatorRestarts);
    }

    [Fact]
    public async Task RestartRejectsEmptyJobIdentityBeforeDelegation()
    {
        var fixture = Fixture.Create(SystemRole.SystemAdmin);

        var result = await fixture.Service.RestartTaskDeadlineDigestAsync(
            Guid.Empty,
            new RestartTaskDeadlineDigestRequest("operator restart"));

        Assert.False(result.IsSuccess);
        Assert.Equal("A bounded digest restart reason is required.", result.Error);
        Assert.Equal(0, fixture.Digests.RestartCalls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RestartRejectsMissingReasonBeforeDelegation(string reason)
    {
        var fixture = Fixture.Create(SystemRole.SystemAdmin);

        var result = await fixture.Service.RestartTaskDeadlineDigestAsync(
            Guid.NewGuid(),
            new RestartTaskDeadlineDigestRequest(reason));

        Assert.False(result.IsSuccess);
        Assert.Equal("A bounded digest restart reason is required.", result.Error);
        Assert.Equal(0, fixture.Digests.RestartCalls);
    }

    [Fact]
    public async Task RestartRejectsReasonLongerThanFiveHundredCharacters()
    {
        var fixture = Fixture.Create(SystemRole.SystemAdmin);

        var result = await fixture.Service.RestartTaskDeadlineDigestAsync(
            Guid.NewGuid(),
            new RestartTaskDeadlineDigestRequest(new string('r', 501)));

        Assert.False(result.IsSuccess);
        Assert.Equal("A bounded digest restart reason is required.", result.Error);
        Assert.Equal(0, fixture.Digests.RestartCalls);
    }

    [Fact]
    public async Task SuccessfulRestartDelegatesAuditedInputsAndRecordsDiagnostic()
    {
        var fixture = Fixture.Create(SystemRole.SystemAdmin);
        var jobId = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();

        var result = await fixture.Service.RestartTaskDeadlineDigestAsync(
            jobId,
            new RestartTaskDeadlineDigestRequest("  retry after claim review  "),
            cancellation.Token);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, fixture.Digests.RestartCalls);
        Assert.Equal(jobId, fixture.Digests.LastJobId);
        Assert.Equal(fixture.Actor.Id, fixture.Digests.LastActorUserId);
        Assert.Equal("retry after claim review", fixture.Digests.LastReason);
        Assert.Equal(fixture.Clock.UtcNow, fixture.Digests.LastRequestedAt);
        Assert.Equal(cancellation.Token, fixture.Digests.LastCancellationToken);
        Assert.Equal(1, fixture.Diagnostics.Snapshot().OperatorRestarts);
        Assert.Equal(0, fixture.UnitOfWork.SaveCalls);
    }

    [Theory]
    [InlineData(TaskDeadlineDigestRestartOutcome.NotFound, "Task deadline digest job not found.")]
    [InlineData(TaskDeadlineDigestRestartOutcome.NotFailed, "Only a failed Task deadline digest can be restarted.")]
    [InlineData(TaskDeadlineDigestRestartOutcome.ActiveAttemptExists, "The Task deadline digest already has an active attempt.")]
    public async Task RejectedRestartDoesNotIncrementDiagnostic(
        TaskDeadlineDigestRestartOutcome outcome,
        string expectedError)
    {
        var fixture = Fixture.Create(SystemRole.SystemAdmin);
        fixture.Digests.RestartOutcome = outcome;

        var result = await fixture.Service.RestartTaskDeadlineDigestAsync(
            Guid.NewGuid(),
            new RestartTaskDeadlineDigestRequest("operator restart"));

        Assert.False(result.IsSuccess);
        Assert.Equal(expectedError, result.Error);
        Assert.Equal(1, fixture.Digests.RestartCalls);
        Assert.Equal(0, fixture.Diagnostics.Snapshot().OperatorRestarts);
    }

    private sealed class Fixture
    {
        private Fixture(SystemRole role)
        {
            Actor = new User
            {
                DisplayName = "Digest operator",
                Email = "operator@example.com",
                NormalizedEmail = "OPERATOR@EXAMPLE.COM",
                SystemRole = role,
                Status = UserStatus.Active
            };
            AdminRepository.Actor = Actor;
            Tenant.SetTenant(Guid.NewGuid(), "digest-tenant");
            Service = new AdminService(
                AdminRepository,
                new FakeTokenHasher(),
                new FakeAuditLogger(),
                new FakeCurrentUser(Actor),
                Clock,
                new FakeUserSessionService(),
                UnitOfWork,
                Tenant,
                authorizationChanges: null,
                Digests,
                Diagnostics);
        }

        public User Actor { get; }
        private FakeAdminRepository AdminRepository { get; } = new();
        public FakeDigestRepository Digests { get; } = new();
        public TaskDeadlineDigestDiagnostics Diagnostics { get; } = new();
        private CurrentTenantService Tenant { get; } = new();
        public FakeClock Clock { get; } = new();
        public FakeUnitOfWork UnitOfWork { get; } = new();
        public AdminService Service { get; }

        public static Fixture Create(SystemRole role) => new(role);
    }

    private sealed class FakeAdminRepository : IAdminRepository
    {
        public User? Actor { get; set; }

        public Task<User?> GetUserAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Actor?.Id == userId ? Actor : null);

        public Task<PagedResponse<AdminUserListItemResponse>> ListUsersAsync(int page, int pageSize, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<User?> GetUserByNormalizedEmailAsync(string normalizedEmail, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> CountSystemAdminsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> CountSystemAdminsExcludingAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Guid>> ListActiveSystemAdminIdsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PagedResponse<AdminInviteResponse>> ListInvitesAsync(int page, int pageSize, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task AddInviteAsync(Invite invite, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Invite?> GetInviteAsync(Guid inviteId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Workspace?> GetWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Group?> GetGroupAsync(Guid groupId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Project?> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Channel?> GetChannelAsync(Guid channelId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SystemSetting>> ListSettingsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SystemSetting?> GetSettingAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task AddSettingAsync(SystemSetting setting, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AdminDashboardSnapshot> GetDashboardSnapshotAsync(int recentCount, DateOnly today, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeDigestRepository : ITaskDeadlineDigestRepository
    {
        public TaskDeadlineDigestRestartOutcome RestartOutcome { get; set; } = TaskDeadlineDigestRestartOutcome.Restarted;
        public int RestartCalls { get; private set; }
        public Guid? LastJobId { get; private set; }
        public Guid? LastActorUserId { get; private set; }
        public string? LastReason { get; private set; }
        public DateTimeOffset? LastRequestedAt { get; private set; }
        public CancellationToken LastCancellationToken { get; private set; }

        public Task<TaskDeadlineDigestRestartOutcome> RestartFailedAsync(
            Guid jobId,
            Guid actorUserId,
            string reason,
            DateTimeOffset requestedAt,
            CancellationToken cancellationToken = default)
        {
            RestartCalls++;
            LastJobId = jobId;
            LastActorUserId = actorUserId;
            LastReason = reason;
            LastRequestedAt = requestedAt;
            LastCancellationToken = cancellationToken;
            return Task.FromResult(RestartOutcome);
        }

        public Task<IReadOnlyList<Guid>> ListActiveTenantIdsAsync(int page, int pageSize, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string?> GetTenantTimeZoneIdAsync(Guid tenantId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TaskDeadlineDigestScheduleCandidate>> ListScheduleCandidatesAsync(int page, int pageSize, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> UpsertSchedulesAsync(IReadOnlyCollection<TaskDeadlineDigestScheduleWrite> schedules, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TaskDeadlineDigestClaim>> ClaimDueAsync(string claimOwner, DateTimeOffset now, int batchSize, TimeSpan claimTimeout, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TaskDeadlineDigestClaim?> GetClaimedAsync(Guid jobId, Guid claimToken, bool forUpdate, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TaskDeadlineDigestClaim?> AcquireGenerationClaimFenceAsync(TaskDeadlineDigestClaim claim, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TaskDeadlineDigestCurrentContext?> GetCurrentContextAsync(Guid jobId, Guid claimToken, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TaskDeadlineDigestCandidate>> ListCurrentCandidatesAsync(Guid jobId, Guid claimToken, DateTimeOffset deadlineBeforeUtc, int page, int pageSize, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TaskDeadlineDigestGenerationFenceOutcome> AcquireGenerationFenceAsync(TaskDeadlineDigestClaim claim, TaskDeadlineDigestCurrentContext? evaluatedContext, IReadOnlyCollection<TaskDeadlineDigestCandidate> evaluatedCandidates, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ITaskDeadlineDigestTransaction> BeginGenerationTransactionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> MarkSucceededAsync(Guid jobId, Guid claimToken, Guid? notificationId, DateTimeOffset completedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeferAsync(Guid jobId, Guid claimToken, DateTimeOffset scheduledForUtc, DateTimeOffset deferredAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> ReleaseFeatureDisabledClaimAsync(Guid jobId, Guid claimToken, DateTimeOffset releasedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TaskDeadlineDigestTransition> MarkFailureAsync(Guid jobId, Guid claimToken, string errorCode, DateTimeOffset failedAt, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TaskDeadlineDigestStoreDiagnostics> GetDiagnosticsAsync(DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void ResetGenerationState()
        {
        }
    }

    private sealed class FakeTokenHasher : ITokenHasher
    {
        public string HashToken(string token) => token;
    }

    private sealed class FakeAuditLogger : IAuditLogger
    {
        public Task LogAsync(AuditLogEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeUserSessionService : IUserSessionService
    {
        public Task<SessionValidationResult> ValidateSessionAsync(Guid userId, Guid sessionId, Guid? tenantId, bool requireActiveTenantMembership, CancellationToken cancellationToken = default) =>
            Task.FromResult(SessionValidationResult.Success());

        public Task<Result> RevokeSessionAsync(Guid sessionId, Guid? actorUserId, string reason, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

        public Task<Result<int>> RevokeUserSessionsAsync(Guid userId, Guid? actorUserId, string reason, Guid? exceptSessionId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<int>.Success(0));
    }

    private sealed class FakeCurrentUser(User actor) : ICurrentUser
    {
        public Guid? UserId => actor.Id;
        public Guid? SessionId => Guid.NewGuid();
        public string? Email => actor.Email;
        public SystemRole? SystemRole => actor.SystemRole;
        public bool IsAuthenticated => true;
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 8, 4, 3, 15, 0, TimeSpan.Zero);
    }

    private sealed class FakeUnitOfWork : IUnitOfWork
    {
        public int SaveCalls { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            return Task.FromResult(1);
        }
    }
}
