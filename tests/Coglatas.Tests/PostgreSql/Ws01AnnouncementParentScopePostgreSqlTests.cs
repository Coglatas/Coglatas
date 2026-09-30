using Coglatas.Application.Announcements;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Search;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Tests.PostgreSql;

[Trait("Scope", "WS01BE")]
public sealed class Ws01AnnouncementParentScopePostgreSqlTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 23, 0, 0, 0, TimeSpan.Zero);

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task WorkspaceRevocationInvalidatesStaleGroupAndChannelAnnouncementAccessEverywhere()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async testConnectionString =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(testConnectionString);

            var tenantScope = new CurrentTenantService();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(testConnectionString)
                .Options;
            await using var dbContext = new AppDbContext(options, tenantScope);

            tenantScope.SetPlatformScope();
            var tenant = NewTenant();
            var actor = NewUser("actor");
            var publisher = NewUser("publisher");
            dbContext.Tenants.Add(tenant);
            dbContext.Users.AddRange(actor, publisher);
            await dbContext.SaveChangesAsync();

            dbContext.TenantUsers.AddRange(
                NewTenantUser(tenant.Id, actor.Id),
                NewTenantUser(tenant.Id, publisher.Id));

            var workspace = NewWorkspace(tenant.Id, publisher.Id);
            dbContext.Workspaces.Add(workspace);
            var actorWorkspaceMembership = NewWorkspaceMember(
                tenant.Id,
                workspace.Id,
                actor.Id,
                WorkspaceRole.Member);
            dbContext.WorkspaceMembers.AddRange(
                actorWorkspaceMembership,
                NewWorkspaceMember(
                    tenant.Id,
                    workspace.Id,
                    publisher.Id,
                    WorkspaceRole.Owner));

            var group = NewGroup(tenant.Id, workspace.Id, publisher.Id);
            dbContext.Groups.Add(group);
            dbContext.GroupMembers.AddRange(
                NewGroupMember(tenant.Id, group.Id, actor.Id),
                NewGroupMember(tenant.Id, group.Id, publisher.Id));

            var channel = NewPrivateChannel(
                tenant.Id,
                workspace.Id,
                group.Id,
                publisher.Id);
            dbContext.Channels.Add(channel);
            dbContext.ChannelMembers.AddRange(
                NewChannelMember(tenant.Id, channel.Id, actor.Id),
                NewChannelMember(tenant.Id, channel.Id, publisher.Id));

            var groupAnnouncement = NewAnnouncement(
                tenant.Id,
                workspace.Id,
                group.Id,
                null,
                publisher.Id,
                "stale-parent-scope group");
            var channelAnnouncement = NewAnnouncement(
                tenant.Id,
                workspace.Id,
                group.Id,
                channel.Id,
                publisher.Id,
                "stale-parent-scope channel");
            dbContext.Announcements.AddRange(groupAnnouncement, channelAnnouncement);
            await dbContext.SaveChangesAsync();

            tenantScope.SetTenant(tenant.Id, tenant.Slug);
            dbContext.ChangeTracker.Clear();

            var clock = new TestClock();
            var repository = new AnnouncementRepository(dbContext, clock, tenantScope);
            var messaging = new MessagingRepository(dbContext);
            var dashboard = new WorkspaceDashboardQuery(dbContext, messaging, clock);
            var search = new DbSearchService(
                dbContext,
                new TestCurrentUser(actor),
                messaging);

            var beforePage = await repository.ListVisibleAsync(
                actor.Id,
                isSystemAdmin: false,
                new AnnouncementListQuery(PageSize: 20, WorkspaceId: workspace.Id));
            Assert.Contains(beforePage.Items, item => item.Id == groupAnnouncement.Id);
            Assert.Contains(beforePage.Items, item => item.Id == channelAnnouncement.Id);
            Assert.True(await repository.IsVisibleToUserAsync(
                groupAnnouncement.Id,
                actor.Id,
                isSystemAdmin: false));
            Assert.True(await repository.IsVisibleToUserAsync(
                channelAnnouncement.Id,
                actor.Id,
                isSystemAdmin: false));

            var beforeSearch = await search.SearchAsync(new SearchRequest(
                Q: "stale-parent-scope",
                Type: SearchResultType.Announcement,
                WorkspaceId: workspace.Id,
                PageSize: 20));
            Assert.True(beforeSearch.IsSuccess, beforeSearch.Error);
            Assert.Contains(beforeSearch.Value!.Items, item => item.Id == groupAnnouncement.Id);
            Assert.Contains(beforeSearch.Value.Items, item => item.Id == channelAnnouncement.Id);

            var beforeDashboard = await dashboard.ListAsync(actor.Id);
            var beforeCard = Assert.Single(beforeDashboard);
            Assert.Equal(2, beforeCard.UnreadAnnouncementCount);

            Assert.Contains(
                await repository.ListTargetUsersAsync(groupAnnouncement),
                target => target.UserId == actor.Id);
            Assert.Contains(
                await repository.ListTargetUsersAsync(channelAnnouncement),
                target => target.UserId == actor.Id);

            var trackedMembership = await dbContext.WorkspaceMembers.SingleAsync(member =>
                member.Id == actorWorkspaceMembership.Id);
            trackedMembership.Status = MembershipStatus.Suspended;
            await dbContext.SaveChangesAsync();
            dbContext.ChangeTracker.Clear();

            // Deliberately retain the stale child-scope rows. Losing the parent
            // Workspace membership must be sufficient to revoke every live
            // Announcement read/search/dashboard/audience path.
            Assert.True(await dbContext.GroupMembers.AnyAsync(member =>
                member.GroupId == group.Id && member.UserId == actor.Id));
            Assert.True(await dbContext.ChannelMembers.AnyAsync(member =>
                member.ChannelId == channel.Id && member.UserId == actor.Id));

            var afterPage = await repository.ListVisibleAsync(
                actor.Id,
                isSystemAdmin: false,
                new AnnouncementListQuery(PageSize: 20, WorkspaceId: workspace.Id));
            Assert.DoesNotContain(afterPage.Items, item => item.Id == groupAnnouncement.Id);
            Assert.DoesNotContain(afterPage.Items, item => item.Id == channelAnnouncement.Id);
            Assert.False(await repository.IsVisibleToUserAsync(
                groupAnnouncement.Id,
                actor.Id,
                isSystemAdmin: false));
            Assert.False(await repository.IsVisibleToUserAsync(
                channelAnnouncement.Id,
                actor.Id,
                isSystemAdmin: false));

            var afterSearch = await search.SearchAsync(new SearchRequest(
                Q: "stale-parent-scope",
                Type: SearchResultType.Announcement,
                WorkspaceId: workspace.Id,
                PageSize: 20));
            Assert.True(afterSearch.IsSuccess, afterSearch.Error);
            Assert.DoesNotContain(afterSearch.Value!.Items, item => item.Id == groupAnnouncement.Id);
            Assert.DoesNotContain(afterSearch.Value.Items, item => item.Id == channelAnnouncement.Id);

            Assert.Empty(await dashboard.ListAsync(actor.Id));

            var groupTargets = await repository.ListTargetUsersAsync(groupAnnouncement);
            Assert.DoesNotContain(groupTargets, target => target.UserId == actor.Id);
            Assert.Contains(groupTargets, target => target.UserId == publisher.Id);

            var channelTargets = await repository.ListTargetUsersAsync(channelAnnouncement);
            Assert.DoesNotContain(channelTargets, target => target.UserId == actor.Id);
            Assert.Contains(channelTargets, target => target.UserId == publisher.Id);
        });
    }

    private static Tenant NewTenant() => new()
    {
        Name = "WS01 announcement parent scope",
        DisplayName = "WS01 announcement parent scope",
        Slug = $"ws01-announcement-parent-{Guid.NewGuid():N}",
        Status = TenantStatus.Active,
        CreatedAt = Now
    };

    private static User NewUser(string prefix)
    {
        var email = $"{prefix}-{Guid.NewGuid():N}@example.test";
        return new User
        {
            DisplayName = prefix,
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            PasswordHash = "test-hash",
            Status = UserStatus.Active,
            SystemRole = SystemRole.NormalUser,
            CreatedAt = Now
        };
    }

    private static TenantUser NewTenantUser(Guid tenantId, Guid userId) => new()
    {
        TenantId = tenantId,
        UserId = userId,
        Role = TenantUserRole.Member,
        Status = TenantUserStatus.Active,
        JoinedAt = Now,
        CreatedAt = Now
    };

    private static Workspace NewWorkspace(Guid tenantId, Guid creatorId) => new()
    {
        TenantId = tenantId,
        Name = "WS01 Parent Workspace",
        Slug = $"ws01-parent-{Guid.NewGuid():N}",
        Status = WorkspaceStatus.Active,
        CreatedByUserId = creatorId,
        CreatedAt = Now
    };

    private static WorkspaceMember NewWorkspaceMember(
        Guid tenantId,
        Guid workspaceId,
        Guid userId,
        WorkspaceRole role) => new()
        {
            TenantId = tenantId,
            WorkspaceId = workspaceId,
            UserId = userId,
            Role = role,
            Status = MembershipStatus.Active,
            JoinedAt = Now,
            CreatedAt = Now
        };

    private static Group NewGroup(Guid tenantId, Guid workspaceId, Guid creatorId) => new()
    {
        TenantId = tenantId,
        WorkspaceId = workspaceId,
        Name = "WS01 Parent Group",
        Slug = $"ws01-group-{Guid.NewGuid():N}",
        GroupType = GroupType.Committee,
        Status = GroupStatus.Active,
        CreatedByUserId = creatorId,
        CreatedAt = Now
    };

    private static GroupMember NewGroupMember(Guid tenantId, Guid groupId, Guid userId) => new()
    {
        TenantId = tenantId,
        GroupId = groupId,
        UserId = userId,
        Role = GroupRole.Member,
        JoinedAt = Now,
        CreatedAt = Now
    };

    private static Channel NewPrivateChannel(
        Guid tenantId,
        Guid workspaceId,
        Guid groupId,
        Guid creatorId) => new()
        {
            TenantId = tenantId,
            WorkspaceId = workspaceId,
            GroupId = groupId,
            Name = "WS01 Private Channel",
            Slug = $"ws01-private-{Guid.NewGuid():N}",
            Type = ChannelType.Private,
            Status = ChannelStatus.Active,
            CreatedByUserId = creatorId,
            CreatedAt = Now
        };

    private static ChannelMember NewChannelMember(Guid tenantId, Guid channelId, Guid userId) => new()
    {
        TenantId = tenantId,
        ChannelId = channelId,
        UserId = userId,
        Role = ChannelRole.Member,
        JoinedAt = Now,
        CreatedAt = Now
    };

    private static Announcement NewAnnouncement(
        Guid tenantId,
        Guid workspaceId,
        Guid groupId,
        Guid? channelId,
        Guid authorId,
        string title) => new()
        {
            TenantId = tenantId,
            WorkspaceId = workspaceId,
            GroupId = groupId,
            ChannelId = channelId,
            AuthorUserId = authorId,
            Title = title,
            Body = $"{title} body",
            PublishedAt = Now.AddMinutes(-1),
            CreatedAt = Now.AddMinutes(-1)
        };

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class TestCurrentUser(User user) : ICurrentUser
    {
        public Guid? UserId => user.Id;
        public Guid? SessionId => Guid.NewGuid();
        public string Email => user.Email;
        public SystemRole? SystemRole => user.SystemRole;
        public bool IsAuthenticated => true;
    }
}
