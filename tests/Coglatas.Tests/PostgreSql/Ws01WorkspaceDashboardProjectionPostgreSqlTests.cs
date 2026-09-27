using System.Data.Common;
using Coglatas.Application.Announcements;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Search;
using Coglatas.Application.Tenancy;
using Coglatas.Application.Workspaces;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Coglatas.Tests.PostgreSql;

[Trait("Scope", "WS01BE")]
public sealed class Ws01WorkspaceDashboardProjectionPostgreSqlTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 22, 3, 0, 0, TimeSpan.Zero);

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task ProjectionIsCanonicalTenantSafeAndBounded()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async testConnectionString =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(testConnectionString);
            var tenantScope = new CurrentTenantService();
            var commandCounter = new CommandCounterInterceptor();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(testConnectionString)
                .AddInterceptors(commandCounter)
                .Options;

            await using var dbContext = new AppDbContext(options, tenantScope);
            var graph = await SeedGraphAsync(dbContext, tenantScope);
            tenantScope.SetTenant(graph.TenantA.Id, graph.TenantA.Slug);
            dbContext.ChangeTracker.Clear();

            var clock = new TestClock();
            var dashboard = new WorkspaceDashboardQuery(
                dbContext,
                new MessagingRepository(dbContext),
                clock);
            var workspaceService = new WorkspaceService(
                null!,
                null!,
                null!,
                new TestCurrentUser(graph.Actor),
                clock,
                null!,
                null!,
                tenantScope,
                dashboardQuery: dashboard);

            var persistedReadState = await dbContext.ReadStates
                .AsNoTracking()
                .SingleAsync(readState => readState.Id == graph.ReadConversationState.Id);
            var persistedReadMessage = await dbContext.Messages
                .AsNoTracking()
                .SingleAsync(message => message.Id == graph.ReadConversationMessage.Id);
            var persistedReadMember = await dbContext.ConversationMembers
                .AsNoTracking()
                .SingleAsync(member => member.Id == graph.ReadConversationMember.Id);
            Assert.Equal(ReadScopeType.Conversation, persistedReadState.ScopeType);
            Assert.Equal(graph.ReadConversation.Id, persistedReadState.ScopeId);
            Assert.Equal(graph.ReadConversation.Id, persistedReadState.ConversationId);
            Assert.Equal(persistedReadMessage.Id, persistedReadState.LastReadItemId);
            Assert.Equal(persistedReadMessage.Id, persistedReadState.LastReadMessageId);
            Assert.Equal(persistedReadMessage.CreatedAt.UtcTicks, persistedReadState.LastReadSequence);
            Assert.True(persistedReadState.LastReadAt >= persistedReadMessage.CreatedAt);
            Assert.Equal(persistedReadState.LastReadMessageId, persistedReadMember.LastReadMessageId);
            Assert.Equal(persistedReadState.LastReadAt, persistedReadMember.LastReadAt);

            commandCounter.Begin();
            var actorResult = await workspaceService.ListAsync();
            var actorCommands = commandCounter.End();
            Assert.True(actorResult.IsSuccess, actorResult.Error);
            var actorItems = actorResult.Value!;

            Assert.Equal(5, actorItems.Count);
            Assert.Equal(
                new WorkspaceRole?[]
                {
                    WorkspaceRole.Owner,
                    WorkspaceRole.Admin,
                    WorkspaceRole.Adviser,
                    WorkspaceRole.Member,
                    WorkspaceRole.ReadOnly
                },
                actorItems.Select(item => item.CurrentUserRole));
            Assert.All(actorItems, item =>
            {
                Assert.Equal(WorkspaceDashboardAccessSource.WorkspaceMembership, item.AccessSource);
                Assert.True(item.CanOpenWorkspace);
                Assert.True(item.CanOpenMembers);
                Assert.True(item.CanOpenProjects);
                Assert.Equal(item.UpdatedAt, item.UpdatedAt.ToUniversalTime());
                Assert.Equal(
                    item.RunningProjectCount + item.NeedsReviewProjectCount,
                    item.InProgressProjectCount);
            });
            Assert.Equal(
                new[] { true, true, false, true, false },
                actorItems.Select(item => item.CanCreateProject));
            Assert.Equal(
                new[] { true, true, true, true, false },
                actorItems.Select(item => item.CanOpenProjectCreate));
            Assert.Equal(
                new[] { true, true, true, true, false },
                actorItems.Select(item => item.CanAddFiles));
            Assert.DoesNotContain(actorItems, item => item.Id == graph.ArchivedWorkspace.Id);
            Assert.DoesNotContain(actorItems, item => item.Id == graph.TenantBWorkspace.Id);

            var readableConversationIds = new MessagingRepository(dbContext)
                .QueryReadableConversationIds(graph.Actor.Id)!;
            var messages = dbContext.Messages;
            var readStates = dbContext.ReadStates;
            Assert.True(await readableConversationIds.ContainsAsync(graph.OwnMessageConversation.Id));
            var unreadConversationTitles = await dbContext.Conversations
                .Where(conversation =>
                    conversation.WorkspaceId == graph.OwnerWorkspace.Id &&
                    readableConversationIds.Contains(conversation.Id) &&
                    messages.Any(message =>
                        message.ConversationId == conversation.Id &&
                        message.AuthorUserId != graph.Actor.Id &&
                        message.DeletedAt == null &&
                        (!readStates.Any(readState =>
                             readState.ConversationId == conversation.Id &&
                             readState.UserId == graph.Actor.Id) ||
                         readStates.Any(readState =>
                             readState.ConversationId == conversation.Id &&
                             readState.UserId == graph.Actor.Id &&
                             message.CreatedAt > readState.LastReadAt))))
                .Select(conversation => conversation.Title)
                .OrderBy(title => title)
                .ToListAsync();
            Assert.Equal(
                new[] { "Private visible title", "Unread Workspace Conversation" },
                unreadConversationTitles);
            Assert.DoesNotContain("Read Workspace Conversation", unreadConversationTitles);
            Assert.DoesNotContain("Own Message Only Conversation", unreadConversationTitles);

            var ownerCard = Assert.Single(actorItems, item => item.Id == graph.OwnerWorkspace.Id);
            Assert.Equal(2, ownerCard.UnreadAnnouncementCount);
            Assert.Equal(2, ownerCard.UnreadConversationCount);
            Assert.Equal(3, ownerCard.InProgressProjectCount);
            Assert.Equal(2, ownerCard.RunningProjectCount);
            Assert.Equal(1, ownerCard.NeedsReviewProjectCount);
            Assert.Equal(graph.OwnerWorkspace.UpdatedAt, ownerCard.UpdatedAt);

            var zeroCard = Assert.Single(actorItems, item => item.Id == graph.AdminWorkspace.Id);
            Assert.Equal(0, zeroCard.UnreadAnnouncementCount);
            Assert.Equal(0, zeroCard.UnreadConversationCount);
            Assert.Equal(0, zeroCard.InProgressProjectCount);
            Assert.Equal(0, zeroCard.RunningProjectCount);
            Assert.Equal(0, zeroCard.NeedsReviewProjectCount);
            Assert.InRange(
                (graph.AdminWorkspace.CreatedAt - zeroCard.UpdatedAt).Duration(),
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(1));

            var workspaceAuthorization = new WorkspaceAuthorizationService(
                new UserRepository(dbContext),
                new WorkspaceRepository(dbContext),
                new TenantAuthorizationService(new TenantRepository(dbContext)));
            Assert.True(await workspaceAuthorization.CanViewWorkspace(
                graph.Actor.Id,
                graph.ReadOnlyWorkspace.Id));
            Assert.False(await workspaceAuthorization.CanManageWorkspace(
                graph.Actor.Id,
                graph.ReadOnlyWorkspace.Id));
            Assert.True(Assert.Single(
                actorItems,
                item => item.Id == graph.ReadOnlyWorkspace.Id).CanOpenMembers);

            Assert.Equal(4, actorCommands.Count);
            Assert.DoesNotContain(actorCommands, command => command.Contains("\"Body\"", StringComparison.Ordinal));
            Assert.DoesNotContain(actorCommands, command => command.Contains("\"Title\"", StringComparison.Ordinal));

            commandCounter.Begin();
            var singleWorkspaceItems = await dashboard.ListAsync(graph.SingleWorkspaceUser.Id);
            var singleWorkspaceCommands = commandCounter.End();
            Assert.Single(singleWorkspaceItems);
            Assert.Equal(4, singleWorkspaceCommands.Count);

            commandCounter.Begin();
            var systemAdminItems = await dashboard.ListAsync(graph.SystemAdmin.Id);
            var systemAdminCommands = commandCounter.End();
            Assert.Equal(5, systemAdminItems.Count);
            Assert.Equal(4, systemAdminCommands.Count);

            var systemAdminOnlyCard = Assert.Single(
                systemAdminItems,
                item => item.Id == graph.OwnerWorkspace.Id);
            Assert.Null(systemAdminOnlyCard.CurrentUserRole);
            Assert.Equal(WorkspaceDashboardAccessSource.SystemAdmin, systemAdminOnlyCard.AccessSource);
            Assert.Equal(5, systemAdminOnlyCard.UnreadAnnouncementCount);
            Assert.Equal(2, systemAdminOnlyCard.InProgressProjectCount);
            Assert.Equal(1, systemAdminOnlyCard.RunningProjectCount);
            Assert.Equal(1, systemAdminOnlyCard.NeedsReviewProjectCount);
            Assert.Equal(0, systemAdminOnlyCard.UnreadConversationCount);
            Assert.False(systemAdminOnlyCard.CanCreateProject);
            Assert.False(systemAdminOnlyCard.CanOpenProjectCreate);
            Assert.True(systemAdminOnlyCard.CanAddFiles);

            var systemAdminMembershipCard = Assert.Single(
                systemAdminItems,
                item => item.Id == graph.AdminWorkspace.Id);
            Assert.Equal(WorkspaceRole.ReadOnly, systemAdminMembershipCard.CurrentUserRole);
            Assert.Equal(
                WorkspaceDashboardAccessSource.WorkspaceMembership,
                systemAdminMembershipCard.AccessSource);
            Assert.False(systemAdminMembershipCard.CanCreateProject);
            Assert.False(systemAdminMembershipCard.CanOpenProjectCreate);
            Assert.True(systemAdminMembershipCard.CanAddFiles);

            var revokedItems = await dashboard.ListAsync(graph.RevokedUser.Id);
            Assert.Empty(revokedItems);

            var membersOnlyProjectMember = await dbContext.ProjectMembers.SingleAsync(member =>
                member.Id == graph.MembersOnlyProjectMember.Id);
            dbContext.ProjectMembers.Remove(membersOnlyProjectMember);
            await dbContext.SaveChangesAsync();
            dbContext.ChangeTracker.Clear();

            var afterProjectRevocation = await dashboard.ListAsync(graph.Actor.Id);
            var afterProjectRevocationOwnerCard = Assert.Single(
                afterProjectRevocation,
                item => item.Id == graph.OwnerWorkspace.Id);
            Assert.Equal(2, afterProjectRevocationOwnerCard.InProgressProjectCount);
            Assert.Equal(1, afterProjectRevocationOwnerCard.RunningProjectCount);
            Assert.Equal(1, afterProjectRevocationOwnerCard.NeedsReviewProjectCount);

            var ownerMembership = await dbContext.WorkspaceMembers.SingleAsync(member =>
                member.WorkspaceId == graph.OwnerWorkspace.Id &&
                member.UserId == graph.Actor.Id);
            ownerMembership.Status = MembershipStatus.Suspended;
            await dbContext.SaveChangesAsync();
            dbContext.ChangeTracker.Clear();

            var afterWorkspaceRevocation = await dashboard.ListAsync(graph.Actor.Id);
            Assert.DoesNotContain(afterWorkspaceRevocation, item => item.Id == graph.OwnerWorkspace.Id);

            var systemAdminAfterRevocation = await dashboard.ListAsync(graph.SystemAdmin.Id);
            Assert.Contains(systemAdminAfterRevocation, item =>
                item.Id == graph.OwnerWorkspace.Id &&
                item.CurrentUserRole == null &&
                item.AccessSource == WorkspaceDashboardAccessSource.SystemAdmin);
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task AnnouncementListDetailSearchAndDashboardShareFailClosedAudienceScope()
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
            var graph = await SeedGraphAsync(dbContext, tenantScope);
            tenantScope.SetTenant(graph.TenantA.Id, graph.TenantA.Slug);
            dbContext.ChangeTracker.Clear();

            var repository = new AnnouncementRepository(dbContext, new TestClock(), tenantScope);
            var page = await repository.ListVisibleAsync(
                graph.Actor.Id,
                isSystemAdmin: false,
                new AnnouncementListQuery(WorkspaceId: graph.OwnerWorkspace.Id, PageSize: 100));

            Assert.Contains(page.Items, item => item.Id == graph.VisibleWorkspaceAnnouncement.Id);
            Assert.Contains(page.Items, item => item.Id == graph.VisibleGroupAnnouncement.Id);
            Assert.DoesNotContain(page.Items, item => item.Id == graph.HiddenGroupAnnouncement.Id);
            Assert.DoesNotContain(page.Items, item => item.Id == graph.HiddenPrivateChannelAnnouncement.Id);
            Assert.False(await repository.IsVisibleToUserAsync(
                graph.HiddenGroupAnnouncement.Id,
                graph.Actor.Id,
                isSystemAdmin: false));
            Assert.False(await repository.IsVisibleToUserAsync(
                graph.HiddenPrivateChannelAnnouncement.Id,
                graph.Actor.Id,
                isSystemAdmin: false));

            var search = new DbSearchService(
                dbContext,
                new TestCurrentUser(graph.Actor),
                new MessagingRepository(dbContext));
            var searchResult = await search.SearchAsync(new SearchRequest(
                Q: "audience-scope-needle",
                Type: SearchResultType.Announcement,
                WorkspaceId: graph.OwnerWorkspace.Id,
                PageSize: 50));

            Assert.True(searchResult.IsSuccess, searchResult.Error);
            Assert.Contains(searchResult.Value!.Items, item => item.Id == graph.VisibleGroupAnnouncement.Id);
            Assert.DoesNotContain(searchResult.Value.Items, item => item.Id == graph.HiddenGroupAnnouncement.Id);
            Assert.DoesNotContain(searchResult.Value.Items, item => item.Id == graph.HiddenPrivateChannelAnnouncement.Id);
        });
    }

    private static async Task<DashboardGraph> SeedGraphAsync(
        AppDbContext dbContext,
        CurrentTenantService tenantScope)
    {
        var tenantA = NewTenant("ws01-a");
        var tenantB = NewTenant("ws01-b");
        var actor = NewUser("actor", SystemRole.NormalUser);
        var other = NewUser("other", SystemRole.NormalUser);
        var singleWorkspaceUser = NewUser("single", SystemRole.NormalUser);
        var revokedUser = NewUser("revoked", SystemRole.NormalUser);
        var systemAdmin = NewUser("system-admin", SystemRole.SystemAdmin);

        tenantScope.SetPlatformScope();
        dbContext.Tenants.AddRange(tenantA, tenantB);
        dbContext.Users.AddRange(actor, other, singleWorkspaceUser, revokedUser, systemAdmin);
        await dbContext.SaveChangesAsync();

        dbContext.TenantUsers.AddRange(
            NewTenantUser(tenantA.Id, actor.Id, TenantUserRole.Owner),
            NewTenantUser(tenantA.Id, other.Id, TenantUserRole.Member),
            NewTenantUser(tenantA.Id, singleWorkspaceUser.Id, TenantUserRole.Member),
            NewTenantUser(tenantA.Id, revokedUser.Id, TenantUserRole.Member),
            NewTenantUser(tenantA.Id, systemAdmin.Id, TenantUserRole.Member),
            NewTenantUser(tenantB.Id, actor.Id, TenantUserRole.Member),
            NewTenantUser(tenantB.Id, other.Id, TenantUserRole.Owner));

        var ownerWorkspace = NewWorkspace(tenantA.Id, actor.Id, "01 Owner", updated: true);
        var adminWorkspace = NewWorkspace(tenantA.Id, actor.Id, "02 Admin");
        var adviserWorkspace = NewWorkspace(tenantA.Id, actor.Id, "03 Adviser");
        var memberWorkspace = NewWorkspace(tenantA.Id, actor.Id, "04 Member");
        var readOnlyWorkspace = NewWorkspace(tenantA.Id, actor.Id, "05 ReadOnly");
        var archivedWorkspace = NewWorkspace(tenantA.Id, actor.Id, "06 Archived");
        archivedWorkspace.Status = WorkspaceStatus.Archived;
        var tenantBWorkspace = NewWorkspace(tenantB.Id, other.Id, "Tenant B Workspace");
        dbContext.Workspaces.AddRange(
            ownerWorkspace,
            adminWorkspace,
            adviserWorkspace,
            memberWorkspace,
            readOnlyWorkspace,
            archivedWorkspace,
            tenantBWorkspace);

        dbContext.WorkspaceMembers.AddRange(
            NewWorkspaceMember(tenantA.Id, ownerWorkspace.Id, actor.Id, WorkspaceRole.Owner),
            NewWorkspaceMember(tenantA.Id, adminWorkspace.Id, actor.Id, WorkspaceRole.Admin),
            NewWorkspaceMember(tenantA.Id, adviserWorkspace.Id, actor.Id, WorkspaceRole.Adviser),
            NewWorkspaceMember(tenantA.Id, memberWorkspace.Id, actor.Id, WorkspaceRole.Member),
            NewWorkspaceMember(tenantA.Id, readOnlyWorkspace.Id, actor.Id, WorkspaceRole.ReadOnly),
            NewWorkspaceMember(tenantA.Id, archivedWorkspace.Id, actor.Id, WorkspaceRole.Owner),
            NewWorkspaceMember(tenantA.Id, ownerWorkspace.Id, other.Id, WorkspaceRole.Member),
            NewWorkspaceMember(tenantA.Id, ownerWorkspace.Id, singleWorkspaceUser.Id, WorkspaceRole.ReadOnly),
            NewWorkspaceMember(
                tenantA.Id,
                ownerWorkspace.Id,
                revokedUser.Id,
                WorkspaceRole.Member,
                MembershipStatus.Suspended),
            NewWorkspaceMember(tenantA.Id, adminWorkspace.Id, systemAdmin.Id, WorkspaceRole.ReadOnly),
            NewWorkspaceMember(tenantB.Id, tenantBWorkspace.Id, actor.Id, WorkspaceRole.Member),
            NewWorkspaceMember(tenantB.Id, tenantBWorkspace.Id, other.Id, WorkspaceRole.Owner));
        dbContext.Set<CapabilityGrant>().Add(new CapabilityGrant
        {
            TenantId = tenantA.Id,
            SubjectUserId = actor.Id,
            CapabilityKey = CapabilityKeys.ProjectCreate,
            ScopeType = CapabilityScopeType.Workspace,
            ScopeId = memberWorkspace.Id,
            GrantedByUserId = actor.Id,
            GrantedAt = Now.AddHours(-1),
            VersionNo = 1
        });

        var visibleGroup = NewGroup(tenantA.Id, ownerWorkspace.Id, actor.Id, "Visible Group");
        var hiddenGroup = NewGroup(tenantA.Id, ownerWorkspace.Id, other.Id, "Hidden Group");
        var managedAdviserGroup = NewGroup(tenantA.Id, adviserWorkspace.Id, actor.Id, "Managed Adviser Group");
        dbContext.Groups.AddRange(visibleGroup, hiddenGroup, managedAdviserGroup);
        dbContext.GroupMembers.AddRange(
            NewGroupMember(tenantA.Id, visibleGroup.Id, actor.Id),
            NewGroupMember(tenantA.Id, visibleGroup.Id, other.Id),
            NewGroupMember(tenantA.Id, hiddenGroup.Id, other.Id),
            NewGroupMember(tenantA.Id, managedAdviserGroup.Id, actor.Id, GroupRole.Owner));

        var privateChannel = NewChannel(
            tenantA.Id,
            ownerWorkspace.Id,
            hiddenGroup.Id,
            other.Id,
            "Private Channel",
            ChannelType.Private);
        dbContext.Channels.Add(privateChannel);
        dbContext.ChannelMembers.Add(NewChannelMember(tenantA.Id, privateChannel.Id, other.Id));

        var visibleWorkspaceAnnouncement = NewAnnouncement(
            tenantA.Id,
            ownerWorkspace.Id,
            null,
            null,
            actor.Id,
            "Visible Workspace Announcement");
        var readWorkspaceAnnouncement = NewAnnouncement(
            tenantA.Id,
            ownerWorkspace.Id,
            null,
            null,
            actor.Id,
            "Read Workspace Announcement");
        var visibleGroupAnnouncement = NewAnnouncement(
            tenantA.Id,
            ownerWorkspace.Id,
            visibleGroup.Id,
            null,
            other.Id,
            "audience-scope-needle visible group");
        var hiddenGroupAnnouncement = NewAnnouncement(
            tenantA.Id,
            ownerWorkspace.Id,
            hiddenGroup.Id,
            null,
            other.Id,
            "audience-scope-needle hidden group");
        var hiddenPrivateChannelAnnouncement = NewAnnouncement(
            tenantA.Id,
            ownerWorkspace.Id,
            hiddenGroup.Id,
            privateChannel.Id,
            other.Id,
            "audience-scope-needle hidden private channel");
        var expiredAnnouncement = NewAnnouncement(
            tenantA.Id,
            ownerWorkspace.Id,
            null,
            null,
            actor.Id,
            "Expired Announcement");
        expiredAnnouncement.ExpiresAt = Now.AddMinutes(-1);
        var futureAnnouncement = NewAnnouncement(
            tenantA.Id,
            ownerWorkspace.Id,
            null,
            null,
            actor.Id,
            "Future Announcement");
        futureAnnouncement.PublishedAt = Now.AddMinutes(1);
        var deletedAnnouncement = NewAnnouncement(
            tenantA.Id,
            ownerWorkspace.Id,
            null,
            null,
            actor.Id,
            "Deleted Announcement");
        deletedAnnouncement.MarkDeleted(Now.AddMinutes(-1));
        var staleTenantAnnouncement = NewAnnouncement(
            tenantB.Id,
            ownerWorkspace.Id,
            null,
            null,
            other.Id,
            "Tenant B stale Workspace id Announcement");
        dbContext.Announcements.AddRange(
            visibleWorkspaceAnnouncement,
            readWorkspaceAnnouncement,
            visibleGroupAnnouncement,
            hiddenGroupAnnouncement,
            hiddenPrivateChannelAnnouncement,
            expiredAnnouncement,
            futureAnnouncement,
            deletedAnnouncement,
            staleTenantAnnouncement);
        dbContext.AnnouncementReads.Add(new AnnouncementRead
        {
            TenantId = tenantA.Id,
            AnnouncementId = readWorkspaceAnnouncement.Id,
            UserId = actor.Id,
            ReadAt = Now
        });

        var activeProject = NewProject(
            tenantA.Id,
            ownerWorkspace.Id,
            actor.Id,
            "Active WorkspaceVisible",
            ProjectStatus.Active,
            ProjectVisibility.WorkspaceVisible);
        var reviewProject = NewProject(
            tenantA.Id,
            ownerWorkspace.Id,
            actor.Id,
            "Review WorkspaceVisible",
            ProjectStatus.Review,
            ProjectVisibility.WorkspaceVisible);
        var planningProject = NewProject(
            tenantA.Id,
            ownerWorkspace.Id,
            actor.Id,
            "Planning",
            ProjectStatus.Planning,
            ProjectVisibility.WorkspaceVisible);
        var completedProject = NewProject(
            tenantA.Id,
            ownerWorkspace.Id,
            actor.Id,
            "Completed",
            ProjectStatus.Completed,
            ProjectVisibility.WorkspaceVisible);
        var suspendedProject = NewProject(
            tenantA.Id,
            ownerWorkspace.Id,
            actor.Id,
            "Suspended",
            ProjectStatus.Suspended,
            ProjectVisibility.WorkspaceVisible);
        var archivedProject = NewProject(
            tenantA.Id,
            ownerWorkspace.Id,
            actor.Id,
            "Archived",
            ProjectStatus.Archived,
            ProjectVisibility.WorkspaceVisible);
        var deletedProject = NewProject(
            tenantA.Id,
            ownerWorkspace.Id,
            actor.Id,
            "Deleted",
            ProjectStatus.Deleted,
            ProjectVisibility.WorkspaceVisible);
        deletedProject.MarkDeleted(Now);
        var restrictedProject = NewProject(
            tenantA.Id,
            ownerWorkspace.Id,
            other.Id,
            "Restricted Hidden",
            ProjectStatus.Active,
            ProjectVisibility.Restricted);
        var membersOnlyProject = NewProject(
            tenantA.Id,
            ownerWorkspace.Id,
            other.Id,
            "MembersOnly Visible",
            ProjectStatus.Active,
            ProjectVisibility.MembersOnly);
        var staleTenantProject = NewProject(
            tenantB.Id,
            ownerWorkspace.Id,
            other.Id,
            "Tenant B stale Workspace id Project",
            ProjectStatus.Active,
            ProjectVisibility.WorkspaceVisible);
        dbContext.Projects.AddRange(
            activeProject,
            reviewProject,
            planningProject,
            completedProject,
            suspendedProject,
            archivedProject,
            deletedProject,
            restrictedProject,
            membersOnlyProject,
            staleTenantProject);
        var membersOnlyProjectMember = NewProjectMember(
            tenantA.Id,
            membersOnlyProject.Id,
            actor.Id,
            ProjectRole.Viewer);
        dbContext.ProjectMembers.Add(membersOnlyProjectMember);

        var unreadConversation = NewConversation(
            tenantA.Id,
            ownerWorkspace.Id,
            null,
            actor.Id,
            "Unread Workspace Conversation",
            ConversationType.WorkspaceChannel);
        var readConversation = NewConversation(
            tenantA.Id,
            ownerWorkspace.Id,
            null,
            actor.Id,
            "Read Workspace Conversation",
            ConversationType.WorkspaceChannel);
        var ownMessageConversation = NewConversation(
            tenantA.Id,
            ownerWorkspace.Id,
            null,
            actor.Id,
            "Own Message Only Conversation",
            ConversationType.WorkspaceChannel);
        var visibleDirectMessage = NewConversation(
            tenantA.Id,
            ownerWorkspace.Id,
            null,
            actor.Id,
            "Private visible title",
            ConversationType.DirectMessage);
        var hiddenDirectMessage = NewConversation(
            tenantA.Id,
            ownerWorkspace.Id,
            null,
            other.Id,
            "Private hidden title",
            ConversationType.DirectMessage);
        var removedConversation = NewConversation(
            tenantA.Id,
            ownerWorkspace.Id,
            null,
            other.Id,
            "Removed participant title",
            ConversationType.WorkspaceChannel);
        var restrictedProjectConversation = NewConversation(
            tenantA.Id,
            ownerWorkspace.Id,
            restrictedProject.Id,
            other.Id,
            "Restricted Project Conversation",
            ConversationType.ProjectChannel);
        var staleTenantConversation = NewConversation(
            tenantB.Id,
            ownerWorkspace.Id,
            null,
            other.Id,
            "Tenant B stale Workspace id Conversation",
            ConversationType.DirectMessage);
        dbContext.Conversations.AddRange(
            unreadConversation,
            readConversation,
            ownMessageConversation,
            visibleDirectMessage,
            hiddenDirectMessage,
            removedConversation,
            restrictedProjectConversation,
            staleTenantConversation);

        var readConversationMember = NewConversationMember(
            tenantA.Id,
            readConversation.Id,
            actor.Id);
        dbContext.ConversationMembers.AddRange(
            NewConversationMember(tenantA.Id, unreadConversation.Id, actor.Id),
            NewConversationMember(tenantA.Id, unreadConversation.Id, other.Id),
            readConversationMember,
            NewConversationMember(tenantA.Id, readConversation.Id, other.Id),
            NewConversationMember(tenantA.Id, ownMessageConversation.Id, actor.Id),
            NewConversationMember(tenantA.Id, ownMessageConversation.Id, other.Id),
            NewConversationMember(tenantA.Id, visibleDirectMessage.Id, actor.Id),
            NewConversationMember(tenantA.Id, visibleDirectMessage.Id, other.Id),
            NewConversationMember(tenantA.Id, hiddenDirectMessage.Id, other.Id),
            NewConversationMember(tenantA.Id, hiddenDirectMessage.Id, singleWorkspaceUser.Id),
            NewConversationMember(
                tenantA.Id,
                removedConversation.Id,
                actor.Id,
                removedAt: Now.AddMinutes(-1)),
            NewConversationMember(tenantA.Id, removedConversation.Id, other.Id),
            NewConversationMember(tenantA.Id, restrictedProjectConversation.Id, actor.Id),
            NewConversationMember(tenantA.Id, restrictedProjectConversation.Id, other.Id),
            NewConversationMember(tenantB.Id, staleTenantConversation.Id, actor.Id),
            NewConversationMember(tenantB.Id, staleTenantConversation.Id, other.Id));

        var readConversationMessage = NewMessage(
            tenantA.Id,
            ownerWorkspace.Id,
            readConversation.Id,
            other.Id,
            "Read body",
            Now.AddMinutes(-5));
        dbContext.Messages.AddRange(
            NewMessage(tenantA.Id, ownerWorkspace.Id, unreadConversation.Id, other.Id, "Unread body one", Now.AddMinutes(-4)),
            NewMessage(tenantA.Id, ownerWorkspace.Id, unreadConversation.Id, other.Id, "Unread body two", Now.AddMinutes(-3)),
            readConversationMessage,
            NewMessage(tenantA.Id, ownerWorkspace.Id, ownMessageConversation.Id, actor.Id, "Own body", Now.AddMinutes(-2)),
            NewMessage(tenantA.Id, ownerWorkspace.Id, visibleDirectMessage.Id, other.Id, "Visible DM body", Now.AddMinutes(-2)),
            NewMessage(tenantA.Id, ownerWorkspace.Id, hiddenDirectMessage.Id, other.Id, "Hidden DM body", Now.AddMinutes(-2)),
            NewMessage(tenantA.Id, ownerWorkspace.Id, removedConversation.Id, other.Id, "Removed body", Now.AddMinutes(-2)),
            NewMessage(tenantA.Id, ownerWorkspace.Id, restrictedProjectConversation.Id, other.Id, "Restricted body", Now.AddMinutes(-2)),
            NewMessage(tenantB.Id, ownerWorkspace.Id, staleTenantConversation.Id, other.Id, "Tenant B body", Now.AddMinutes(-2)));

        await dbContext.SaveChangesAsync();
        await dbContext.Entry(readConversationMessage).ReloadAsync();

        var readAt = readConversationMessage.CreatedAt.AddMilliseconds(1);
        readConversationMember.LastReadMessageId = readConversationMessage.Id;
        readConversationMember.LastReadAt = readAt;
        var readConversationState = new ReadState
        {
            TenantId = tenantA.Id,
            UserId = actor.Id,
            ScopeType = ReadScopeType.Conversation,
            ScopeId = readConversation.Id,
            ConversationId = readConversation.Id,
            LastReadItemId = readConversationMessage.Id,
            LastReadMessageId = readConversationMessage.Id,
            LastReadAt = readAt,
            LastReadSequence = readConversationMessage.CreatedAt.UtcTicks,
            StateVersion = 1,
            CreatedAt = Now
        };
        dbContext.ReadStates.Add(readConversationState);

        await dbContext.SaveChangesAsync();

        return new DashboardGraph(
            tenantA,
            tenantB,
            actor,
            singleWorkspaceUser,
            revokedUser,
            systemAdmin,
            ownerWorkspace,
            adminWorkspace,
            readOnlyWorkspace,
            archivedWorkspace,
            tenantBWorkspace,
            membersOnlyProjectMember,
            readConversation,
            ownMessageConversation,
            readConversationMessage,
            readConversationMember,
            readConversationState,
            visibleWorkspaceAnnouncement,
            visibleGroupAnnouncement,
            hiddenGroupAnnouncement,
            hiddenPrivateChannelAnnouncement);
    }

    private static Tenant NewTenant(string prefix) => new()
    {
        Name = prefix,
        DisplayName = prefix,
        Slug = $"{prefix}-{Guid.NewGuid():N}",
        Status = TenantStatus.Active
    };

    private static User NewUser(string prefix, SystemRole role)
    {
        var email = $"{prefix}-{Guid.NewGuid():N}@example.test";
        return new User
        {
            DisplayName = prefix,
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            PasswordHash = "test-hash",
            Status = UserStatus.Active,
            SystemRole = role,
            CreatedAt = Now
        };
    }

    private static TenantUser NewTenantUser(Guid tenantId, Guid userId, TenantUserRole role) => new()
    {
        TenantId = tenantId,
        UserId = userId,
        Role = role,
        Status = TenantUserStatus.Active,
        JoinedAt = Now,
        CreatedAt = Now
    };

    private static Workspace NewWorkspace(Guid tenantId, Guid creatorId, string name, bool updated = false) => new()
    {
        TenantId = tenantId,
        Name = name,
        Slug = $"{name.ToLowerInvariant().Replace(' ', '-')}-{Guid.NewGuid():N}",
        Description = $"{name} description",
        Icon = "briefcase",
        Status = WorkspaceStatus.Active,
        CreatedByUserId = creatorId,
        CreatedAt = Now.AddDays(-1),
        UpdatedAt = updated ? Now : null
    };

    private static WorkspaceMember NewWorkspaceMember(
        Guid tenantId,
        Guid workspaceId,
        Guid userId,
        WorkspaceRole role,
        MembershipStatus status = MembershipStatus.Active) => new()
        {
            TenantId = tenantId,
            WorkspaceId = workspaceId,
            UserId = userId,
            Role = role,
            Status = status,
            JoinedAt = Now.AddDays(-1),
            CreatedAt = Now.AddDays(-1)
        };

    private static Group NewGroup(Guid tenantId, Guid workspaceId, Guid creatorId, string name) => new()
    {
        TenantId = tenantId,
        WorkspaceId = workspaceId,
        Name = name,
        Slug = $"{name.ToLowerInvariant().Replace(' ', '-')}-{Guid.NewGuid():N}",
        Status = GroupStatus.Active,
        CreatedByUserId = creatorId,
        CreatedAt = Now.AddDays(-1)
    };

    private static GroupMember NewGroupMember(
        Guid tenantId,
        Guid groupId,
        Guid userId,
        GroupRole role = GroupRole.Member) => new()
    {
        TenantId = tenantId,
        GroupId = groupId,
        UserId = userId,
        Role = role,
        JoinedAt = Now.AddDays(-1),
        CreatedAt = Now.AddDays(-1)
    };

    private static Channel NewChannel(
        Guid tenantId,
        Guid workspaceId,
        Guid groupId,
        Guid creatorId,
        string name,
        ChannelType type) => new()
        {
            TenantId = tenantId,
            WorkspaceId = workspaceId,
            GroupId = groupId,
            Name = name,
            Slug = $"{name.ToLowerInvariant().Replace(' ', '-')}-{Guid.NewGuid():N}",
            Type = type,
            Status = ChannelStatus.Active,
            CreatedByUserId = creatorId,
            CreatedAt = Now.AddDays(-1)
        };

    private static ChannelMember NewChannelMember(Guid tenantId, Guid channelId, Guid userId) => new()
    {
        TenantId = tenantId,
        ChannelId = channelId,
        UserId = userId,
        Role = ChannelRole.Member,
        JoinedAt = Now.AddDays(-1),
        CreatedAt = Now.AddDays(-1)
    };

    private static Announcement NewAnnouncement(
        Guid tenantId,
        Guid workspaceId,
        Guid? groupId,
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
            PublishedAt = Now.AddHours(-1),
            CreatedAt = Now.AddHours(-1)
        };

    private static Project NewProject(
        Guid tenantId,
        Guid workspaceId,
        Guid ownerId,
        string name,
        ProjectStatus status,
        ProjectVisibility visibility) => new()
        {
            TenantId = tenantId,
            WorkspaceId = workspaceId,
            OwnerUserId = ownerId,
            CreatedByUserId = ownerId,
            Name = name,
            Slug = $"{name.ToLowerInvariant().Replace(' ', '-')}-{Guid.NewGuid():N}",
            Status = status,
            Visibility = visibility,
            ActivationState = ProjectActivationState.Activated,
            ActivatedAtUtc = Now.AddDays(-1),
            ActivationVersion = 1,
            VersionNo = 1,
            CreatedAt = Now.AddDays(-1)
        };

    private static ProjectMember NewProjectMember(
        Guid tenantId,
        Guid projectId,
        Guid userId,
        ProjectRole role) => new()
        {
            TenantId = tenantId,
            ProjectId = projectId,
            UserId = userId,
            Role = role,
            JoinedAt = Now.AddDays(-1),
            CreatedAt = Now.AddDays(-1)
        };

    private static Conversation NewConversation(
        Guid tenantId,
        Guid workspaceId,
        Guid? projectId,
        Guid creatorId,
        string title,
        ConversationType type) => new()
        {
            TenantId = tenantId,
            WorkspaceId = workspaceId,
            ProjectId = projectId,
            Type = type,
            Title = title,
            CreatedByUserId = creatorId,
            CreatedAt = Now.AddHours(-1)
        };

    private static ConversationMember NewConversationMember(
        Guid tenantId,
        Guid conversationId,
        Guid userId,
        DateTimeOffset? removedAt = null) => new()
        {
            TenantId = tenantId,
            ConversationId = conversationId,
            UserId = userId,
            CanRead = true,
            CanPost = true,
            JoinedAt = Now.AddHours(-1),
            RemovedAt = removedAt,
            CreatedAt = Now.AddHours(-1)
        };

    private static Message NewMessage(
        Guid tenantId,
        Guid workspaceId,
        Guid conversationId,
        Guid authorId,
        string body,
        DateTimeOffset createdAt) => new()
        {
            TenantId = tenantId,
            WorkspaceId = workspaceId,
            ConversationId = conversationId,
            AuthorUserId = authorId,
            Body = body,
            CreatedAt = createdAt
        };

    private sealed record DashboardGraph(
        Tenant TenantA,
        Tenant TenantB,
        User Actor,
        User SingleWorkspaceUser,
        User RevokedUser,
        User SystemAdmin,
        Workspace OwnerWorkspace,
        Workspace AdminWorkspace,
        Workspace ReadOnlyWorkspace,
        Workspace ArchivedWorkspace,
        Workspace TenantBWorkspace,
        ProjectMember MembersOnlyProjectMember,
        Conversation ReadConversation,
        Conversation OwnMessageConversation,
        Message ReadConversationMessage,
        ConversationMember ReadConversationMember,
        ReadState ReadConversationState,
        Announcement VisibleWorkspaceAnnouncement,
        Announcement VisibleGroupAnnouncement,
        Announcement HiddenGroupAnnouncement,
        Announcement HiddenPrivateChannelAnnouncement);

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class TestCurrentUser(User user) : ICurrentUser
    {
        public Guid? UserId => user.Id;
        public Guid? SessionId => Guid.NewGuid();
        public string? Email => user.Email;
        public SystemRole? SystemRole => user.SystemRole;
        public bool IsAuthenticated => true;
    }

    private sealed class CommandCounterInterceptor : DbCommandInterceptor
    {
        private readonly List<string> commands = [];
        private bool active;

        public void Begin()
        {
            commands.Clear();
            active = true;
        }

        public IReadOnlyList<string> End()
        {
            active = false;
            return commands.ToArray();
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (active)
            {
                commands.Add(command.CommandText);
            }

            return ValueTask.FromResult(result);
        }
    }
}
