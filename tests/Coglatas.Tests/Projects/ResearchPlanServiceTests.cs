using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Projects;
using Coglatas.Application.Realtime;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Tests.Projects;

public sealed class ResearchPlanServiceTests
{
    [Fact]
    [Trait("Scope", "Issue364")]
    public async Task SavingAndReorderingPlanStepsCreatesImmutableOrderedRevisions()
    {
        await using var fixture = await Fixture.CreateAsync();

        var first = await fixture.Service.ReplaceAsync(
            fixture.TaskItem.Id,
            new ReplaceResearchPlanRequest(0,
            [
                new ResearchPlanStepRequest("Collect evidence", "Gather approved source material.", "Project Files", ResearchPlanStepStatus.Ready),
                new ResearchPlanStepRequest("Review findings", "Review the evidence.", "Task scope", ResearchPlanStepStatus.Planned)
            ]));

        Assert.True(first.IsSuccess, first.Error);
        Assert.Equal(1, first.Value!.Version);
        Assert.Equal(1, first.Value.CurrentRevision!.Number);
        Assert.Equal(["Collect evidence", "Review findings"], first.Value.CurrentRevision.Steps.Select(step => step.Title));

        var second = await fixture.Service.ReplaceAsync(
            fixture.TaskItem.Id,
            new ReplaceResearchPlanRequest(first.Value.Version,
            [
                new ResearchPlanStepRequest("Review findings", "Review the evidence.", "Task scope", ResearchPlanStepStatus.Ready),
                new ResearchPlanStepRequest("Collect evidence", "Gather approved source material.", "Project Files", ResearchPlanStepStatus.Planned)
            ]));

        Assert.True(second.IsSuccess, second.Error);
        Assert.Equal(2, second.Value!.Version);
        Assert.Equal(2, second.Value.CurrentRevision!.Number);
        Assert.Equal(["Review findings", "Collect evidence"], second.Value.CurrentRevision.Steps.Select(step => step.Title));

        var revisions = await fixture.Db.ResearchPlanRevisions
            .AsNoTracking()
            .OrderBy(revision => revision.RevisionNo)
            .ToListAsync();
        Assert.Equal(2, revisions.Count);
        var oldSteps = await fixture.Db.ResearchPlanSteps
            .AsNoTracking()
            .Where(step => step.ResearchPlanRevisionId == revisions[0].Id)
            .OrderBy(step => step.SortOrder)
            .Select(step => step.Title)
            .ToListAsync();
        Assert.Equal(["Collect evidence", "Review findings"], oldSteps);

        var current = await fixture.Service.GetAsync(fixture.TaskItem.Id);
        Assert.True(current.IsSuccess, current.Error);
        Assert.Equal(second.Value.CurrentRevision.Id, current.Value!.CurrentRevision!.Id);
        Assert.Equal(["Review findings", "Collect evidence"], current.Value.CurrentRevision.Steps.Select(step => step.Title));
        Assert.Equal(2, fixture.Audit.Entries.Count(entry => entry.Action == "ResearchPlanRevisionSaved"));
    }

    [Fact]
    [Trait("Scope", "Issue364")]
    public async Task ViewerCanReadButCannotMutateTheTaskBoundPlan()
    {
        await using var fixture = await Fixture.CreateAsync(canManage: false);

        var readable = await fixture.Service.GetAsync(fixture.TaskItem.Id);
        var replace = await fixture.Service.ReplaceAsync(
            fixture.TaskItem.Id,
            new ReplaceResearchPlanRequest(0,
            [new ResearchPlanStepRequest("Unauthorized", "", "", ResearchPlanStepStatus.Planned)]));

        Assert.True(readable.IsSuccess, readable.Error);
        Assert.False(readable.Value!.CanManage);
        Assert.False(replace.IsSuccess);
        Assert.Equal("RESEARCH_PLAN_NOT_FOUND", replace.ErrorDetail!.Code);
        Assert.Empty(await fixture.Db.ResearchPlans.ToListAsync());
        Assert.Empty(fixture.Audit.Entries);
    }

    [Fact]
    [Trait("Scope", "Issue364")]
    public async Task StaleVersionAndDirectRevisionMutationFailClosed()
    {
        await using var fixture = await Fixture.CreateAsync();
        var created = await fixture.Service.ReplaceAsync(
            fixture.TaskItem.Id,
            new ReplaceResearchPlanRequest(0,
            [new ResearchPlanStepRequest("Initial", "Objective", "Scope", ResearchPlanStepStatus.Planned)]));
        Assert.True(created.IsSuccess, created.Error);

        var stale = await fixture.Service.ReplaceAsync(
            fixture.TaskItem.Id,
            new ReplaceResearchPlanRequest(0,
            [new ResearchPlanStepRequest("Stale", "", "", ResearchPlanStepStatus.Planned)]));
        Assert.False(stale.IsSuccess);
        Assert.Equal("RESEARCH_PLAN_STALE_VERSION", stale.ErrorDetail!.Code);

        var revision = await fixture.Db.ResearchPlanRevisions.SingleAsync();
        revision.CreatedAtUtc = revision.CreatedAtUtc.AddMinutes(1);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Db.SaveChangesAsync());
        Assert.Contains("immutable", exception.Message, StringComparison.OrdinalIgnoreCase);
        fixture.Db.ChangeTracker.Clear();
    }

    [Fact]
    [Trait("Scope", "Issue364")]
    public async Task TenantScopedRepositoriesRedactAnotherTenantsPlan()
    {
        await using var fixture = await Fixture.CreateAsync();
        var created = await fixture.Service.ReplaceAsync(
            fixture.TaskItem.Id,
            new ReplaceResearchPlanRequest(0,
            [new ResearchPlanStepRequest("Private", "Objective", "Scope", ResearchPlanStepStatus.Planned)]));
        Assert.True(created.IsSuccess, created.Error);

        await fixture.SwitchToOtherTenantAsync();
        var read = await fixture.Service.GetAsync(fixture.TaskItem.Id);
        var replace = await fixture.Service.ReplaceAsync(
            fixture.TaskItem.Id,
            new ReplaceResearchPlanRequest(0,
            [new ResearchPlanStepRequest("Injected", "", "", ResearchPlanStepStatus.Planned)]));

        Assert.False(read.IsSuccess);
        Assert.False(replace.IsSuccess);
        Assert.Equal("RESEARCH_PLAN_NOT_FOUND", read.ErrorDetail!.Code);
        Assert.Equal("RESEARCH_PLAN_NOT_FOUND", replace.ErrorDetail!.Code);
        Assert.Empty(await fixture.Db.ResearchPlans.ToListAsync());

        fixture.ReturnToPrimaryTenant();
        Assert.Single(await fixture.Db.ResearchPlans.ToListAsync());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            AppDbContext db,
            CurrentTenantService currentTenant,
            Tenant tenant,
            User actor,
            TaskItem taskItem,
            ControllableProjectAuthorization authorization,
            RecordingAuditLogger audit)
        {
            Db = db;
            CurrentTenant = currentTenant;
            Tenant = tenant;
            Actor = actor;
            TaskItem = taskItem;
            Authorization = authorization;
            Audit = audit;
            Service = CreateService();
        }

        public AppDbContext Db { get; }
        private CurrentTenantService CurrentTenant { get; }
        private Tenant Tenant { get; }
        private User Actor { get; }
        public TaskItem TaskItem { get; }
        private ControllableProjectAuthorization Authorization { get; }
        public RecordingAuditLogger Audit { get; }
        public ResearchPlanService Service { get; }

        private ResearchPlanService CreateService()
        {
            var clock = new FixedClock();
            var outbox = new TransactionalOutbox(new OutboxEventRepository(Db), CurrentTenant, clock);
            return new ResearchPlanService(
                new ProjectRepository(Db),
                Authorization,
                new ResearchPlanRepository(Db),
                new TestCurrentUser(Actor.Id),
                clock,
                Audit,
                new BusinessInvalidationPublisher(outbox, CurrentTenant, clock),
                new EfUnitOfWork(Db));
        }

        public static async Task<Fixture> CreateAsync(bool canManage = true)
        {
            var currentTenant = new CurrentTenantService();
            currentTenant.SetPlatformScope();
            var db = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseInMemoryDatabase($"issue364-{Guid.NewGuid():N}")
                    .Options,
                currentTenant);
            var tenant = new Tenant
            {
                Name = "Issue 364 tenant",
                DisplayName = "Issue 364 tenant",
                Slug = $"issue364-{Guid.NewGuid():N}"
            };
            var actor = new User
            {
                DisplayName = "Issue 364 manager",
                Email = $"issue364-{Guid.NewGuid():N}@example.test",
                NormalizedEmail = $"ISSUE364-{Guid.NewGuid():N}@EXAMPLE.TEST",
                PasswordHash = "not-used-by-test"
            };
            db.Tenants.Add(tenant);
            db.Users.Add(actor);
            await db.SaveChangesAsync();

            currentTenant.SetTenant(tenant.Id, tenant.Slug);
            var workspace = new Workspace
            {
                Name = "Issue 364 workspace",
                Slug = $"issue364-{Guid.NewGuid():N}",
                CreatedByUserId = actor.Id
            };
            db.Workspaces.Add(workspace);
            await db.SaveChangesAsync();

            var project = new Project
            {
                WorkspaceId = workspace.Id,
                OwnerUserId = actor.Id,
                CreatedByUserId = actor.Id,
                Name = "Issue 364 project",
                Slug = $"issue364-{Guid.NewGuid():N}",
                Status = ProjectStatus.Active
            };
            db.Projects.Add(project);
            await db.SaveChangesAsync();

            var taskItem = new TaskItem
            {
                WorkspaceId = workspace.Id,
                ProjectId = project.Id,
                CreatedByUserId = actor.Id,
                Title = "Research plan task"
            };
            db.TaskItems.Add(taskItem);
            await db.SaveChangesAsync();

            return new Fixture(
                db,
                currentTenant,
                tenant,
                actor,
                taskItem,
                new ControllableProjectAuthorization { CanManage = canManage },
                new RecordingAuditLogger());
        }

        public async Task SwitchToOtherTenantAsync()
        {
            CurrentTenant.SetPlatformScope();
            var tenant = new Tenant
            {
                Name = "Other issue 364 tenant",
                DisplayName = "Other issue 364 tenant",
                Slug = $"issue364-other-{Guid.NewGuid():N}"
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
        private bool CanView { get; } = true;
        public bool CanManage { get; init; } = true;

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
        public DateTimeOffset UtcNow => new(2026, 8, 30, 9, 0, 0, TimeSpan.Zero);
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
}
