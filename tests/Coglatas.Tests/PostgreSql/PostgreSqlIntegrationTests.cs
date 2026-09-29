using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Groups;
using Coglatas.Application.Messaging;
using Coglatas.Application.Projects;
using Coglatas.Application.Search;
using Coglatas.Application.Tenancy;
using Coglatas.Application.Workspaces;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Tests.PostgreSql;

public sealed class PostgreSqlIntegrationTests
{
    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR05")]
    public async Task MigrationsAndTenantScopedRepositoriesWorkAgainstPostgreSql()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        var currentTenant = new CurrentTenantService();
        var runId = Guid.NewGuid().ToString("N");

        await using var dbContext = new AppDbContext(options, currentTenant);
        var pendingMigrations = await dbContext.Database.GetPendingMigrationsAsync();
        Assert.Empty(pendingMigrations);

        var tenantA = new Tenant { Name = $"CI Tenant A {runId}", DisplayName = "CI Tenant A", Slug = $"ci-a-{runId}" };
        var tenantB = new Tenant { Name = $"CI Tenant B {runId}", DisplayName = "CI Tenant B", Slug = $"ci-b-{runId}" };
        var user = new User
        {
            DisplayName = "CI PostgreSQL User",
            Email = $"ci-{runId}@example.test",
            NormalizedEmail = $"CI-{runId}@EXAMPLE.TEST"
        };

        currentTenant.SetPlatformScope();
        await dbContext.Tenants.AddRangeAsync(tenantA, tenantB);
        await dbContext.Users.AddAsync(user);
        await dbContext.SaveChangesAsync();

        currentTenant.SetTenant(tenantA.Id, tenantA.Slug);
        var workspaceA = new Workspace
        {
            Name = "PostgreSQL Workspace A",
            Slug = $"pg-workspace-a-{runId}",
            CreatedByUserId = user.Id
        };
        await dbContext.Workspaces.AddAsync(workspaceA);
        await dbContext.WorkspaceMembers.AddAsync(new WorkspaceMember
        {
            WorkspaceId = workspaceA.Id,
            UserId = user.Id,
            Role = WorkspaceRole.Owner,
            Status = MembershipStatus.Active,
            JoinedAt = DateTimeOffset.UtcNow
        });
        var projectA = new Project
        {
            WorkspaceId = workspaceA.Id,
            OwnerUserId = user.Id,
            CreatedByUserId = user.Id,
            Name = "PostgreSQL Project A",
            Slug = $"pg-project-a-{runId}",
            Status = ProjectStatus.Active
        };
        await dbContext.Projects.AddAsync(projectA);
        await dbContext.ProjectMembers.AddAsync(new ProjectMember
        {
            ProjectId = projectA.Id,
            UserId = user.Id,
            Role = ProjectRole.Owner,
            JoinedAt = DateTimeOffset.UtcNow
        });
        await dbContext.SaveChangesAsync();

        currentTenant.SetTenant(tenantB.Id, tenantB.Slug);
        var workspaceB = new Workspace
        {
            Name = "PostgreSQL Workspace B",
            Slug = $"pg-workspace-b-{runId}",
            CreatedByUserId = user.Id
        };
        await dbContext.Workspaces.AddAsync(workspaceB);
        await dbContext.WorkspaceMembers.AddAsync(new WorkspaceMember
        {
            WorkspaceId = workspaceB.Id,
            UserId = user.Id,
            Role = WorkspaceRole.Owner,
            Status = MembershipStatus.Active,
            JoinedAt = DateTimeOffset.UtcNow
        });
        await dbContext.Projects.AddAsync(new Project
        {
            WorkspaceId = workspaceB.Id,
            OwnerUserId = user.Id,
            CreatedByUserId = user.Id,
            Name = "PostgreSQL Project B",
            Slug = $"pg-project-b-{runId}",
            Status = ProjectStatus.Active
        });
        await dbContext.SaveChangesAsync();

        var repository = new ProjectRepository(dbContext);

        currentTenant.SetTenant(tenantA.Id, tenantA.Slug);
        var tenantAProjects = await repository.ListVisibleAsync(user.Id);
        Assert.Contains(tenantAProjects, project => project.Name == "PostgreSQL Project A");
        Assert.DoesNotContain(tenantAProjects, project => project.Name == "PostgreSQL Project B");

        var workspaceMemberA = await dbContext.WorkspaceMembers.SingleAsync(member =>
            member.WorkspaceId == workspaceA.Id && member.UserId == user.Id);
        workspaceMemberA.Status = MembershipStatus.Suspended;
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var revokedTenantAProjects = await repository.ListVisibleAsync(user.Id);
        Assert.DoesNotContain(revokedTenantAProjects, project => project.Name == "PostgreSQL Project A");

        currentTenant.SetTenant(tenantB.Id, tenantB.Slug);
        var tenantBProjects = await repository.ListVisibleAsync(user.Id);
        Assert.Contains(tenantBProjects, project => project.Name == "PostgreSQL Project B");
        Assert.DoesNotContain(tenantBProjects, project => project.Name == "PostgreSQL Project A");
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "Issue329")]
    public async Task TenantScopedSearchIsolationWorksAgainstPostgreSql()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        var currentTenant = new CurrentTenantService();
        var runId = Guid.NewGuid().ToString("N");
        var now = new DateTimeOffset(2026, 6, 14, 0, 0, 0, TimeSpan.Zero);

        await using var dbContext = new AppDbContext(options, currentTenant);
        var pendingMigrations = await dbContext.Database.GetPendingMigrationsAsync();
        Assert.Empty(pendingMigrations);

        var tenantA = new Tenant { Name = $"Search Tenant A {runId}", DisplayName = "Search Tenant A", Slug = $"search-a-{runId}" };
        var tenantB = new Tenant { Name = $"Search Tenant B {runId}", DisplayName = "Search Tenant B", Slug = $"search-b-{runId}" };
        var userA = NewUser($"search-a-{runId}@example.test", "Search Tenant A User");
        var userB = NewUser($"search-b-{runId}@example.test", "Search Tenant B User");

        currentTenant.SetPlatformScope();
        dbContext.Tenants.AddRange(tenantA, tenantB);
        dbContext.Users.AddRange(userA, userB);
        await dbContext.SaveChangesAsync();

        var tenantAData = await SeedSearchGraphAsync(dbContext, currentTenant, tenantA, userA, runId, "TenantA", now);
        var tenantBData = await SeedSearchGraphAsync(dbContext, currentTenant, tenantB, userB, runId, "TenantB", now.AddMinutes(1));

        currentTenant.SetTenant(tenantA.Id, tenantA.Slug);
        var search = new DbSearchService(
            dbContext,
            new TestCurrentUser(userA),
            new MessagingRepository(dbContext));
        var result = await search.SearchAsync(new SearchRequest("securesearchneedle", PageSize: 50));

        Assert.True(result.IsSuccess, result.Error);
        var items = result.Value!.Items;
        AssertSearchContains(items, SearchResultType.Workspace, tenantAData.Workspace.Id, "Tenant A workspace search result was missing.");
        AssertSearchContains(items, SearchResultType.Group, tenantAData.Group.Id, "Tenant A group search result was missing.");
        AssertSearchContains(items, SearchResultType.Channel, tenantAData.Channel.Id, "Tenant A channel search result was missing.");
        AssertSearchContains(items, SearchResultType.Post, tenantAData.Post.Id, "Tenant A post search result was missing.");
        AssertSearchContains(items, SearchResultType.Message, tenantAData.Message.Id, "Tenant A message search result was missing.");
        AssertSearchContains(items, SearchResultType.Project, tenantAData.Project.Id, "Tenant A project search result was missing.");
        AssertSearchContains(items, SearchResultType.Task, tenantAData.Task.Id, "Tenant A task search result was missing.");
        AssertSearchContains(items, SearchResultType.Artifact, tenantAData.Artifact.Id, "Tenant A artifact search result was missing.");

        AssertSearchDoesNotContain(items, SearchResultType.Workspace, tenantBData.Workspace.Id, "Tenant A search leaked Tenant B workspace.");
        AssertSearchDoesNotContain(items, SearchResultType.Group, tenantBData.Group.Id, "Tenant A search leaked Tenant B group.");
        AssertSearchDoesNotContain(items, SearchResultType.Channel, tenantBData.Channel.Id, "Tenant A search leaked Tenant B channel.");
        AssertSearchDoesNotContain(items, SearchResultType.Post, tenantBData.Post.Id, "Tenant A search leaked Tenant B channel post.");
        AssertSearchDoesNotContain(items, SearchResultType.Message, tenantBData.Message.Id, "Tenant A search leaked Tenant B message.");
        AssertSearchDoesNotContain(items, SearchResultType.Project, tenantBData.Project.Id, "Tenant A search leaked Tenant B project.");
        AssertSearchDoesNotContain(items, SearchResultType.Task, tenantBData.Task.Id, "Tenant A search leaked Tenant B task.");
        AssertSearchDoesNotContain(items, SearchResultType.Artifact, tenantBData.Artifact.Id, "Tenant A search leaked Tenant B artifact.");

        var workspaceFileResult = await search.SearchAsync(new SearchRequest(
            "securesearchneedle",
            SearchResultType.File,
            WorkspaceId: tenantAData.Workspace.Id,
            PageSize: 50));
        Assert.True(workspaceFileResult.IsSuccess, workspaceFileResult.Error);
        var fileItem = Assert.Single(workspaceFileResult.Value!.Items);
        Assert.Equal(SearchResultType.File, fileItem.Type);
        Assert.Equal(tenantAData.FileObject.Id, fileItem.Id);
        Assert.Equal(tenantAData.FileObject.OriginalFileName, fileItem.Title);
        Assert.Null(fileItem.Snippet);
        Assert.Equal(tenantAData.Workspace.Id, fileItem.WorkspaceId);
        Assert.Equal($"/workspaces/{tenantAData.Workspace.Id}/files", fileItem.Route);
        Assert.Equal(tenantAData.FileObject.ContentType, fileItem.ContentType);
        Assert.Equal(tenantAData.FileObject.SizeBytes, fileItem.SizeBytes);
        Assert.Equal(tenantAData.FileObject.Status.ToString(), fileItem.Status);
        Assert.Equal(tenantAData.Attachment.ScanStatus.ToString(), fileItem.ScanStatus);
        Assert.Equal(tenantAData.FileObject.UpdatedAt, fileItem.UpdatedAt);
        Assert.DoesNotContain(tenantBData.FileObject.Id, workspaceFileResult.Value.Items.Select(item => item.Id));

        var filteredFileResult = await search.SearchAsync(new SearchRequest(
            "securesearchneedle",
            SearchResultType.File,
            WorkspaceId: tenantAData.Workspace.Id,
            AuthorUserId: userA.Id,
            FromDate: now.AddDays(-1),
            PageSize: 50,
            FileKind: FileSearchKind.Pdf));
        Assert.True(filteredFileResult.IsSuccess, filteredFileResult.Error);
        Assert.Equal(1, filteredFileResult.Value!.TotalCount);
        Assert.Equal(tenantAData.FileObject.Id, Assert.Single(filteredFileResult.Value.Items).Id);

        var wrongKindResult = await search.SearchAsync(new SearchRequest(
            "securesearchneedle",
            SearchResultType.File,
            WorkspaceId: tenantAData.Workspace.Id,
            PageSize: 50,
            FileKind: FileSearchKind.Image));
        Assert.True(wrongKindResult.IsSuccess, wrongKindResult.Error);
        Assert.Empty(wrongKindResult.Value!.Items);
        Assert.Equal(0, wrongKindResult.Value.TotalCount);

        var invalidFacetType = await search.SearchAsync(new SearchRequest(
            "securesearchneedle",
            SearchResultType.Project,
            WorkspaceId: tenantAData.Workspace.Id,
            FileKind: FileSearchKind.Pdf));
        Assert.False(invalidFacetType.IsSuccess);

        var invalidFileKind = await search.SearchAsync(new SearchRequest(
            "securesearchneedle",
            SearchResultType.File,
            WorkspaceId: tenantAData.Workspace.Id,
            FileKind: (FileSearchKind)999));
        Assert.False(invalidFileKind.IsSuccess);

        foreach (var type in new[] { SearchResultType.Workspace, SearchResultType.Group, SearchResultType.Channel, SearchResultType.Post, SearchResultType.Message, SearchResultType.Project, SearchResultType.Task, SearchResultType.Artifact, SearchResultType.File })
        {
            var scopedResult = await search.SearchAsync(new SearchRequest("securesearchneedle", type, WorkspaceId: tenantBData.Workspace.Id, PageSize: 50));
            Assert.True(scopedResult.IsSuccess, scopedResult.Error);
            Assert.Empty(scopedResult.Value!.Items);
        }

        var membership = await dbContext.WorkspaceMembers.SingleAsync(member =>
            member.WorkspaceId == tenantAData.Workspace.Id && member.UserId == userA.Id);
        membership.Status = MembershipStatus.Suspended;
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var revokedFileResult = await search.SearchAsync(new SearchRequest(
            "securesearchneedle",
            SearchResultType.File,
            WorkspaceId: tenantAData.Workspace.Id,
            PageSize: 50));
        Assert.True(revokedFileResult.IsSuccess, revokedFileResult.Error);
        Assert.Empty(revokedFileResult.Value!.Items);
        Assert.Equal(0, revokedFileResult.Value.TotalCount);

        var revokedFilteredFileResult = await search.SearchAsync(new SearchRequest(
            "securesearchneedle",
            SearchResultType.File,
            WorkspaceId: tenantAData.Workspace.Id,
            AuthorUserId: userA.Id,
            FromDate: now.AddDays(-1),
            PageSize: 50,
            FileKind: FileSearchKind.Pdf));
        Assert.True(revokedFilteredFileResult.IsSuccess, revokedFilteredFileResult.Error);
        Assert.Empty(revokedFilteredFileResult.Value!.Items);
        Assert.Equal(0, revokedFilteredFileResult.Value.TotalCount);
    }

    private sealed record SearchGraph(
        Workspace Workspace,
        Group Group,
        Channel Channel,
        Post Post,
        Conversation Conversation,
        Message Message,
        Project Project,
        TaskItem Task,
        Artifact Artifact,
        FileObject FileObject,
        Attachment Attachment);

    private static async Task<SearchGraph> SeedSearchGraphAsync(AppDbContext dbContext, CurrentTenantService currentTenant, Tenant tenant, User user, string runId, string prefix, DateTimeOffset now)
    {
        currentTenant.SetTenant(tenant.Id, tenant.Slug);
        var workspace = new Workspace { TenantId = tenant.Id, Name = $"{prefix} SeCuReSearchNeedle Workspace", Slug = $"{prefix.ToLowerInvariant()}-ws-{runId}", Description = $"{prefix} workspace", CreatedByUserId = user.Id, CreatedAt = now };
        var group = new Group { TenantId = tenant.Id, WorkspaceId = workspace.Id, Name = $"{prefix} SeCuReSearchNeedle Group", Slug = $"{prefix.ToLowerInvariant()}-grp-{runId}", Description = $"{prefix} group", CreatedByUserId = user.Id, CreatedAt = now };
        var channel = new Channel { TenantId = tenant.Id, WorkspaceId = workspace.Id, GroupId = group.Id, Name = $"{prefix} SeCuReSearchNeedle Channel", Slug = $"{prefix.ToLowerInvariant()}-chn-{runId}", Description = $"{prefix} channel", CreatedByUserId = user.Id, Type = ChannelType.Public, Status = ChannelStatus.Active, CreatedAt = now };
        var project = new Project { TenantId = tenant.Id, WorkspaceId = workspace.Id, GroupId = group.Id, Name = $"{prefix} SeCuReSearchNeedle Project", Slug = $"{prefix.ToLowerInvariant()}-prj-{runId}", Description = $"{prefix} project", OwnerUserId = user.Id, CreatedByUserId = user.Id, Status = ProjectStatus.Active, CreatedAt = now };
        var task = new TaskItem { TenantId = tenant.Id, WorkspaceId = workspace.Id, ProjectId = project.Id, Title = $"{prefix} SeCuReSearchNeedle Task", Description = $"{prefix} task", CreatedByUserId = user.Id, Status = TaskItemStatus.NotStarted, CreatedAt = now };
        var artifact = new Artifact { TenantId = tenant.Id, ProjectId = project.Id, TaskItemId = task.Id, Name = $"{prefix} SeCuReSearchNeedle Artifact", Description = $"{prefix} artifact", CreatedByUserId = user.Id, ArtifactType = ArtifactType.Other, Status = ArtifactStatus.Draft, CreatedAt = now };
        var conversation = new Conversation { TenantId = tenant.Id, WorkspaceId = workspace.Id, Title = $"{prefix} conversation", CreatedByUserId = user.Id, Type = ConversationType.DirectMessage, CreatedAt = now };
        var post = new Post { TenantId = tenant.Id, ChannelId = channel.Id, AuthorUserId = user.Id, Body = $"{prefix} SeCuReSearchNeedle post body", CreatedAt = now };
        var message = new Message { TenantId = tenant.Id, WorkspaceId = workspace.Id, ConversationId = conversation.Id, AuthorUserId = user.Id, Body = $"{prefix} SeCuReSearchNeedle message body", CreatedAt = now };
        var fileObject = new FileObject
        {
            TenantId = tenant.Id,
            WorkspaceId = workspace.Id,
            UploadedByUserId = user.Id,
            OriginalFileName = $"{prefix}-SeCuReSearchNeedle-evidence.pdf",
            StorageKey = $"search/{runId}/{prefix.ToLowerInvariant()}-evidence.pdf",
            ContentType = "application/pdf",
            SizeBytes = 256,
            Status = FileObjectStatus.Active,
            CreatedAt = now
        };
        var attachment = new Attachment
        {
            TenantId = tenant.Id,
            FileObjectId = fileObject.Id,
            WorkspaceId = workspace.Id,
            OwnerType = AttachmentOwnerType.Workspace,
            OwnerId = workspace.Id,
            OwnerUserId = user.Id,
            UploadedByUserId = user.Id,
            FileName = fileObject.OriginalFileName,
            StoredFileName = $"{fileObject.Id:D}.pdf",
            FilePath = $"/search/{fileObject.Id:D}.pdf",
            ContentType = fileObject.ContentType,
            Extension = ".pdf",
            SizeBytes = fileObject.SizeBytes,
            StorageProvider = "test",
            StorageKey = fileObject.StorageKey,
            CreatedAt = now
        };

        dbContext.TenantUsers.Add(new TenantUser { TenantId = tenant.Id, UserId = user.Id, Role = TenantUserRole.Member, Status = TenantUserStatus.Active, JoinedAt = now });
        dbContext.Workspaces.Add(workspace);
        dbContext.WorkspaceMembers.Add(new WorkspaceMember { TenantId = tenant.Id, WorkspaceId = workspace.Id, UserId = user.Id, Role = WorkspaceRole.Owner, Status = MembershipStatus.Active, JoinedAt = now });
        dbContext.Groups.Add(group);
        dbContext.GroupMembers.Add(new GroupMember { TenantId = tenant.Id, GroupId = group.Id, UserId = user.Id, Role = GroupRole.Member, JoinedAt = now });
        dbContext.Channels.Add(channel);
        dbContext.ChannelMembers.Add(new ChannelMember { TenantId = tenant.Id, ChannelId = channel.Id, UserId = user.Id, Role = ChannelRole.Admin, JoinedAt = now });
        dbContext.Projects.Add(project);
        dbContext.ProjectMembers.Add(new ProjectMember { TenantId = tenant.Id, ProjectId = project.Id, UserId = user.Id, Role = ProjectRole.Owner, JoinedAt = now });
        dbContext.TaskItems.Add(task);
        dbContext.Artifacts.Add(artifact);
        dbContext.Conversations.Add(conversation);
        dbContext.ConversationMembers.Add(new ConversationMember { TenantId = tenant.Id, ConversationId = conversation.Id, UserId = user.Id, Role = ConversationMemberRole.Admin, JoinedAt = now });
        dbContext.Posts.Add(post);
        dbContext.Messages.Add(message);
        dbContext.FileObjects.Add(fileObject);
        dbContext.Attachments.Add(attachment);
        await dbContext.SaveChangesAsync();
        return new SearchGraph(workspace, group, channel, post, conversation, message, project, task, artifact, fileObject, attachment);
    }

    private static User NewUser(string email, string displayName) => new()
    {
        DisplayName = displayName,
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        PasswordHash = "hash",
        SystemRole = SystemRole.User,
        Status = UserStatus.Active
    };

    private static void AssertSearchContains(IReadOnlyCollection<SearchResultItemResponse> items, SearchResultType type, Guid id, string message) =>
        Assert.True(items.Any(item => item.Type == type && item.Id == id), message);

    private static void AssertSearchDoesNotContain(IReadOnlyCollection<SearchResultItemResponse> items, SearchResultType type, Guid id, string message) =>
        Assert.False(items.Any(item => item.Type == type && item.Id == id), message);

    private sealed class TestCurrentUser(User user) : ICurrentUser
    {
        public Guid? UserId => user.Id;
        public Guid? SessionId => null;
        public string? Email => user.Email;
        public SystemRole? SystemRole => user.SystemRole;
        public bool IsAuthenticated => true;
    }
}
