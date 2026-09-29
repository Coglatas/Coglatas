using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Planning;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Tests.Planning;

public sealed class MyTasksProjectionRepositoryTests
{
    [Fact]
    [Trait("Scope", "TaskV1PR04")]
    public async Task RelationshipViewsAreDistinctAndTheSameTaskMayAppearInMoreThanOneView()
    {
        var graph = await CreateGraphAsync();
        var shared = graph.NewTask("Shared", primaryAssignee: graph.User.Id, reviewer: graph.User.Id);
        // A Reviewer cannot equal the primary assignee in the real schema, so use the
        // collaborator relationship for the same-row overlap assertion instead.
        shared.ReviewerUserId = graph.OtherUser.Id;
        graph.Context.WorkItemCollaborators.Add(new WorkItemCollaborator { TaskItemId = shared.Id, UserId = graph.User.Id, AddedByUserId = graph.User.Id, AddedAt = DateTimeOffset.UtcNow });
        graph.Context.WorkItemWatchStates.Add(new WorkItemWatchState { TaskItemId = shared.Id, UserId = graph.User.Id, IsManualWatch = true, IsWatching = true, UpdatedAt = DateTimeOffset.UtcNow });

        var review = graph.NewTask("Review", reviewer: graph.User.Id);
        var created = graph.NewTask("Created");
        var queue = graph.NewTask("Queue", targetGroupId: graph.Group.Id);
        await graph.Context.SaveChangesAsync();

        var repository = new PlanningRepository(graph.Context);
        var scope = new MyTasksQuery(WorkspaceId: graph.Workspace.Id);

        var assigned = await repository.ListMyTasksAsync(graph.User.Id, scope with { View = MyTasksRelationshipView.Assigned }, DateTimeOffset.UtcNow);
        var participating = await repository.ListMyTasksAsync(graph.User.Id, scope with { View = MyTasksRelationshipView.Participating }, DateTimeOffset.UtcNow);
        var reviews = await repository.ListMyTasksAsync(graph.User.Id, scope with { View = MyTasksRelationshipView.Reviews }, DateTimeOffset.UtcNow);
        var watching = await repository.ListMyTasksAsync(graph.User.Id, scope with { View = MyTasksRelationshipView.Watching }, DateTimeOffset.UtcNow);
        var teamQueue = await repository.ListMyTasksAsync(graph.User.Id, scope with { View = MyTasksRelationshipView.TeamQueue }, DateTimeOffset.UtcNow);

        Assert.Contains(assigned.Items, task => task.TaskId == shared.Id);
        Assert.Contains(participating.Items, task => task.TaskId == shared.Id);
        Assert.Contains(watching.Items, task => task.TaskId == shared.Id);
        Assert.Contains(reviews.Items, task => task.TaskId == review.Id);
        Assert.Contains(teamQueue.Items, task => task.TaskId == queue.Id);
        Assert.True(assigned.Items.Single(task => task.TaskId == shared.Id).Relationships.IsCollaborator);
        Assert.Contains(created.Id, (await repository.ListMyTasksAsync(graph.User.Id, scope with { View = MyTasksRelationshipView.Created }, DateTimeOffset.UtcNow)).Items.Select(task => task.TaskId));
    }

    [Fact]
    [Trait("Scope", "TaskV1PR04")]
    public async Task CurrentWorkspaceDoesNotMixWorkspacesButExplicitAllWorkspacesDoes()
    {
        var graph = await CreateGraphAsync();
        var otherWorkspace = new Workspace { Name = "Second", Slug = "second", CreatedByUserId = graph.User.Id };
        graph.Context.Workspaces.Add(otherWorkspace);
        await graph.Context.SaveChangesAsync();
        graph.Context.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = otherWorkspace.Id, UserId = graph.User.Id, Status = MembershipStatus.Active, Role = WorkspaceRole.Member, JoinedAt = DateTimeOffset.UtcNow });
        var otherProject = new Project { WorkspaceId = otherWorkspace.Id, OwnerUserId = graph.User.Id, CreatedByUserId = graph.User.Id, Name = "Second project", Slug = "second-project", Status = ProjectStatus.Active };
        graph.Context.Projects.Add(otherProject);
        await graph.Context.SaveChangesAsync();
        var current = graph.NewTask("Current", primaryAssignee: graph.User.Id);
        var other = new TaskItem { WorkspaceId = otherWorkspace.Id, ProjectId = otherProject.Id, Title = "Other", CreatedByUserId = graph.User.Id, PrimaryAssigneeUserId = graph.User.Id, Status = TaskItemStatus.NotStarted };
        graph.Context.TaskItems.Add(other);
        await graph.Context.SaveChangesAsync();

        var repository = new PlanningRepository(graph.Context);
        var currentScope = await repository.ListMyTasksAsync(graph.User.Id, new MyTasksQuery(WorkspaceId: graph.Workspace.Id), DateTimeOffset.UtcNow);
        var allScope = await repository.ListMyTasksAsync(graph.User.Id, new MyTasksQuery(Scope: MyTasksScope.AllWorkspaces), DateTimeOffset.UtcNow);

        Assert.Equal([current.Id], currentScope.Items.Select(item => item.TaskId));
        Assert.Equal(new[] { current.Id, other.Id }.OrderBy(id => id), allScope.Items.Select(item => item.TaskId).OrderBy(id => id));
        Assert.All(allScope.Items, item => Assert.False(string.IsNullOrWhiteSpace(item.WorkspaceTitle)));
    }

    [Fact]
    [Trait("Scope", "TaskV1PR04")]
    public async Task CountsUseTheSameRelationshipAndScopePredicatesAsRows()
    {
        var graph = await CreateGraphAsync();
        var assigned = graph.NewTask("Assigned", primaryAssignee: graph.User.Id);
        graph.NewTask("Someone else", primaryAssignee: graph.OtherUser.Id);
        await graph.Context.SaveChangesAsync();
        var repository = new PlanningRepository(graph.Context);
        var query = new MyTasksQuery(WorkspaceId: graph.Workspace.Id);

        var page = await repository.ListMyTasksAsync(graph.User.Id, query, DateTimeOffset.UtcNow);
        var counts = await repository.GetMyTaskCountsAsync(graph.User.Id, query, DateTimeOffset.UtcNow);

        Assert.Equal(page.TotalCount, counts.Views.Single(item => item.View == MyTasksRelationshipView.Assigned).Count);
        Assert.Contains(page.Items, item => item.TaskId == assigned.Id);
        Assert.DoesNotContain(page.Items, item => item.Title == "Someone else");
    }

    private static async Task<TestGraph> CreateGraphAsync()
    {
        var currentTenant = new CurrentTenantService();
        var setup = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options, currentTenant);
        currentTenant.SetPlatformScope();
        var tenant = new Tenant { Name = "Tenant", DisplayName = "Tenant", Slug = $"tenant-{Guid.NewGuid():N}" };
        var user = new User { DisplayName = "User", Email = $"user-{Guid.NewGuid():N}@test", NormalizedEmail = $"USER-{Guid.NewGuid():N}@TEST", PasswordHash = "hash" };
        var otherUser = new User { DisplayName = "Other", Email = $"other-{Guid.NewGuid():N}@test", NormalizedEmail = $"OTHER-{Guid.NewGuid():N}@TEST", PasswordHash = "hash" };
        setup.AddRange(tenant, user, otherUser);
        await setup.SaveChangesAsync();
        currentTenant.SetTenant(tenant.Id, tenant.Slug);
        var workspace = new Workspace { Name = "Current", Slug = "current", CreatedByUserId = user.Id };
        setup.Workspaces.Add(workspace);
        await setup.SaveChangesAsync();
        setup.WorkspaceMembers.AddRange(
            new WorkspaceMember { WorkspaceId = workspace.Id, UserId = user.Id, Status = MembershipStatus.Active, Role = WorkspaceRole.Member, JoinedAt = DateTimeOffset.UtcNow },
            new WorkspaceMember { WorkspaceId = workspace.Id, UserId = otherUser.Id, Status = MembershipStatus.Active, Role = WorkspaceRole.Member, JoinedAt = DateTimeOffset.UtcNow });
        var group = new Group { WorkspaceId = workspace.Id, Name = "Queue", Slug = "queue", CreatedByUserId = user.Id };
        setup.Groups.Add(group);
        var project = new Project { WorkspaceId = workspace.Id, OwnerUserId = user.Id, CreatedByUserId = user.Id, Name = "Project", Slug = "project", Status = ProjectStatus.Active };
        setup.Projects.Add(project);
        await setup.SaveChangesAsync();
        setup.GroupMembers.Add(new GroupMember { GroupId = group.Id, UserId = user.Id, JoinedAt = DateTimeOffset.UtcNow });
        await setup.SaveChangesAsync();

        // Keep the context alive after setup; tenant query filters remain active.
        return new TestGraph(setup, workspace, group, project, user, otherUser);
    }

    private sealed class TestGraph(AppDbContext context, Workspace workspace, Group group, Project project, User user, User otherUser)
    {
        public AppDbContext Context { get; } = context;
        public Workspace Workspace { get; } = workspace;
        public Group Group { get; } = group;
        private Project Project { get; } = project;
        public User User { get; } = user;
        public User OtherUser { get; } = otherUser;

        public TaskItem NewTask(string title, Guid? primaryAssignee = null, Guid? reviewer = null, Guid? targetGroupId = null)
        {
            var task = new TaskItem
            {
                WorkspaceId = Workspace.Id,
                ProjectId = Project.Id,
                Title = title,
                CreatedByUserId = User.Id,
                PrimaryAssigneeUserId = primaryAssignee,
                ReviewerUserId = reviewer,
                TargetGroupId = targetGroupId,
                Status = TaskItemStatus.NotStarted
            };
            Context.TaskItems.Add(task);
            return task;
        }
    }
}
