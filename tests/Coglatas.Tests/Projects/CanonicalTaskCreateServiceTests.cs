using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Groups;
using Coglatas.Application.Projects;
using Coglatas.Application.Realtime;
using Coglatas.Application.Workspaces;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Tests.Projects;

public sealed class CanonicalTaskCreateServiceTests
{
    [Fact]
    [Trait("Scope", "Issue410")]
    public async Task ManagerCreateAtomicallyStagesTaskInitialAssigneeAndCompleteScopeOverride()
    {
        await using var fixture = await Fixture.CreateAsync(ProjectRole.Manager);
        var request = fixture.ValidRequest(
            primaryAssigneeUserId: fixture.Assignee.Id,
            sourceScopeMode: TaskCreateSourceScopeMode.TaskOverride,
            taskOverridePolicy: new TaskCreateSourceScopePolicyRequest(true, false));

        var result = await fixture.Service.CreateAsync(fixture.Project.Id, request, "task-create-atomic-001");

        Assert.True(result.IsSuccess, result.Error);
        var response = result.Value!;
        Assert.Equal(fixture.Project.Id, response.ProjectId);
        Assert.Equal(fixture.Milestone.Id, response.MilestoneId);
        Assert.Equal(fixture.Assignee.Id, response.PrimaryAssigneeUserId);
        Assert.Equal(TaskCreateSourceScopeMode.TaskOverride, response.SourceScopeMode);
        Assert.Equal(new TaskExecutionSourcePolicyResponse(true, false), response.TaskOverridePolicy);
        Assert.NotEqual(Guid.Empty, response.WorkflowStageId);
        Assert.Equal(1, response.Version);

        fixture.Db.ChangeTracker.Clear();
        var task = await fixture.Db.TaskItems.SingleAsync(item => item.Id == response.TaskId);
        var scopeOverride = await fixture.Db.TaskExecutionScopeOverrides.SingleAsync(item => item.TaskItemId == task.Id);
        var watches = await fixture.Db.WorkItemWatchStates
            .Where(item => item.TaskItemId == task.Id)
            .OrderBy(item => item.UserId)
            .ToListAsync();

        Assert.Equal(fixture.Project.WorkspaceId, task.WorkspaceId);
        Assert.Equal(fixture.Tenant.Id, task.TenantId);
        Assert.Equal(TaskItemStatus.NotStarted, task.Status);
        Assert.Equal(fixture.InitialStage.Id, task.WorkflowStageId);
        Assert.True(scopeOverride.WebEnabled);
        Assert.False(scopeOverride.ProjectFilesEnabled);
        Assert.Equal(1, scopeOverride.VersionNo);
        Assert.Equal(2, watches.Count);
        Assert.Contains(watches, watch =>
            watch.UserId == fixture.Actor.Id &&
            watch.AutomaticSources.HasFlag(WorkItemWatchAutomaticSource.Creator));
        Assert.Contains(watches, watch =>
            watch.UserId == fixture.Assignee.Id &&
            watch.AutomaticSources == WorkItemWatchAutomaticSource.PrimaryAssignee);
        Assert.Contains(fixture.Audit.Entries, entry => entry.Action == "TaskCreated" && entry.EntityId == task.Id);
        Assert.Contains(fixture.Audit.Entries, entry => entry.Action == "TaskExecutionScopeOverrideSet" && entry.EntityId == scopeOverride.Id);
        Assert.Single(fixture.Notifications.Requests);
        Assert.Equal(TaskNotificationEventKind.PrimaryAssigneeChanged, fixture.Notifications.Requests[0].EventKind);
        Assert.Single(await fixture.Db.IdempotencyRecords.ToListAsync());
        var taskEvents = await fixture.Db.OutboxEvents.Where(item => item.AggregateId == task.Id).ToListAsync();
        Assert.Contains(taskEvents, item => item.EventType == "Projects.TaskChanged.v1");
        Assert.Contains(taskEvents, item => item.EventType == "Projects.TaskAssignmentChanged.v1");
        Assert.Empty(await fixture.Db.TaskExecutionRuns.ToListAsync());
    }

    [Fact]
    [Trait("Scope", "Issue410")]
    public async Task ContributorCanCreateOnlyAnUnassignedInheritingTask()
    {
        await using var fixture = await Fixture.CreateAsync(ProjectRole.Contributor);

        var options = await fixture.Service.GetCreateOptionsAsync(fixture.Project.Id);
        var inherited = await fixture.Service.CreateAsync(
            fixture.Project.Id,
            fixture.ValidRequest(),
            "task-create-contributor-001");
        var assigneeDenied = await fixture.Service.CreateAsync(
            fixture.Project.Id,
            fixture.ValidRequest(primaryAssigneeUserId: fixture.Assignee.Id),
            "task-create-contributor-002");
        var overrideDenied = await fixture.Service.CreateAsync(
            fixture.Project.Id,
            fixture.ValidRequest(
                sourceScopeMode: TaskCreateSourceScopeMode.TaskOverride,
                taskOverridePolicy: new TaskCreateSourceScopePolicyRequest(true, true)),
            "task-create-contributor-003");

        Assert.True(options.IsSuccess, options.Error);
        Assert.True(options.Value!.CanCreateTask);
        Assert.False(options.Value.CanManageProject);
        Assert.Empty(options.Value.Assignees);
        Assert.True(inherited.IsSuccess, inherited.Error);
        Assert.Equal(TaskCreateSourceScopeMode.Inherit, inherited.Value!.SourceScopeMode);
        Assert.Null(inherited.Value.PrimaryAssigneeUserId);
        Assert.False(assigneeDenied.IsSuccess);
        Assert.Equal("CapabilityDenied", assigneeDenied.ErrorDetail!.Code);
        Assert.Equal("body.primaryAssigneeUserId", assigneeDenied.ErrorDetail.Target);
        Assert.False(overrideDenied.IsSuccess);
        Assert.Equal("CapabilityDenied", overrideDenied.ErrorDetail!.Code);
        Assert.Equal("body.sourceScopeMode", overrideDenied.ErrorDetail.Target);

        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(1, await fixture.Db.TaskItems.CountAsync(item => item.Title == "Create task"));
        Assert.Empty(await fixture.Db.TaskExecutionScopeOverrides.ToListAsync());
        Assert.Single(await fixture.Db.IdempotencyRecords.ToListAsync());
    }

    [Fact]
    [Trait("Scope", "Issue410")]
    public async Task ReplayUsesCommittedTaskAfterPriorMilestoneAssigneeAndScopeSelectionsChange()
    {
        await using var fixture = await Fixture.CreateAsync(ProjectRole.Manager);
        var request = fixture.ValidRequest(
            primaryAssigneeUserId: fixture.Assignee.Id,
            sourceScopeMode: TaskCreateSourceScopeMode.TaskOverride,
            taskOverridePolicy: new TaskCreateSourceScopePolicyRequest(true, false));
        const string key = "task-create-replay-001";

        var first = await fixture.Service.CreateAsync(fixture.Project.Id, request, key);
        Assert.True(first.IsSuccess, first.Error);
        var createdTaskId = first.Value!.TaskId;

        var milestone = await fixture.Db.Milestones.SingleAsync(item => item.Id == fixture.Milestone.Id);
        milestone.MarkDeleted(DateTimeOffset.UtcNow);
        fixture.Db.ProjectMembers.Remove(await fixture.Db.ProjectMembers.SingleAsync(item =>
            item.ProjectId == fixture.Project.Id && item.UserId == fixture.Assignee.Id));
        var projectScope = await fixture.Db.ProjectExecutionScopes.SingleAsync(item => item.ProjectId == fixture.Project.Id);
        projectScope.WebEnabled = false;
        projectScope.ProjectFilesEnabled = true;
        projectScope.VersionNo++;
        var taskScope = await fixture.Db.TaskExecutionScopeOverrides.SingleAsync(item => item.TaskItemId == createdTaskId);
        taskScope.WebEnabled = false;
        taskScope.ProjectFilesEnabled = true;
        taskScope.VersionNo++;
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var replay = await fixture.Service.CreateAsync(fixture.Project.Id, request, key);

        Assert.True(replay.IsSuccess, replay.Error);
        Assert.Equal(createdTaskId, replay.Value!.TaskId);
        Assert.Equal(fixture.Milestone.Id, replay.Value.MilestoneId);
        Assert.Equal(fixture.Assignee.Id, replay.Value.PrimaryAssigneeUserId);
        Assert.Equal(TaskCreateSourceScopeMode.TaskOverride, replay.Value.SourceScopeMode);
        Assert.Equal(new TaskExecutionSourcePolicyResponse(false, true), replay.Value.TaskOverridePolicy);
        Assert.Equal(1, await fixture.Db.TaskItems.CountAsync(item => item.Id == createdTaskId));
        Assert.Single(await fixture.Db.IdempotencyRecords.ToListAsync());
        Assert.Single(fixture.Audit.Entries, entry => entry.Action == "TaskCreated");
    }

    [Fact]
    [Trait("Scope", "Issue410")]
    public async Task RequestMismatchAndHiddenProjectFailWithoutCreatingAnotherTask()
    {
        await using var fixture = await Fixture.CreateAsync(ProjectRole.Manager);
        const string key = "task-create-mismatch-001";
        var first = await fixture.Service.CreateAsync(fixture.Project.Id, fixture.ValidRequest(), key);
        var mismatch = await fixture.Service.CreateAsync(
            fixture.Project.Id,
            fixture.ValidRequest(title: "Different title"),
            key);

        Assert.True(first.IsSuccess, first.Error);
        Assert.False(mismatch.IsSuccess);
        Assert.Equal("IdempotencyConflict", mismatch.ErrorDetail!.Code);
        Assert.Equal("header.Idempotency-Key", mismatch.ErrorDetail.Target);
        Assert.Single(await fixture.Db.TaskItems.Where(item => item.Title == "Create task").ToListAsync());

        fixture.Current.UserIdValue = fixture.Outsider.Id;
        var hidden = await fixture.Service.CreateAsync(
            fixture.Project.Id,
            fixture.ValidRequest(),
            "task-create-hidden-001");

        Assert.False(hidden.IsSuccess);
        Assert.Equal("NotFound", hidden.ErrorDetail!.Code);
        Assert.Single(await fixture.Db.IdempotencyRecords.ToListAsync());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            AppDbContext db,
            CurrentTenantService currentTenant,
            MutableCurrentUser current,
            Tenant tenant,
            User actor,
            User assignee,
            User outsider,
            Project project,
            Milestone milestone,
            TaskWorkflowStage initialStage,
            CanonicalTaskCreateService service,
            RecordingAudit audit,
            RecordingNotifications notifications)
        {
            Db = db;
            CurrentTenant = currentTenant;
            Current = current;
            Tenant = tenant;
            Actor = actor;
            Assignee = assignee;
            Outsider = outsider;
            Project = project;
            Milestone = milestone;
            InitialStage = initialStage;
            Service = service;
            Audit = audit;
            Notifications = notifications;
        }

        public AppDbContext Db { get; }
        public CurrentTenantService CurrentTenant { get; }
        public MutableCurrentUser Current { get; }
        public Tenant Tenant { get; }
        public User Actor { get; }
        public User Assignee { get; }
        public User Outsider { get; }
        public Project Project { get; }
        public Milestone Milestone { get; }
        public TaskWorkflowStage InitialStage { get; }
        public CanonicalTaskCreateService Service { get; }
        public RecordingAudit Audit { get; }
        public RecordingNotifications Notifications { get; }

        public CanonicalCreateTaskRequest ValidRequest(
            string title = "Create task",
            Guid? primaryAssigneeUserId = null,
            TaskCreateSourceScopeMode sourceScopeMode = TaskCreateSourceScopeMode.Inherit,
            TaskCreateSourceScopePolicyRequest? taskOverridePolicy = null) => new(
            title,
            "Create description",
            TaskPriority.High,
            Milestone.Id,
            new DateOnly(2026, 8, 26),
            new DateOnly(2026, 8, 30),
            "Goal",
            "Deliverable",
            "Constraints",
            primaryAssigneeUserId,
            sourceScopeMode,
            taskOverridePolicy);

        public static async Task<Fixture> CreateAsync(ProjectRole actorRole)
        {
            var currentTenant = new CurrentTenantService();
            currentTenant.SetPlatformScope();
            var db = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseInMemoryDatabase($"issue410-task-create-{Guid.NewGuid():N}")
                    .Options,
                currentTenant);

            var tenant = new Tenant
            {
                Name = "Issue 410 tenant",
                DisplayName = "Issue 410 tenant",
                Slug = $"issue410-{Guid.NewGuid():N}"
            };
            var actor = User("manager");
            var assignee = User("assignee");
            var outsider = User("outsider");
            db.Tenants.Add(tenant);
            db.Users.AddRange(actor, assignee, outsider);
            await db.SaveChangesAsync();

            currentTenant.SetTenant(tenant.Id, tenant.Slug);
            var current = new MutableCurrentUser(actor.Id);
            var workspace = new Workspace
            {
                TenantId = tenant.Id,
                Name = "Issue 410 workspace",
                Slug = $"issue410-workspace-{Guid.NewGuid():N}",
                CreatedByUserId = actor.Id,
                Status = WorkspaceStatus.Active
            };
            var project = new Project
            {
                TenantId = tenant.Id,
                WorkspaceId = workspace.Id,
                OwnerUserId = actor.Id,
                CreatedByUserId = actor.Id,
                Name = "Issue 410 Project",
                Slug = $"issue410-project-{Guid.NewGuid():N}",
                Status = ProjectStatus.Active,
                Visibility = ProjectVisibility.MembersOnly,
                ActivationState = ProjectActivationState.Activated,
                ActivatedAtUtc = new DateTimeOffset(2026, 8, 25, 0, 0, 0, TimeSpan.Zero),
                ActivationVersion = 1,
                VersionNo = 1
            };
            var definition = new TaskWorkflowDefinition
            {
                TenantId = tenant.Id,
                WorkspaceId = workspace.Id,
                ProjectId = project.Id,
                Name = "Issue 410 workflow"
            };
            var initialStage = new TaskWorkflowStage
            {
                TenantId = tenant.Id,
                WorkspaceId = workspace.Id,
                ProjectId = project.Id,
                DefinitionId = definition.Id,
                Name = "Backlog",
                InternalCategory = TaskStageCategory.Backlog,
                IsInitialStage = true,
                SortKey = 1000
            };
            var milestone = new Milestone
            {
                TenantId = tenant.Id,
                ProjectId = project.Id,
                Name = "Issue 410 milestone",
                SortOrder = 1000
            };
            db.Workspaces.Add(workspace);
            db.WorkspaceMembers.AddRange(
                Membership(tenant.Id, workspace.Id, actor.Id, WorkspaceRole.Member),
                Membership(tenant.Id, workspace.Id, assignee.Id, WorkspaceRole.Member));
            db.Projects.Add(project);
            db.ProjectMembers.AddRange(
                ProjectMembership(tenant.Id, project.Id, actor.Id, actorRole),
                ProjectMembership(tenant.Id, project.Id, assignee.Id, ProjectRole.Contributor));
            db.TaskWorkflowDefinitions.Add(definition);
            db.TaskWorkflowStages.Add(initialStage);
            db.Milestones.Add(milestone);
            db.ProjectExecutionScopes.Add(new ProjectExecutionScope
            {
                TenantId = tenant.Id,
                WorkspaceId = workspace.Id,
                ProjectId = project.Id,
                WebEnabled = true,
                ProjectFilesEnabled = false,
                VersionNo = 1,
                UpdatedByUserId = actor.Id
            });
            await db.SaveChangesAsync();

            var projectRepository = new ProjectRepository(db);
            var userRepository = new UserRepository(db);
            var workspaceRepository = new WorkspaceRepository(db);
            var groupRepository = new GroupRepository(db);
            var workspaceAuthorization = new WorkspaceAuthorizationService(userRepository, workspaceRepository);
            var groupAuthorization = new GroupAuthorizationService(groupRepository, workspaceRepository, workspaceAuthorization);
            var projectAuthorization = new ProjectAuthorizationService(
                projectRepository,
                workspaceAuthorization,
                groupAuthorization,
                groupRepository);
            var clock = new FixedClock();
            var audit = new RecordingAudit();
            var notifications = new RecordingNotifications();
            var outbox = new TransactionalOutbox(new OutboxEventRepository(db), currentTenant, clock);
            var service = new CanonicalTaskCreateService(
                projectRepository,
                userRepository,
                new TaskExecutionScopeRepository(db),
                projectAuthorization,
                projectAuthorization,
                new TaskRelationshipTargetPolicy(projectRepository, userRepository, projectAuthorization),
                current,
                currentTenant,
                clock,
                audit,
                new BusinessInvalidationPublisher(outbox, currentTenant, clock),
                notifications,
                new EfCreateIdempotencyCoordinator(db));

            return new Fixture(
                db,
                currentTenant,
                current,
                tenant,
                actor,
                assignee,
                outsider,
                project,
                milestone,
                initialStage,
                service,
                audit,
                notifications);
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();

        private static User User(string name) => new()
        {
            DisplayName = $"Issue 410 {name}",
            Email = $"issue410-{name}-{Guid.NewGuid():N}@example.test",
            NormalizedEmail = $"ISSUE410-{name}-{Guid.NewGuid():N}@EXAMPLE.TEST",
            PasswordHash = "not-used-by-test",
            Status = UserStatus.Active
        };

        private static WorkspaceMember Membership(Guid tenantId, Guid workspaceId, Guid userId, WorkspaceRole role) => new()
        {
            TenantId = tenantId,
            WorkspaceId = workspaceId,
            UserId = userId,
            Role = role,
            Status = MembershipStatus.Active,
            JoinedAt = new DateTimeOffset(2026, 8, 25, 0, 0, 0, TimeSpan.Zero)
        };

        private static ProjectMember ProjectMembership(Guid tenantId, Guid projectId, Guid userId, ProjectRole role) => new()
        {
            TenantId = tenantId,
            ProjectId = projectId,
            UserId = userId,
            Role = role,
            JoinedAt = new DateTimeOffset(2026, 8, 25, 0, 0, 0, TimeSpan.Zero)
        };
    }

    private sealed class MutableCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid? UserIdValue { get; set; } = userId;
        public Guid? UserId => UserIdValue;
        public Guid? SessionId => null;
        public string? Email => null;
        public SystemRole? SystemRole => Coglatas.Domain.Enums.SystemRole.User;
        public bool IsAuthenticated => UserIdValue.HasValue;
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 8, 25, 9, 30, 0, TimeSpan.Zero);
    }

    private sealed class RecordingAudit : IAuditLogger
    {
        public List<AuditLogEntry> Entries { get; } = [];

        public Task LogAsync(AuditLogEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingNotifications : ITaskNotificationProducer
    {
        public List<TaskNotificationRecipientRequest> Requests { get; } = [];

        public Task ProduceAsync(TaskNotificationRecipientRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.CompletedTask;
        }
    }
}
