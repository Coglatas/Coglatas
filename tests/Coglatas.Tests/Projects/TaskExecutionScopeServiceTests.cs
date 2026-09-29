using Coglatas.Application;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Projects;
using Coglatas.Application.Realtime;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Tests.Projects;

public sealed class TaskExecutionScopeServiceTests
{
    [Fact]
    [Trait("Scope", "Issue357")]
    [Trait("Scope", "Issue461")]
    public async Task ProjectDefaultAndTaskOverrideDetermineTheNextRunWithoutMutatingItsRecordedPolicy()
    {
        await using var fixture = await Fixture.CreateAsync();

        var initial = await fixture.Service.GetTaskScopeAsync(fixture.TaskItem.Id);

        Assert.True(initial.IsSuccess, initial.Error);
        Assert.Equal(TaskExecutionScopeOrigin.ProjectDefault, initial.Value!.Origin);
        Assert.False(initial.Value.EffectivePolicy.WebEnabled);
        Assert.False(initial.Value.EffectivePolicy.ProjectFilesEnabled);
        Assert.Equal(1, initial.Value.ProjectDefaultVersion);
        Assert.Null(initial.Value.TaskOverrideVersion);

        var projectDefault = await fixture.Service.UpdateProjectScopeAsync(
            fixture.Project.Id,
            new UpdateProjectExecutionScopeRequest(true, false, ExpectedVersion: 1));

        Assert.True(projectDefault.IsSuccess, projectDefault.Error);
        Assert.Equal(2, projectDefault.Value!.Version);
        Assert.True(projectDefault.Value.Policy.WebEnabled);
        Assert.False(projectDefault.Value.Policy.ProjectFilesEnabled);

        var taskOverride = await fixture.Service.UpdateTaskOverrideAsync(
            fixture.TaskItem.Id,
            new UpdateTaskExecutionScopeOverrideRequest(false, true, ExpectedVersion: 0));

        Assert.True(taskOverride.IsSuccess, taskOverride.Error);
        Assert.Equal(TaskExecutionScopeOrigin.TaskOverride, taskOverride.Value!.Origin);
        Assert.False(taskOverride.Value.EffectivePolicy.WebEnabled);
        Assert.True(taskOverride.Value.EffectivePolicy.ProjectFilesEnabled);
        Assert.Equal(2, taskOverride.Value.ProjectDefaultVersion);
        Assert.Equal(1, taskOverride.Value.TaskOverrideVersion);

        var requested = await fixture.Service.RequestRunAsync(fixture.TaskItem.Id, "scope-run-0001");

        Assert.True(requested.IsSuccess, requested.Error);
        var recordedRun = requested.Value!;
        Assert.Equal(TaskExecutionRunStatus.Accepted, recordedRun.Status);
        Assert.Equal(TaskExecutionMajorState.Accepted, recordedRun.MajorState);
        Assert.Null(recordedRun.FailureCode);
        Assert.Equal(TaskExecutionProvider.FirstPartyProjectFilesRuntimeV1, recordedRun.RuntimeProvider);
        Assert.Equal(TaskExecutionRun.RuntimeContractVersion1, recordedRun.RuntimeContractVersion);
        Assert.Equal(TaskExecutionRun.CurrentSnapshotSchemaVersion, recordedRun.SnapshotSchemaVersion);
        Assert.Null(recordedRun.QueuedAtUtc);
        Assert.Null(recordedRun.StartedAtUtc);
        Assert.Null(recordedRun.FinishedAtUtc);
        Assert.Equal(TaskExecutionScopeOrigin.TaskOverride, recordedRun.SnapshotScopeOrigin);
        Assert.Equal(2, recordedRun.SnapshotProjectScopeVersion);
        Assert.Equal(1, recordedRun.SnapshotTaskOverrideVersion);
        Assert.False(recordedRun.SnapshotWebEnabled);
        Assert.True(recordedRun.SnapshotProjectFilesEnabled);
        Assert.Null(recordedRun.SnapshotResearchPlanRevisionId);
        Assert.Null(recordedRun.SnapshotResearchPlanRevisionNo);

        var changedOverride = await fixture.Service.UpdateTaskOverrideAsync(
            fixture.TaskItem.Id,
            new UpdateTaskExecutionScopeOverrideRequest(true, false, ExpectedVersion: 1));

        Assert.True(changedOverride.IsSuccess, changedOverride.Error);
        Assert.True(changedOverride.Value!.EffectivePolicy.WebEnabled);
        Assert.False(changedOverride.Value.EffectivePolicy.ProjectFilesEnabled);
        Assert.Equal(2, changedOverride.Value.TaskOverrideVersion);

        var persisted = await fixture.Db.TaskExecutionRuns
            .AsNoTracking()
            .SingleAsync(run => run.Id == recordedRun.Id);

        Assert.Equal(TaskExecutionScopeOrigin.TaskOverride, persisted.SnapshotScopeOrigin);
        Assert.Equal(2, persisted.SnapshotProjectScopeVersion);
        Assert.Equal(1, persisted.SnapshotTaskOverrideVersion);
        Assert.False(persisted.SnapshotWebEnabled);
        Assert.True(persisted.SnapshotProjectFilesEnabled);
        Assert.Null(persisted.SnapshotResearchPlanRevisionId);
        Assert.Null(persisted.SnapshotResearchPlanRevisionNo);

        var staleProjectDefault = await fixture.Service.UpdateProjectScopeAsync(
            fixture.Project.Id,
            new UpdateProjectExecutionScopeRequest(false, true, ExpectedVersion: 1));

        Assert.False(staleProjectDefault.IsSuccess);
        Assert.Equal("TASK_EXECUTION_STALE_VERSION", staleProjectDefault.ErrorDetail!.Code);
        Assert.Equal(2, (await fixture.Db.ProjectExecutionScopes.SingleAsync()).VersionNo);
    }

    [Fact]
    [Trait("Scope", "Issue357")]
    public async Task ScopeChangesAndRunRequestsFailClosedForAViewerWhoCannotManageTheProject()
    {
        await using var fixture = await Fixture.CreateAsync(canManage: false);

        var readable = await fixture.Service.GetTaskScopeAsync(fixture.TaskItem.Id);
        var projectUpdate = await fixture.Service.UpdateProjectScopeAsync(
            fixture.Project.Id,
            new UpdateProjectExecutionScopeRequest(true, true, ExpectedVersion: 0));
        var taskUpdate = await fixture.Service.UpdateTaskOverrideAsync(
            fixture.TaskItem.Id,
            new UpdateTaskExecutionScopeOverrideRequest(true, true, ExpectedVersion: 0));
        var run = await fixture.Service.RequestRunAsync(fixture.TaskItem.Id, "viewer-run-0001");

        Assert.True(readable.IsSuccess, readable.Error);
        Assert.False(readable.Value!.CanManage);
        Assert.False(projectUpdate.IsSuccess);
        Assert.False(taskUpdate.IsSuccess);
        Assert.False(run.IsSuccess);
        Assert.Equal("TASK_EXECUTION_NOT_FOUND", projectUpdate.ErrorDetail!.Code);
        Assert.Equal("TASK_EXECUTION_NOT_FOUND", taskUpdate.ErrorDetail!.Code);
        Assert.Equal("TASK_EXECUTION_NOT_FOUND", run.ErrorDetail!.Code);
        var baselineScope = Assert.Single(await fixture.Db.ProjectExecutionScopes.ToListAsync());
        Assert.False(baselineScope.WebEnabled);
        Assert.False(baselineScope.ProjectFilesEnabled);
        Assert.Equal(1, baselineScope.VersionNo);
        Assert.Empty(await fixture.Db.TaskExecutionScopeOverrides.ToListAsync());
        Assert.Empty(await fixture.Db.TaskExecutionRuns.ToListAsync());
        Assert.Empty(await fixture.Db.IdempotencyRecords.ToListAsync());
        Assert.Empty(await fixture.Db.OutboxEvents.ToListAsync());
        Assert.Empty(fixture.Audit.Entries);
    }

    [Fact]
    [Trait("Scope", "Issue357")]
    [Trait("Scope", "Issue461")]
    public async Task IdempotencyReplaysTheOriginalImmutableSnapshotWhenTheEffectivePolicyChanges()
    {
        await using var fixture = await Fixture.CreateAsync();

        var initialScope = await fixture.Service.UpdateProjectScopeAsync(
            fixture.Project.Id,
            new UpdateProjectExecutionScopeRequest(true, false, ExpectedVersion: 1));
        Assert.True(initialScope.IsSuccess, initialScope.Error);

        var created = await fixture.Service.RequestRunAsync(fixture.TaskItem.Id, "replay-run-0001");
        var replayed = await fixture.Service.RequestRunAsync(fixture.TaskItem.Id, "replay-run-0001");

        Assert.True(created.IsSuccess, created.Error);
        Assert.True(replayed.IsSuccess, replayed.Error);
        Assert.Equal(created.Value!.Id, replayed.Value!.Id);
        Assert.Equal(TaskExecutionRunStatus.Accepted, created.Value.Status);
        Assert.Equal(TaskExecutionProvider.FirstPartyProjectFilesRuntimeV1, created.Value.RuntimeProvider);
        Assert.Equal(TaskExecutionRun.RuntimeContractVersion1, created.Value.RuntimeContractVersion);
        Assert.Equal(created.Value.RuntimeProvider, replayed.Value.RuntimeProvider);
        Assert.Equal(created.Value.RuntimeContractVersion, replayed.Value.RuntimeContractVersion);
        Assert.Single(await fixture.Db.TaskExecutionRuns.ToListAsync());
        Assert.Single(await fixture.Db.IdempotencyRecords.ToListAsync());

        var changedScope = await fixture.Service.UpdateProjectScopeAsync(
            fixture.Project.Id,
            new UpdateProjectExecutionScopeRequest(false, true, initialScope.Value!.Version));
        Assert.True(changedScope.IsSuccess, changedScope.Error);
        Assert.Equal(3, changedScope.Value!.Version);

        var replayedAfterScopeChange = await fixture.Service.RequestRunAsync(fixture.TaskItem.Id, "replay-run-0001");

        Assert.True(replayedAfterScopeChange.IsSuccess, replayedAfterScopeChange.Error);
        Assert.Equal(created.Value.Id, replayedAfterScopeChange.Value!.Id);
        Assert.Equal(TaskExecutionScopeOrigin.ProjectDefault, replayedAfterScopeChange.Value.SnapshotScopeOrigin);
        Assert.Equal(2, replayedAfterScopeChange.Value.SnapshotProjectScopeVersion);
        Assert.True(replayedAfterScopeChange.Value.SnapshotWebEnabled);
        Assert.False(replayedAfterScopeChange.Value.SnapshotProjectFilesEnabled);
        Assert.Single(await fixture.Db.TaskExecutionRuns.ToListAsync());

        var currentScope = await fixture.Service.GetTaskScopeAsync(fixture.TaskItem.Id);
        Assert.True(currentScope.IsSuccess, currentScope.Error);
        Assert.False(currentScope.Value!.EffectivePolicy.WebEnabled);
        Assert.True(currentScope.Value.EffectivePolicy.ProjectFilesEnabled);
    }

    [Fact]
    [Trait("Scope", "Issue357")]
    public async Task RunSnapshotIsReadInsideTheIdempotentCreationStage()
    {
        await using var fixture = await Fixture.CreateAsync();
        var scope = await fixture.Db.ProjectExecutionScopes.SingleAsync();
        Func<CancellationToken, Task<int>> saveChangesAsync = fixture.Db.SaveChangesAsync;
        var coordinator = new BeforeStageIdempotencyCoordinator(async cancellationToken =>
        {
            scope.WebEnabled = true;
            scope.ProjectFilesEnabled = true;
            scope.VersionNo = 2;
            await saveChangesAsync(cancellationToken);
        });
        var service = fixture.CreateService(new EfUnitOfWork(fixture.Db), coordinator);

        var requested = await service.RequestRunAsync(fixture.TaskItem.Id, "stage-snapshot-0001");

        Assert.True(requested.IsSuccess, requested.Error);
        Assert.Equal(TaskExecutionScopeOrigin.ProjectDefault, requested.Value!.SnapshotScopeOrigin);
        Assert.Equal(2, requested.Value.SnapshotProjectScopeVersion);
        Assert.True(requested.Value.SnapshotWebEnabled);
        Assert.True(requested.Value.SnapshotProjectFilesEnabled);
    }

    [Fact]
    [Trait("Scope", "Issue364")]
    [Trait("Scope", "Issue461")]
    public async Task AcceptedRunSnapshotsTheCurrentResearchPlanRevisionAndIdempotencyReplaysIt()
    {
        await using var fixture = await Fixture.CreateAsync();
        var firstRevision = await fixture.SaveResearchPlanRevisionAsync("Collect approved evidence");

        var accepted = await fixture.Service.RequestRunAsync(fixture.TaskItem.Id, "plan-snapshot-run-0001");

        Assert.True(accepted.IsSuccess, accepted.Error);
        Assert.Equal(TaskExecutionRun.CurrentSnapshotSchemaVersion, accepted.Value!.SnapshotSchemaVersion);
        Assert.Equal(firstRevision.Id, accepted.Value.SnapshotResearchPlanRevisionId);
        Assert.Equal(firstRevision.RevisionNo, accepted.Value.SnapshotResearchPlanRevisionNo);

        var secondRevision = await fixture.SaveResearchPlanRevisionAsync("Review approved evidence");
        Assert.NotEqual(firstRevision.Id, secondRevision.Id);

        var replayed = await fixture.Service.RequestRunAsync(fixture.TaskItem.Id, "plan-snapshot-run-0001");

        Assert.True(replayed.IsSuccess, replayed.Error);
        Assert.Equal(accepted.Value.Id, replayed.Value!.Id);
        Assert.Equal(firstRevision.Id, replayed.Value.SnapshotResearchPlanRevisionId);
        Assert.Equal(firstRevision.RevisionNo, replayed.Value.SnapshotResearchPlanRevisionNo);

        var persisted = await fixture.Db.TaskExecutionRuns
            .AsNoTracking()
            .SingleAsync(run => run.Id == accepted.Value.Id);
        Assert.Equal(firstRevision.Id, persisted.SnapshotResearchPlanRevisionId);
        Assert.Equal(firstRevision.RevisionNo, persisted.SnapshotResearchPlanRevisionNo);
        Assert.Single(await fixture.Db.TaskExecutionRuns.ToListAsync());
    }

    [Fact]
    [Trait("Scope", "Issue364")]
    public async Task DirectEfMutationOfTheAcceptedResearchPlanSnapshotFailsClosed()
    {
        await using var fixture = await Fixture.CreateAsync();
        var revision = await fixture.SaveResearchPlanRevisionAsync("Immutable run plan");
        var accepted = await fixture.Service.RequestRunAsync(fixture.TaskItem.Id, "plan-snapshot-immutable-0001");
        Assert.True(accepted.IsSuccess, accepted.Error);

        var run = await fixture.Db.TaskExecutionRuns.SingleAsync();
        run.SnapshotResearchPlanRevisionId = revision.Id;
        run.SnapshotResearchPlanRevisionNo = revision.RevisionNo + 1;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Db.SaveChangesAsync());

        Assert.Contains("snapshots are immutable", exception.Message, StringComparison.OrdinalIgnoreCase);
        fixture.Db.ChangeTracker.Clear();
    }

    [Fact]
    [Trait("Scope", "Issue357")]
    public async Task TenantScopedRepositoriesDoNotExposeOrMutateAnotherTenantsTaskExecutionState()
    {
        await using var fixture = await Fixture.CreateAsync();
        var created = await fixture.Service.UpdateProjectScopeAsync(
            fixture.Project.Id,
            new UpdateProjectExecutionScopeRequest(true, true, ExpectedVersion: 1));
        Assert.True(created.IsSuccess, created.Error);

        await fixture.SwitchToOtherTenantAsync();

        var read = await fixture.Service.GetTaskScopeAsync(fixture.TaskItem.Id);
        var update = await fixture.Service.UpdateTaskOverrideAsync(
            fixture.TaskItem.Id,
            new UpdateTaskExecutionScopeOverrideRequest(false, false, ExpectedVersion: 0));
        var run = await fixture.Service.RequestRunAsync(fixture.TaskItem.Id, "cross-tenant-run-0001");

        Assert.False(read.IsSuccess);
        Assert.False(update.IsSuccess);
        Assert.False(run.IsSuccess);
        Assert.Equal("TASK_EXECUTION_NOT_FOUND", read.ErrorDetail!.Code);
        Assert.Equal("TASK_EXECUTION_NOT_FOUND", update.ErrorDetail!.Code);
        Assert.Equal("TASK_EXECUTION_NOT_FOUND", run.ErrorDetail!.Code);
        Assert.Empty(await fixture.Db.ProjectExecutionScopes.ToListAsync());
        Assert.Empty(await fixture.Db.TaskExecutionScopeOverrides.ToListAsync());
        Assert.Empty(await fixture.Db.TaskExecutionRuns.ToListAsync());

        fixture.ReturnToPrimaryTenant();
        var stillOwned = await fixture.Db.ProjectExecutionScopes.SingleAsync();
        Assert.True(stillOwned.WebEnabled);
        Assert.True(stillOwned.ProjectFilesEnabled);
    }

    [Fact]
    [Trait("Scope", "Issue461")]
    public void FirstPartyRuntimeContractDisablesWebAndRequiresProjectFiles()
    {
        var eligible = FirstPartyProjectFilesRuntimeV1.EvaluateScope(webEnabled: false, projectFilesEnabled: true);
        var webRejected = FirstPartyProjectFilesRuntimeV1.EvaluateScope(webEnabled: true, projectFilesEnabled: true);
        var noFilesRejected = FirstPartyProjectFilesRuntimeV1.EvaluateScope(webEnabled: false, projectFilesEnabled: false);

        Assert.True(eligible.IsEligible);
        Assert.Null(eligible.FailureCode);
        Assert.False(webRejected.IsEligible);
        Assert.Equal("TASK_EXECUTION_WEB_UNSUPPORTED", webRejected.FailureCode);
        Assert.False(noFilesRejected.IsEligible);
        Assert.Equal("TASK_EXECUTION_PROJECT_FILES_REQUIRED", noFilesRejected.FailureCode);
    }

    [Fact]
    [Trait("Scope", "Issue357")]
    public async Task DirectProjectPersistenceCreatesOneExplicitFailClosedDefaultScope()
    {
        await using var fixture = await Fixture.CreateAsync();

        var scope = Assert.Single(await fixture.Db.ProjectExecutionScopes.ToListAsync());

        Assert.Equal(fixture.Project.Id, scope.ProjectId);
        Assert.Equal(fixture.Tenant.Id, scope.TenantId);
        Assert.Equal(fixture.Workspace.Id, scope.WorkspaceId);
        Assert.Equal(fixture.Actor.Id, scope.UpdatedByUserId);
        Assert.False(scope.WebEnabled);
        Assert.False(scope.ProjectFilesEnabled);
        Assert.Equal(1, scope.VersionNo);
    }

    [Fact]
    [Trait("Scope", "Issue357")]
    public async Task DirectEfDeletionOfTheProjectDefaultScopeFailsClosed()
    {
        await using var fixture = await Fixture.CreateAsync();

        var scope = await fixture.Db.ProjectExecutionScopes.SingleAsync();
        fixture.Db.ProjectExecutionScopes.Remove(scope);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Db.SaveChangesAsync());

        Assert.Contains("cannot be deleted", exception.Message, StringComparison.OrdinalIgnoreCase);
        fixture.Db.ChangeTracker.Clear();
    }

    [Fact]
    [Trait("Scope", "Issue357")]
    public async Task DirectEfDeletionOfAnExecutionRunFailsClosed()
    {
        await using var fixture = await Fixture.CreateAsync();
        var requested = await fixture.Service.RequestRunAsync(fixture.TaskItem.Id, "delete-run-0001");
        Assert.True(requested.IsSuccess, requested.Error);

        var run = await fixture.Db.TaskExecutionRuns.SingleAsync();
        fixture.Db.TaskExecutionRuns.Remove(run);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Db.SaveChangesAsync());

        Assert.Contains("append-only", exception.Message, StringComparison.OrdinalIgnoreCase);
        fixture.Db.ChangeTracker.Clear();
    }

    [Fact]
    [Trait("Scope", "Issue357")]
    public async Task FirstTaskOverrideUniqueConflictIsReturnedAsARefetchableStaleScope()
    {
        await using var fixture = await Fixture.CreateAsync();
        var unitOfWork = new UniqueConflictTaskUnitOfWork(fixture.Db);
        var service = fixture.CreateService(unitOfWork);

        var result = await service.UpdateTaskOverrideAsync(
            fixture.TaskItem.Id,
            new UpdateTaskExecutionScopeOverrideRequest(true, false, ExpectedVersion: 0));

        Assert.False(result.IsSuccess);
        Assert.Equal("TASK_EXECUTION_STALE_VERSION", result.ErrorDetail!.Code);
        Assert.True(unitOfWork.SaveAttempted);
        Assert.Empty(await fixture.Db.TaskExecutionScopeOverrides.ToListAsync());
        Assert.Empty(await fixture.Db.OutboxEvents.ToListAsync());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            AppDbContext db,
            CurrentTenantService currentTenant,
            Tenant tenant,
            Workspace workspace,
            User actor,
            Project project,
            TaskItem taskItem,
            ControllableProjectAuthorization authorization,
            RecordingAuditLogger audit)
        {
            Db = db;
            CurrentTenant = currentTenant;
            Tenant = tenant;
            Workspace = workspace;
            Actor = actor;
            Project = project;
            TaskItem = taskItem;
            Authorization = authorization;
            Audit = audit;

            Service = CreateService(new EfUnitOfWork(db));
        }

        public AppDbContext Db { get; }
        private CurrentTenantService CurrentTenant { get; }
        public Tenant Tenant { get; }
        public Workspace Workspace { get; }
        public User Actor { get; }
        public Project Project { get; }
        public TaskItem TaskItem { get; }
        private ControllableProjectAuthorization Authorization { get; }
        public RecordingAuditLogger Audit { get; }
        public TaskExecutionScopeService Service { get; }

        public TaskExecutionScopeService CreateService(
            ITaskCommandUnitOfWork unitOfWork,
            ICreateIdempotencyCoordinator? idempotency = null)
        {
            var clock = new FixedClock();
            var outbox = new TransactionalOutbox(new OutboxEventRepository(Db), CurrentTenant, clock);
            return new TaskExecutionScopeService(
                new ProjectRepository(Db),
                Authorization,
                new TaskExecutionScopeRepository(Db),
                new ResearchPlanRepository(Db),
                new TestCurrentUser(Actor.Id),
                clock,
                Audit,
                new BusinessInvalidationPublisher(outbox, CurrentTenant, clock),
                unitOfWork,
                idempotency ?? new EfCreateIdempotencyCoordinator(Db));
        }

        public static async Task<Fixture> CreateAsync(bool canManage = true)
        {
            var currentTenant = new CurrentTenantService();
            currentTenant.SetPlatformScope();
            var db = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseInMemoryDatabase($"issue357-{Guid.NewGuid():N}")
                    .Options,
                currentTenant);
            var tenant = new Tenant
            {
                Name = "Issue 357 tenant",
                DisplayName = "Issue 357 tenant",
                Slug = $"issue357-{Guid.NewGuid():N}"
            };
            var actor = new User
            {
                DisplayName = "Issue 357 manager",
                Email = $"issue357-{Guid.NewGuid():N}@example.test",
                NormalizedEmail = $"ISSUE357-{Guid.NewGuid():N}@EXAMPLE.TEST",
                PasswordHash = "not-used-by-test"
            };
            db.Tenants.Add(tenant);
            db.Users.Add(actor);
            await db.SaveChangesAsync();

            currentTenant.SetTenant(tenant.Id, tenant.Slug);
            var workspace = new Workspace
            {
                Name = "Issue 357 workspace",
                Slug = $"issue357-{Guid.NewGuid():N}",
                CreatedByUserId = actor.Id
            };
            db.Workspaces.Add(workspace);
            await db.SaveChangesAsync();

            var project = new Project
            {
                WorkspaceId = workspace.Id,
                OwnerUserId = actor.Id,
                CreatedByUserId = actor.Id,
                Name = "Issue 357 project",
                Slug = $"issue357-{Guid.NewGuid():N}",
                Status = ProjectStatus.Active
            };
            db.Projects.Add(project);
            await db.SaveChangesAsync();

            var taskItem = new TaskItem
            {
                WorkspaceId = workspace.Id,
                ProjectId = project.Id,
                CreatedByUserId = actor.Id,
                Title = "Execution scope task"
            };
            db.TaskItems.Add(taskItem);
            await db.SaveChangesAsync();

            return new Fixture(
                db,
                currentTenant,
                tenant,
                workspace,
                actor,
                project,
                taskItem,
                new ControllableProjectAuthorization { CanManage = canManage },
                new RecordingAuditLogger());
        }

        public async Task<ResearchPlanRevision> SaveResearchPlanRevisionAsync(string title)
        {
            var plan = await Db.ResearchPlans.SingleOrDefaultAsync(researchPlan => researchPlan.TaskItemId == TaskItem.Id);
            if (plan is null)
            {
                plan = new ResearchPlan
                {
                    TenantId = Tenant.Id,
                    WorkspaceId = Workspace.Id,
                    ProjectId = Project.Id,
                    TaskItemId = TaskItem.Id,
                    VersionNo = 1
                };
                Db.ResearchPlans.Add(plan);
            }

            var revisionNo = (await Db.ResearchPlanRevisions
                .Where(revision => revision.ResearchPlanId == plan.Id)
                .Select(revision => (long?)revision.RevisionNo)
                .MaxAsync()) is { } latest
                    ? latest + 1
                    : 1;
            var revision = new ResearchPlanRevision
            {
                TenantId = Tenant.Id,
                WorkspaceId = Workspace.Id,
                ProjectId = Project.Id,
                TaskItemId = TaskItem.Id,
                ResearchPlanId = plan.Id,
                RevisionNo = revisionNo,
                CreatedByUserId = Actor.Id,
                CreatedAtUtc = new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.Zero)
            };
            plan.CurrentRevisionId = revision.Id;
            plan.VersionNo = revisionNo;
            Db.ResearchPlanRevisions.Add(revision);
            Db.ResearchPlanSteps.Add(new ResearchPlanStep
            {
                TenantId = Tenant.Id,
                WorkspaceId = Workspace.Id,
                ProjectId = Project.Id,
                TaskItemId = TaskItem.Id,
                ResearchPlanId = plan.Id,
                ResearchPlanRevisionId = revision.Id,
                SortOrder = 1,
                Title = title,
                Objective = "Execution-start plan provenance test.",
                ScopeSummary = "Project Files",
                Status = ResearchPlanStepStatus.Ready
            });
            await Db.SaveChangesAsync();
            return revision;
        }

        public async Task SwitchToOtherTenantAsync()
        {
            CurrentTenant.SetPlatformScope();
            var tenant = new Tenant
            {
                Name = "Other issue 357 tenant",
                DisplayName = "Other issue 357 tenant",
                Slug = $"issue357-other-{Guid.NewGuid():N}"
            };
            Db.Tenants.Add(tenant);
            await Db.SaveChangesAsync();
            CurrentTenant.SetTenant(tenant.Id, tenant.Slug);
        }

        public void ReturnToPrimaryTenant() => CurrentTenant.SetTenant(Tenant.Id, Tenant.Slug);

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class ControllableProjectAuthorization : IProjectAuthorizationService
    {
        private bool CanView { get; set; } = true;
        public bool CanManage { get; set; } = true;

        public Task<bool> CanViewProject(Guid userId, Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(CanView);

        public Task<bool> CanManageProject(Guid userId, Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(CanView && CanManage);

        public Task<bool> CanCreateProject(Guid userId, Guid workspaceId, Guid groupId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class TestCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid? UserId => userId;
        public Guid? SessionId => null;
        public string? Email => null;
        public SystemRole? SystemRole => Coglatas.Domain.Enums.SystemRole.NormalUser;
        public bool IsAuthenticated => true;
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 8, 25, 8, 30, 0, TimeSpan.Zero);
    }

    private sealed class RecordingAuditLogger : IAuditLogger
    {
        public List<AuditLogEntry> Entries { get; } = [];

        public Task LogAsync(AuditLogEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class BeforeStageIdempotencyCoordinator(
        Func<CancellationToken, Task> beforeStage) : ICreateIdempotencyCoordinator
    {
        public async Task<IdempotentCreateResult<T>> ExecuteAsync<T>(
            CreateIdempotencyContext context,
            Func<CancellationToken, Task<T>> stageCreation,
            Func<Guid, CancellationToken, Task<T?>> loadCommittedResource,
            CancellationToken cancellationToken = default)
            where T : class
        {
            await beforeStage(cancellationToken);
            var created = await stageCreation(cancellationToken);
            return new IdempotentCreateResult<T>(IdempotentCreateDisposition.Created, created);
        }
    }

    private sealed class UniqueConflictTaskUnitOfWork(AppDbContext db) : ITaskCommandUnitOfWork
    {
        public bool SaveAttempted { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            db.SaveChangesAsync(cancellationToken);

        public Task<TaskCommandSaveOutcome> SaveTaskCommandAsync(CancellationToken cancellationToken = default)
        {
            SaveAttempted = true;
            db.ChangeTracker.Clear();
            return Task.FromResult(new TaskCommandSaveOutcome(
                TaskCommandSaveResult.UniqueConflict,
                "ux_task_execution_scope_overrides_TaskItemId"));
        }

        public void ClearTaskCommandTracking() => db.ChangeTracker.Clear();
    }
}
