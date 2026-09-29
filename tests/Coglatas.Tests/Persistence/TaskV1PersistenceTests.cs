using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Projects;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Tests.Persistence;

public sealed class TaskV1PersistenceTests
{
    [Fact]
    public void StructuredTaskBriefUsesBoundedNullableColumnsWithoutAddingListIndexes()
    {
        using var context = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options,
            new CurrentTenantService());
        var entity = context.Model.FindEntityType(typeof(TaskItem));

        Assert.NotNull(entity);
        foreach (var propertyName in new[]
                 {
                     nameof(TaskItem.BriefGoal),
                     nameof(TaskItem.BriefDeliverable),
                     nameof(TaskItem.BriefConstraints)
                 })
        {
            var property = entity.FindProperty(propertyName);
            Assert.NotNull(property);
            Assert.True(property.IsNullable);
            Assert.Equal(TaskBriefText.MaximumFieldLength, property.GetMaxLength());
            Assert.DoesNotContain(entity.GetIndexes(), index => index.Properties.Contains(property));
        }
    }

    [Fact]
    public async Task CreatingPlanningProjectDoesNotProvisionTaskWorkflowBeforeActivation()
    {
        var (context, _, workspace, user) = await CreateGraphAsync();
        var project = new Project
        {
            WorkspaceId = workspace.Id,
            OwnerUserId = user.Id,
            CreatedByUserId = user.Id,
            Name = "Task v1 project",
            Slug = "task-v1-project",
            Status = ProjectStatus.Planning,
            ActivationState = ProjectActivationState.NeverActivated
        };

        context.Projects.Add(project);
        await context.SaveChangesAsync();

        Assert.Empty(await context.TaskWorkflowDefinitions.Where(item => item.ProjectId == project.Id).ToListAsync());
        Assert.Empty(await context.TaskWorkflowStages.Where(item => item.ProjectId == project.Id).ToListAsync());
    }

    [Fact]
    public async Task TaskVersionIsIncrementedAndDetectsConcurrentWriter()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        var currentTenant = new CurrentTenantService();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(databaseName).Options;
        await using var seed = new AppDbContext(options, currentTenant);
        var (tenant, workspace, user) = await SeedGraphAsync(seed, currentTenant);
        var project = new Project { WorkspaceId = workspace.Id, OwnerUserId = user.Id, CreatedByUserId = user.Id, Name = "Concurrency", Slug = "concurrency" };
        seed.Projects.Add(project);
        await seed.SaveChangesAsync();
        var task = new TaskItem { WorkspaceId = workspace.Id, ProjectId = project.Id, Title = "Versioned", CreatedByUserId = user.Id, Priority = TaskPriority.Medium };
        seed.TaskItems.Add(task);
        await seed.SaveChangesAsync();

        var tenantA = new CurrentTenantService();
        tenantA.SetTenant(tenant.Id, tenant.Slug);
        var tenantB = new CurrentTenantService();
        tenantB.SetTenant(tenant.Id, tenant.Slug);
        await using var writerA = new AppDbContext(options, tenantA);
        await using var writerB = new AppDbContext(options, tenantB);
        var taskA = await writerA.TaskItems.SingleAsync();
        var taskB = await writerB.TaskItems.SingleAsync();

        taskA.Title = "Writer A";
        await writerA.SaveChangesAsync();
        Assert.Equal(2, taskA.VersionNo);

        taskB.Title = "Writer B";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => writerB.SaveChangesAsync());
    }

    [Fact]
    public void CanonicalPriorityHasNoNormalAlias()
    {
        Assert.Equal([TaskPriority.Low, TaskPriority.Medium, TaskPriority.High, TaskPriority.Critical], Enum.GetValues<TaskPriority>());
    }

    [Fact]
    public void DomainV1FeatureUsesTheCentralizedFeatureKeyRegistry()
    {
        Assert.Contains(FeatureKeys.TasksDomainV1, FeatureKeys.All);
        Assert.Contains(FeatureKeys.KanbanV1, FeatureKeys.All);
        Assert.Contains(FeatureKeys.GanttV1, FeatureKeys.All);
        Assert.Contains(FeatureKeys.TasksNotificationsV1, FeatureKeys.All);
        Assert.DoesNotContain(FeatureKeys.TasksNotificationsV1, FeatureKeys.DefaultEnabled);
    }

    [Fact]
    public async Task KanbanUsesCanonicalTaskRankIndexAndWorkflowConcurrencyTokens()
    {
        var (context, _, _, _) = await CreateGraphAsync();
        var taskType = context.Model.FindEntityType(typeof(TaskItem))!;
        var definitionType = context.Model.FindEntityType(typeof(TaskWorkflowDefinition))!;
        var stageType = context.Model.FindEntityType(typeof(TaskWorkflowStage))!;

        Assert.Contains(taskType.GetIndexes(), index =>
            index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(TaskItem.ProjectId), nameof(TaskItem.WorkflowStageId), nameof(TaskItem.SortKey)]));
        Assert.True(taskType.FindProperty(nameof(TaskItem.VersionNo))!.IsConcurrencyToken);
        Assert.True(definitionType.FindProperty(nameof(TaskWorkflowDefinition.VersionNo))!.IsConcurrencyToken);
        Assert.True(stageType.FindProperty(nameof(TaskWorkflowStage.VersionNo))!.IsConcurrencyToken);
        Assert.Equal(40, definitionType.FindProperty(nameof(TaskWorkflowDefinition.KanbanDefaultSwimlane))!.GetMaxLength());
    }

    [Fact]
    public async Task TaskReviewOutcomeIsPersistedWithTheTaskAggregate()
    {
        var (context, _, workspace, user) = await CreateGraphAsync();
        var project = new Project { WorkspaceId = workspace.Id, OwnerUserId = user.Id, CreatedByUserId = user.Id, Name = "Review", Slug = "review" };
        context.Projects.Add(project);
        var task = new TaskItem { WorkspaceId = workspace.Id, ProjectId = project.Id, Title = "Review me", CreatedByUserId = user.Id, ReviewStatus = TaskReviewStatus.Submitted, ReviewSubmittedAt = DateTimeOffset.UtcNow };
        context.TaskItems.Add(task);
        await context.SaveChangesAsync();

        var persisted = await context.TaskItems.SingleAsync(item => item.Id == task.Id);
        Assert.Equal(TaskReviewStatus.Submitted, persisted.ReviewStatus);
        Assert.NotNull(persisted.ReviewSubmittedAt);
    }

    private static async Task<(AppDbContext Context, Tenant Tenant, Workspace Workspace, User User)> CreateGraphAsync()
    {
        var currentTenant = new CurrentTenantService();
        var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options, currentTenant);
        var (_, workspace, user) = await SeedGraphAsync(context, currentTenant);
        var tenant = await context.Tenants.SingleAsync();
        return (context, tenant, workspace, user);
    }

    private static async Task<(Tenant Tenant, Workspace Workspace, User User)> SeedGraphAsync(AppDbContext context, CurrentTenantService currentTenant)
    {
        currentTenant.SetPlatformScope();
        var tenant = new Tenant { Name = "Task v1 tenant", DisplayName = "Task v1 tenant", Slug = $"task-v1-{Guid.NewGuid():N}" };
        var user = new User { DisplayName = "Task v1 user", Email = $"task-v1-{Guid.NewGuid():N}@example.test", NormalizedEmail = $"TASK-V1-{Guid.NewGuid():N}@EXAMPLE.TEST", PasswordHash = "hash" };
        context.Tenants.Add(tenant);
        context.Users.Add(user);
        await context.SaveChangesAsync();

        currentTenant.SetTenant(tenant.Id, tenant.Slug);
        var workspace = new Workspace { Name = "Task v1 workspace", Slug = $"task-v1-{Guid.NewGuid():N}", CreatedByUserId = user.Id };
        context.Workspaces.Add(workspace);
        await context.SaveChangesAsync();
        return (tenant, workspace, user);
    }
}
