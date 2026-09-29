using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Projects;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Tests.PostgreSql;

/// <summary>
/// Relational acceptance evidence for the Task v1 write boundary.  These tests
/// deliberately use independent Npgsql DbContexts; InMemory and SQLite cannot
/// establish the unique-index or optimistic-concurrency guarantees asserted here.
/// </summary>
[Collection("PostgreSqlTaskV1")]
public sealed class TaskV1PostgreSqlAcceptanceTests
{
    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task StructuredTaskBriefColumnsRoundTripAtTheirPostgreSqlLimit()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        var graph = await SeedAsync(connectionString);
        var goal = new string('g', TaskBriefText.MaximumFieldLength);

        await using (var writer = CreateContext(connectionString, graph.Tenant))
        {
            var task = await writer.TaskItems.SingleAsync(item => item.Id == graph.Task.Id);
            task.Description = "Legacy free-form notes";
            task.BriefGoal = goal;
            task.BriefDeliverable = "Deliverable";
            task.BriefConstraints = "Constraints";
            await writer.SaveChangesAsync();
        }

        await using var verify = CreateContext(connectionString, graph.Tenant);
        var persisted = await verify.TaskItems.AsNoTracking().SingleAsync(item => item.Id == graph.Task.Id);
        Assert.Equal("Legacy free-form notes", persisted.Description);
        Assert.Equal(goal, persisted.BriefGoal);
        Assert.Equal("Deliverable", persisted.BriefDeliverable);
        Assert.Equal("Constraints", persisted.BriefConstraints);
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task ConcurrentTaskWritesPersistOnlyTheWinnerAndClearTheLoser()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        var graph = await SeedAsync(connectionString);
        await using var writerA = CreateContext(connectionString, graph.Tenant);
        await using var writerB = CreateContext(connectionString, graph.Tenant);
        var taskA = await writerA.TaskItems.SingleAsync(item => item.Id == graph.Task.Id);
        var taskB = await writerB.TaskItems.SingleAsync(item => item.Id == graph.Task.Id);

        QueueTaskMutation(writerA, taskA, graph.User.Id, "winner");
        QueueTaskMutation(writerB, taskB, graph.User.Id, "loser");

        Assert.Equal(TaskCommandSaveResult.Saved, await new EfUnitOfWork(writerA).SaveTaskCommandAsync());
        Assert.Equal(TaskCommandSaveResult.ConcurrencyConflict, await new EfUnitOfWork(writerB).SaveTaskCommandAsync());
        Assert.Empty(writerB.ChangeTracker.Entries());

        await using var verify = CreateContext(connectionString, graph.Tenant);
        var persisted = await verify.TaskItems.SingleAsync(item => item.Id == graph.Task.Id);
        Assert.Equal("winner", persisted.Title);
        Assert.Equal(2, persisted.VersionNo);
        Assert.Single(await verify.AuditLogs.Where(log => log.EntityId == graph.Task.Id).ToListAsync());
        Assert.Single(await verify.OutboxEvents.Where(evt => evt.AggregateId == graph.Task.Id).ToListAsync());

        // A cleared failed request can safely retry from the authoritative version.
        await using var retry = CreateContext(connectionString, graph.Tenant);
        var retryTask = await retry.TaskItems.SingleAsync(item => item.Id == graph.Task.Id);
        QueueTaskMutation(retry, retryTask, graph.User.Id, "retry");
        Assert.Equal(TaskCommandSaveResult.Saved, await new EfUnitOfWork(retry).SaveTaskCommandAsync());
        await using var final = CreateContext(connectionString, graph.Tenant);
        Assert.Equal("retry", (await final.TaskItems.SingleAsync(item => item.Id == graph.Task.Id)).Title);
        Assert.Equal(3, (await final.TaskItems.SingleAsync(item => item.Id == graph.Task.Id)).VersionNo);
        Assert.Equal(2, await final.AuditLogs.CountAsync(log => log.EntityId == graph.Task.Id));
        Assert.Equal(2, await final.OutboxEvents.CountAsync(evt => evt.AggregateId == graph.Task.Id));
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task ConcurrentTaskFileAssociationLeavesOneActiveLinkAndNoLosingSideEffects()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        var graph = await SeedAsync(connectionString, withFile: true);
        await using var writerA = CreateContext(connectionString, graph.Tenant);
        await using var writerB = CreateContext(connectionString, graph.Tenant);
        var taskA = await writerA.TaskItems.SingleAsync(item => item.Id == graph.Task.Id);
        var taskB = await writerB.TaskItems.SingleAsync(item => item.Id == graph.Task.Id);
        var fileId = graph.File!.Id;

        QueueTaskFileAssociation(writerA, taskA, graph, "winner");
        QueueTaskFileAssociation(writerB, taskB, graph, "loser");

        Assert.Equal(TaskCommandSaveResult.Saved, (await new EfUnitOfWork(writerA).SaveTaskCommandAsync()).Result);
        Assert.Equal(TaskCommandSaveResult.UniqueConflict, (await new EfUnitOfWork(writerB).SaveTaskCommandAsync()).Result);
        Assert.Empty(writerB.ChangeTracker.Entries());

        await using var verify = CreateContext(connectionString, graph.Tenant);
        Assert.Single(await verify.Attachments.Where(item => item.OwnerType == AttachmentOwnerType.TaskItem && item.OwnerId == graph.Task.Id && item.FileObjectId == fileId && item.DeletedAt == null).ToListAsync());
        Assert.Single(await verify.AuditLogs.Where(log => log.EntityId == graph.Task.Id && log.Action == "TaskFileAssociated").ToListAsync());
        Assert.Single(await verify.OutboxEvents.Where(evt => evt.AggregateId == graph.Task.Id && evt.EventType == "Projects.TaskChanged.v1").ToListAsync());
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task WatchUniqueConstraintAllowsOnlyOneIndependentWriterAndClearsLoser()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        var graph = await SeedAsync(connectionString);
        await using var winner = CreateContext(connectionString, graph.Tenant);
        winner.WorkItemWatchStates.Add(new WorkItemWatchState { TaskItemId = graph.Task.Id, UserId = graph.User.Id, AutomaticSources = WorkItemWatchAutomaticSource.Creator, IsWatching = true, UpdatedAt = DateTimeOffset.UtcNow, VersionNo = 1 });
        Assert.Equal(TaskCommandSaveResult.Saved, (await new EfUnitOfWork(winner).SaveTaskCommandAsync()).Result);

        await using var loser = CreateContext(connectionString, graph.Tenant);
        loser.WorkItemWatchStates.Add(new WorkItemWatchState { TaskItemId = graph.Task.Id, UserId = graph.User.Id, AutomaticSources = WorkItemWatchAutomaticSource.Creator, IsWatching = true, UpdatedAt = DateTimeOffset.UtcNow, VersionNo = 1 });
        Assert.Equal(TaskCommandSaveResult.UniqueConflict, (await new EfUnitOfWork(loser).SaveTaskCommandAsync()).Result);
        Assert.Empty(loser.ChangeTracker.Entries());

        await using var verify = CreateContext(connectionString, graph.Tenant);
        Assert.Equal(1, await verify.WorkItemWatchStates.CountAsync(state => state.TaskItemId == graph.Task.Id && state.UserId == graph.User.Id));
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task NormalizedLabelUniqueConstraintRejectsCaseAndWhitespaceVariantsWithoutLoserEffects()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        var graph = await SeedAsync(connectionString);

        await using var winner = CreateContext(connectionString, graph.Tenant);
        winner.ProjectTaskLabels.Add(new ProjectTaskLabel { WorkspaceId = graph.Workspace.Id, ProjectId = graph.Project.Id, Name = "Release", SortKey = 1024, VersionNo = 1 });
        Assert.Equal(TaskCommandSaveResult.Saved, (await new EfUnitOfWork(winner).SaveTaskCommandAsync()).Result);

        await using var loser = CreateContext(connectionString, graph.Tenant);
        loser.ProjectTaskLabels.Add(new ProjectTaskLabel { WorkspaceId = graph.Workspace.Id, ProjectId = graph.Project.Id, Name = " release ", SortKey = 2048, VersionNo = 1 });
        Assert.Equal(TaskCommandSaveResult.UniqueConflict, (await new EfUnitOfWork(loser).SaveTaskCommandAsync()).Result);
        Assert.Empty(loser.ChangeTracker.Entries());

        await using (var verify = CreateContext(connectionString, graph.Tenant))
        {
            Assert.Equal(1, await verify.ProjectTaskLabels.CountAsync(label => label.ProjectId == graph.Project.Id));
            Assert.Equal("release", await verify.ProjectTaskLabels.Where(label => label.ProjectId == graph.Project.Id).Select(label => label.NormalizedName).SingleAsync());
            Assert.Empty(await verify.AuditLogs.Where(log => log.EntityId == graph.Project.Id).ToListAsync());
            Assert.Empty(await verify.OutboxEvents.Where(evt => evt.AggregateId == graph.Project.Id).ToListAsync());
        }

        // A cleared loser context is immediately reusable for a non-conflicting retry.
        loser.ProjectTaskLabels.Add(new ProjectTaskLabel { WorkspaceId = graph.Workspace.Id, ProjectId = graph.Project.Id, Name = "Other", SortKey = 3072, VersionNo = 1 });
        Assert.Equal(TaskCommandSaveResult.Saved, (await new EfUnitOfWork(loser).SaveTaskCommandAsync()).Result);

        var otherTenant = new Tenant { Name = $"Other {Guid.NewGuid():N}", DisplayName = "Other", Slug = $"other-{Guid.NewGuid():N}" };
        await using (var platform = CreatePlatformContext(connectionString))
        {
            platform.Tenants.Add(otherTenant);
            await platform.SaveChangesAsync();
        }
        await using var otherContext = CreateContext(connectionString, otherTenant);
        Assert.Empty(await otherContext.TaskItems.Where(item => item.Id == graph.Task.Id).ToListAsync());
    }

    private static AppDbContext CreateContext(string connectionString, Tenant tenant)
    {
        var currentTenant = new CurrentTenantService();
        currentTenant.SetTenant(tenant.Id, tenant.Slug);
        return new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options, currentTenant);
    }

    private static AppDbContext CreatePlatformContext(string connectionString)
    {
        var currentTenant = new CurrentTenantService();
        currentTenant.SetPlatformScope();
        return new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options, currentTenant);
    }

    private static async Task<Graph> SeedAsync(string connectionString, bool withFile = false)
    {
        var suffix = Guid.NewGuid().ToString("N");
        await using var context = CreatePlatformContext(connectionString);
        var tenant = new Tenant { Name = $"Task acceptance {suffix}", DisplayName = "Task acceptance", Slug = $"task-acceptance-{suffix}" };
        var user = new User { DisplayName = "Task acceptance user", Email = $"task-{suffix}@example.test", NormalizedEmail = $"TASK-{suffix}@EXAMPLE.TEST", PasswordHash = "hash", Status = UserStatus.Active };
        context.Tenants.Add(tenant);
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var currentTenant = new CurrentTenantService();
        currentTenant.SetTenant(tenant.Id, tenant.Slug);
        await using var tenantContext = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options, currentTenant);
        var workspace = new Workspace { TenantId = tenant.Id, Name = "Task acceptance workspace", Slug = $"task-acceptance-ws-{suffix}", CreatedByUserId = user.Id };
        var project = new Project { TenantId = tenant.Id, WorkspaceId = workspace.Id, OwnerUserId = user.Id, CreatedByUserId = user.Id, Name = "Task acceptance project", Slug = $"task-acceptance-project-{suffix}" };
        var task = new TaskItem { TenantId = tenant.Id, WorkspaceId = workspace.Id, ProjectId = project.Id, Title = "original", CreatedByUserId = user.Id, Priority = TaskPriority.Medium, VersionNo = 1 };
        tenantContext.Workspaces.Add(workspace);
        tenantContext.WorkspaceMembers.Add(new WorkspaceMember { TenantId = tenant.Id, WorkspaceId = workspace.Id, UserId = user.Id, Role = WorkspaceRole.Owner, Status = MembershipStatus.Active, JoinedAt = DateTimeOffset.UtcNow });
        tenantContext.TenantUsers.Add(new TenantUser { TenantId = tenant.Id, UserId = user.Id, Role = TenantUserRole.Member, Status = TenantUserStatus.Active, JoinedAt = DateTimeOffset.UtcNow });
        tenantContext.Projects.Add(project);
        tenantContext.ProjectMembers.Add(new ProjectMember { TenantId = tenant.Id, ProjectId = project.Id, UserId = user.Id, Role = ProjectRole.Owner, JoinedAt = DateTimeOffset.UtcNow });
        tenantContext.TaskItems.Add(task);
        FileObject? file = null;
        if (withFile)
        {
            file = new FileObject { TenantId = tenant.Id, WorkspaceId = workspace.Id, ProjectId = project.Id, UploadedByUserId = user.Id, OriginalFileName = "acceptance.txt", StorageKey = $"acceptance/{suffix}", ContentType = "text/plain", SizeBytes = 1, Status = FileObjectStatus.Active };
            tenantContext.FileObjects.Add(file);
        }
        await tenantContext.SaveChangesAsync();
        return new Graph(tenant, workspace, project, task, user, file);
    }

    private static void QueueTaskMutation(AppDbContext context, TaskItem task, Guid actorId, string title)
    {
        task.Title = title;
        task.VersionNo++;
        QueueSideEffects(context, task, actorId, title);
    }

    private static void QueueTaskFileAssociation(AppDbContext context, TaskItem task, Graph graph, string marker)
    {
        task.VersionNo++;
        context.Attachments.Add(new Attachment
        {
            TenantId = graph.Tenant.Id, FileObjectId = graph.File!.Id, WorkspaceId = graph.Workspace.Id,
            OwnerType = AttachmentOwnerType.TaskItem, OwnerId = graph.Task.Id, OwnerUserId = graph.User.Id,
            UploadedByUserId = graph.User.Id, FileName = "acceptance.txt", StoredFileName = "acceptance.txt",
            FilePath = "acceptance.txt", ContentType = "text/plain", Extension = ".txt", SizeBytes = 1,
            StorageProvider = "test", StorageKey = $"acceptance/{marker}", ScanStatus = FileScanStatus.Clean
        });
        QueueSideEffects(context, task, graph.User.Id, marker, "TaskFileAssociated");
    }

    private static void QueueSideEffects(AppDbContext context, TaskItem task, Guid actorId, string marker, string action = "TaskUpdated")
    {
        context.AuditLogs.Add(new AuditLog { TenantId = task.TenantId, ActorUserId = actorId, Action = action, EntityType = "TaskItem", EntityId = task.Id, WorkspaceId = task.WorkspaceId, ProjectId = task.ProjectId, Summary = marker, CreatedAt = DateTimeOffset.UtcNow });
        context.OutboxEvents.Add(new OutboxEvent(Guid.NewGuid()) { TenantId = task.TenantId, EventType = "Projects.TaskChanged.v1", PayloadSchemaVersion = 1, AggregateType = "Task", AggregateId = task.Id, AggregateVersion = task.VersionNo, OccurredAt = DateTimeOffset.UtcNow, PayloadJson = "{}", RoutingJson = "[]", Status = OutboxEventStatus.Pending, NextAttemptAt = DateTimeOffset.UtcNow });
    }

    private sealed record Graph(Tenant Tenant, Workspace Workspace, Project Project, TaskItem Task, User User, FileObject? File);
}

[CollectionDefinition("PostgreSqlTaskV1", DisableParallelization = true)]
public sealed class PostgreSqlTaskV1Collection;
