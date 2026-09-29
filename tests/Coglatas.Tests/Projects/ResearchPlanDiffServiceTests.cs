using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Projects;
using Coglatas.Application.Realtime;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Tests.Projects;

public sealed class ResearchPlanDiffServiceTests
{
    [Fact]
    [Trait("Scope", "Issue366")]
    public async Task PreviewDistinguishesAddedRemovedModifiedAndReorderedStepsAndReportsBoundedImpacts()
    {
        await using var fixture = await Fixture.CreateAsync();
        var created = await fixture.Service.ReplaceAsync(
            fixture.TaskItem.Id,
            new ReplaceResearchPlanRequest(0,
            [
                new ResearchPlanStepRequest("A", "Collect baseline evidence.", "Project files", ResearchPlanStepStatus.Ready),
                new ResearchPlanStepRequest("B", "Review the baseline.", "Project files", ResearchPlanStepStatus.Planned),
                new ResearchPlanStepRequest("C", "Write the review.", "Task scope", ResearchPlanStepStatus.Planned)
            ]));
        Assert.True(created.IsSuccess, created.Error);

        var current = created.Value!.CurrentRevision!.Steps;
        var proposed = new ResearchPlanStepRequest[]
        {
            new("C", "Write the review.", "Task scope", ResearchPlanStepStatus.Planned, current[2].Id),
            new("B", "Review and reconcile the baseline.", "Approved web and project files", ResearchPlanStepStatus.Ready, current[1].Id),
            new("D", "Publish the reviewed result.", "Task scope", ResearchPlanStepStatus.Ready)
        };

        var preview = await fixture.Service.PreviewAsync(
            fixture.TaskItem.Id,
            new PreviewResearchPlanRequest(created.Value.Version, proposed));

        Assert.True(preview.IsSuccess, preview.Error);
        Assert.Equal(created.Value.Version, preview.Value!.BaseVersion);
        Assert.Equal(created.Value.CurrentRevision.Id, preview.Value.BaseRevisionId);
        Assert.Equal(4, preview.Value.Changes.Count);
        Assert.Contains(preview.Value.Changes, change => change.Kinds.SequenceEqual(["Removed"]) && change.Before?.Title == "A");
        Assert.Contains(preview.Value.Changes, change => change.Kinds.Contains("Added") && change.After?.Title == "D");
        Assert.Contains(preview.Value.Changes, change =>
            change.Before?.Title == "B" &&
            change.Kinds.Contains("Modified") &&
            change.Kinds.Contains("Reordered") &&
            change.ChangedFields.Contains("objective") &&
            change.ChangedFields.Contains("scopeSummary") &&
            change.ChangedFields.Contains("status"));
        Assert.Contains(preview.Value.Changes, change => change.Before?.Title == "C" && change.Kinds.Contains("Reordered"));
        Assert.True(preview.Value.Impact.ExecutionOrderChanged);
        Assert.True(preview.Value.Impact.SourceScopeGuidanceChanged);
        Assert.True(preview.Value.Impact.DeliverableAlignmentReviewRequired);
        Assert.False(preview.Value.Impact.ExecutionStepCountChanged);
        Assert.Contains(preview.Value.Impact.Items, item => item.Kind == "SourceScopeGuidanceChanged");
        Assert.Contains(preview.Value.Impact.Items, item => item.Kind == "DeliverableAlignmentReviewRequired");
        Assert.Equal(64, preview.Value.Fingerprint.Length);

        var saved = await fixture.Service.ReplaceAsync(
            fixture.TaskItem.Id,
            new ReplaceResearchPlanRequest(created.Value.Version, proposed, preview.Value.Fingerprint));

        Assert.True(saved.IsSuccess, saved.Error);
        Assert.Equal(["C", "B", "D"], saved.Value!.CurrentRevision!.Steps.Select(step => step.Title));
        Assert.Equal(2, await fixture.Db.ResearchPlanRevisions.CountAsync());
        var audited = Assert.Single(fixture.Audit.Entries, entry =>
            entry.Action == "ResearchPlanRevisionSaved" &&
            entry.Metadata is not null &&
            entry.Metadata.TryGetValue("reviewedDiff", out var reviewed) &&
            Equals(reviewed, true));
        Assert.Equal(4, audited.Metadata!["changeCount"]);
    }

    [Fact]
    [Trait("Scope", "Issue366")]
    public async Task ReviewedFingerprintRejectsAChangedDraftWithoutAppendingARevision()
    {
        await using var fixture = await Fixture.CreateAsync();
        var created = await fixture.Service.ReplaceAsync(
            fixture.TaskItem.Id,
            new ReplaceResearchPlanRequest(0,
            [new ResearchPlanStepRequest("Initial", "Objective", "Project files", ResearchPlanStepStatus.Planned)]));
        Assert.True(created.IsSuccess, created.Error);

        var currentStep = Assert.Single(created.Value!.CurrentRevision!.Steps);
        var reviewedDraft = new ResearchPlanStepRequest[]
        {
            new("Initial", "Updated objective", "Project files", ResearchPlanStepStatus.Ready, currentStep.Id)
        };
        var preview = await fixture.Service.PreviewAsync(
            fixture.TaskItem.Id,
            new PreviewResearchPlanRequest(created.Value.Version, reviewedDraft));
        Assert.True(preview.IsSuccess, preview.Error);

        var changedAfterReview = new ResearchPlanStepRequest[]
        {
            new("Changed after review", "Updated objective", "Project files", ResearchPlanStepStatus.Ready, currentStep.Id)
        };
        var rejected = await fixture.Service.ReplaceAsync(
            fixture.TaskItem.Id,
            new ReplaceResearchPlanRequest(created.Value.Version, changedAfterReview, preview.Value!.Fingerprint));

        Assert.False(rejected.IsSuccess);
        Assert.Equal("RESEARCH_PLAN_PREVIEW_MISMATCH", rejected.ErrorDetail!.Code);
        Assert.Single(await fixture.Db.ResearchPlanRevisions.ToListAsync());
        Assert.Single(fixture.Audit.Entries, entry => entry.Action == "ResearchPlanRevisionSaved");
    }

    [Fact]
    [Trait("Scope", "Issue366")]
    public async Task PreviewRejectsStepIdentityOutsideTheCurrentRevision()
    {
        await using var fixture = await Fixture.CreateAsync();
        var created = await fixture.Service.ReplaceAsync(
            fixture.TaskItem.Id,
            new ReplaceResearchPlanRequest(0,
            [new ResearchPlanStepRequest("Initial", "Objective", "Project files", ResearchPlanStepStatus.Planned)]));
        Assert.True(created.IsSuccess, created.Error);

        var preview = await fixture.Service.PreviewAsync(
            fixture.TaskItem.Id,
            new PreviewResearchPlanRequest(
                created.Value!.Version,
                [new ResearchPlanStepRequest("Injected", "Objective", "Scope", ResearchPlanStepStatus.Ready, Guid.NewGuid())]));

        Assert.False(preview.IsSuccess);
        Assert.Equal("RESEARCH_PLAN_VALIDATION_FAILED", preview.ErrorDetail!.Code);
        Assert.Contains("base step", preview.ErrorDetail.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            AppDbContext db,
            CurrentTenantService currentTenant,
            User actor,
            TaskItem taskItem,
            RecordingAuditLogger audit)
        {
            Db = db;
            TaskItem = taskItem;
            Audit = audit;
            var clock = new FixedClock();
            var outbox = new TransactionalOutbox(new OutboxEventRepository(db), currentTenant, clock);
            Service = new ResearchPlanService(
                new ProjectRepository(db),
                new AllowProjectAuthorization(),
                new ResearchPlanRepository(db),
                new TestCurrentUser(actor.Id),
                clock,
                audit,
                new BusinessInvalidationPublisher(outbox, currentTenant, clock),
                new EfUnitOfWork(db));
        }

        public AppDbContext Db { get; }
        public TaskItem TaskItem { get; }
        public RecordingAuditLogger Audit { get; }
        public ResearchPlanService Service { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var currentTenant = new CurrentTenantService();
            currentTenant.SetPlatformScope();
            var db = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseInMemoryDatabase($"issue366-{Guid.NewGuid():N}")
                    .Options,
                currentTenant);
            var tenant = new Tenant
            {
                Name = "Issue 366 tenant",
                DisplayName = "Issue 366 tenant",
                Slug = $"issue366-{Guid.NewGuid():N}"
            };
            var actor = new User
            {
                DisplayName = "Issue 366 manager",
                Email = $"issue366-{Guid.NewGuid():N}@example.test",
                NormalizedEmail = $"ISSUE366-{Guid.NewGuid():N}@EXAMPLE.TEST",
                PasswordHash = "not-used-by-test"
            };
            db.Tenants.Add(tenant);
            db.Users.Add(actor);
            await db.SaveChangesAsync();

            currentTenant.SetTenant(tenant.Id, tenant.Slug);
            var workspace = new Workspace
            {
                Name = "Issue 366 workspace",
                Slug = $"issue366-{Guid.NewGuid():N}",
                CreatedByUserId = actor.Id
            };
            db.Workspaces.Add(workspace);
            await db.SaveChangesAsync();

            var project = new Project
            {
                WorkspaceId = workspace.Id,
                OwnerUserId = actor.Id,
                CreatedByUserId = actor.Id,
                Name = "Issue 366 project",
                Slug = $"issue366-{Guid.NewGuid():N}",
                Status = ProjectStatus.Active
            };
            db.Projects.Add(project);
            await db.SaveChangesAsync();

            var taskItem = new TaskItem
            {
                WorkspaceId = workspace.Id,
                ProjectId = project.Id,
                CreatedByUserId = actor.Id,
                Title = "Research plan diff task"
            };
            db.TaskItems.Add(taskItem);
            await db.SaveChangesAsync();

            return new Fixture(db, currentTenant, actor, taskItem, new RecordingAuditLogger());
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class AllowProjectAuthorization : IProjectAuthorizationService
    {
        public Task<bool> CanViewProject(Guid userId, Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> CanManageProject(Guid userId, Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> CanCreateProject(Guid userId, Guid workspaceId, Guid groupId, CancellationToken cancellationToken = default) => Task.FromResult(false);
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
        public DateTimeOffset UtcNow => new(2026, 9, 1, 14, 40, 0, TimeSpan.Zero);
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
