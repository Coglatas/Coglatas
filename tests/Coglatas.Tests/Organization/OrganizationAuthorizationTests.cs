using Coglatas.Application.Channels;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Groups;
using Coglatas.Application.Workspaces;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;

namespace Coglatas.Tests.Organization;

public sealed class OrganizationAuthorizationTests
{
    [Fact]
    public async Task NonMemberCannotViewGroup()
    {
        var fixture = OrgFixture.Create();

        var canView = await fixture.GroupAuthorization.CanViewGroup(Guid.NewGuid(), fixture.Group.Id);

        Assert.False(canView);
    }

    [Fact]
    public async Task MemberCannotManageGroupMembers()
    {
        var fixture = OrgFixture.Create();
        var member = fixture.AddUser(SystemRole.User);
        fixture.AddWorkspaceMember(member.Id, WorkspaceRole.Member);
        fixture.AddGroupMember(member.Id, GroupRole.Member);

        var canManage = await fixture.GroupAuthorization.CanManageGroup(member.Id, fixture.Group.Id);

        Assert.False(canManage);
    }

    [Fact]
    public async Task GroupAdminCanAddMember()
    {
        var fixture = OrgFixture.Create();
        var admin = fixture.AddUser(SystemRole.User);
        var target = fixture.AddUser(SystemRole.User);
        fixture.Current.UserIdValue = admin.Id;
        fixture.AddWorkspaceMember(admin.Id, WorkspaceRole.Member);
        fixture.AddWorkspaceMember(target.Id, WorkspaceRole.Member);
        fixture.AddGroupMember(admin.Id, GroupRole.Admin);

        var result = await fixture.GroupService.AddMemberAsync(fixture.Group.Id, new AddGroupMemberRequest(target.Id, GroupRole.Member));

        Assert.True(result.IsSuccess);
        Assert.Contains(fixture.GroupMembers, member => member.UserId == target.Id);
    }

    [Fact]
    public async Task UserCannotBeAddedToGroupIfNotInWorkspace()
    {
        var fixture = OrgFixture.Create();
        var admin = fixture.AddUser(SystemRole.User);
        var target = fixture.AddUser(SystemRole.User);
        fixture.Current.UserIdValue = admin.Id;
        fixture.AddWorkspaceMember(admin.Id, WorkspaceRole.Member);
        fixture.AddGroupMember(admin.Id, GroupRole.Admin);

        var result = await fixture.GroupService.AddMemberAsync(fixture.Group.Id, new AddGroupMemberRequest(target.Id, GroupRole.Member));

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task DuplicateGroupMembershipIsRejected()
    {
        var fixture = OrgFixture.Create();
        var admin = fixture.AddUser(SystemRole.User);
        var target = fixture.AddUser(SystemRole.User);
        fixture.Current.UserIdValue = admin.Id;
        fixture.AddWorkspaceMember(admin.Id, WorkspaceRole.Member);
        fixture.AddWorkspaceMember(target.Id, WorkspaceRole.Member);
        fixture.AddGroupMember(admin.Id, GroupRole.Admin);
        fixture.AddGroupMember(target.Id, GroupRole.Member);

        var result = await fixture.GroupService.AddMemberAsync(fixture.Group.Id, new AddGroupMemberRequest(target.Id, GroupRole.Member));

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task NonMemberCannotReadPrivateChannel()
    {
        var fixture = OrgFixture.Create();
        fixture.Channel.Type = ChannelType.Private;

        var canView = await fixture.ChannelAuthorization.CanViewChannel(Guid.NewGuid(), fixture.Channel.Id);

        Assert.False(canView);
    }

    [Fact]
    public async Task GroupMemberCanReadPublicChannel()
    {
        var fixture = OrgFixture.Create();
        var user = fixture.AddUser(SystemRole.User);
        fixture.AddWorkspaceMember(user.Id, WorkspaceRole.Member);
        fixture.AddGroupMember(user.Id, GroupRole.Member);
        fixture.Channel.Type = ChannelType.Public;

        var canView = await fixture.ChannelAuthorization.CanViewChannel(user.Id, fixture.Channel.Id);

        Assert.True(canView);
    }

    [Fact]
    public async Task ReadOnlyCannotPost()
    {
        var fixture = OrgFixture.Create();
        var user = fixture.AddUser(SystemRole.User);
        fixture.AddWorkspaceMember(user.Id, WorkspaceRole.Member);
        fixture.AddGroupMember(user.Id, GroupRole.ReadOnly);
        fixture.Channel.Type = ChannelType.Public;

        var canPost = await fixture.ChannelAuthorization.CanPostToChannel(user.Id, fixture.Channel.Id);

        Assert.False(canPost);
    }

    [Fact]
    public async Task AnnouncementChannelBlocksNormalMemberPosts()
    {
        var fixture = OrgFixture.Create();
        var user = fixture.AddUser(SystemRole.User);
        fixture.AddWorkspaceMember(user.Id, WorkspaceRole.Member);
        fixture.AddGroupMember(user.Id, GroupRole.Member);
        fixture.Channel.Type = ChannelType.Announcement;

        var canPost = await fixture.ChannelAuthorization.CanPostToChannel(user.Id, fixture.Channel.Id);

        Assert.False(canPost);
    }

    [Fact]
    public async Task UnauthorizedUserCannotPin()
    {
        var fixture = OrgFixture.Create();
        var author = fixture.AddUser(SystemRole.User);
        var outsider = fixture.AddUser(SystemRole.User);
        fixture.AddWorkspaceMember(author.Id, WorkspaceRole.Member);
        fixture.AddGroupMember(author.Id, GroupRole.Member);
        var post = fixture.AddPost(author.Id);

        var canPin = await fixture.ChannelAuthorization.CanPinPost(outsider.Id, post.Id);

        Assert.False(canPin);
    }

    private sealed class OrgFixture
    {
        private OrgFixture()
        {
            WorkspaceAuthorization = new WorkspaceAuthorizationService(Users, Workspaces);
            GroupAuthorization = new GroupAuthorizationService(Groups, Workspaces, WorkspaceAuthorization);
            ChannelAuthorization = new ChannelAuthorizationService(Channels, Groups, GroupAuthorization);
            GroupService = new GroupService(Groups, Workspaces, Users, GroupAuthorization, Current, Clock, Audit, UnitOfWork);
        }

        public FakeUsers Users { get; } = new();
        public FakeWorkspaces Workspaces { get; } = new();
        public FakeGroups Groups { get; } = new();
        public FakeChannels Channels { get; } = new();
        public FakeCurrentUser Current { get; } = new();
        public FakeClock Clock { get; } = new();
        public FakeAuditLogger Audit { get; } = new();
        public FakeUnitOfWork UnitOfWork { get; } = new();
        public WorkspaceAuthorizationService WorkspaceAuthorization { get; }
        public GroupAuthorizationService GroupAuthorization { get; }
        public ChannelAuthorizationService ChannelAuthorization { get; }
        public GroupService GroupService { get; }
        public Workspace Workspace { get; } = new() { Name = "Workspace", Slug = "workspace", CreatedByUserId = Guid.NewGuid() };
        public Group Group { get; } = new() { Name = "Group", Slug = "group", WorkspaceId = Guid.Empty, CreatedByUserId = Guid.NewGuid() };
        public Channel Channel { get; } = new() { Name = "General", Slug = "general", WorkspaceId = Guid.Empty, GroupId = Guid.Empty, CreatedByUserId = Guid.NewGuid() };
        public List<GroupMember> GroupMembers => Groups.Members;

        public static OrgFixture Create()
        {
            var fixture = new OrgFixture();
            fixture.Group.WorkspaceId = fixture.Workspace.Id;
            fixture.Channel.WorkspaceId = fixture.Workspace.Id;
            fixture.Channel.GroupId = fixture.Group.Id;
            fixture.Workspaces.Items[fixture.Workspace.Id] = fixture.Workspace;
            fixture.Groups.Items[fixture.Group.Id] = fixture.Group;
            fixture.Channels.Items[fixture.Channel.Id] = fixture.Channel;
            return fixture;
        }

        public User AddUser(SystemRole role)
        {
            var user = new User
            {
                DisplayName = $"User {Users.Items.Count + 1}",
                Email = $"user{Users.Items.Count + 1}@example.com",
                NormalizedEmail = $"USER{Users.Items.Count + 1}@EXAMPLE.COM",
                PasswordHash = "hash",
                SystemRole = role,
                Status = UserStatus.Active
            };
            Users.Items[user.Id] = user;
            return user;
        }

        public void AddWorkspaceMember(Guid userId, WorkspaceRole role)
        {
            Workspaces.Members.Add(new WorkspaceMember
            {
                WorkspaceId = Workspace.Id,
                UserId = userId,
                User = Users.Items[userId],
                Role = role,
                Status = MembershipStatus.Active,
                JoinedAt = Clock.UtcNow
            });
        }

        public void AddGroupMember(Guid userId, GroupRole role)
        {
            Groups.Members.Add(new GroupMember
            {
                GroupId = Group.Id,
                UserId = userId,
                User = Users.Items[userId],
                Role = role,
                JoinedAt = Clock.UtcNow
            });
        }

        public Post AddPost(Guid authorUserId)
        {
            var post = new Post
            {
                ChannelId = Channel.Id,
                AuthorUserId = authorUserId,
                AuthorUser = Users.Items[authorUserId],
                Body = "Post"
            };
            Channels.Posts.Add(post);
            return post;
        }
    }

    private sealed class FakeUsers : IUserRepository
    {
        public Dictionary<Guid, User> Items { get; } = [];
        public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Items.GetValueOrDefault(id));
        public Task<User?> GetByNormalizedEmailAsync(string normalizedEmail, CancellationToken cancellationToken = default) => Task.FromResult(Items.Values.FirstOrDefault(user => user.NormalizedEmail == normalizedEmail));
        public Task AddAsync(User user, CancellationToken cancellationToken = default) { Items[user.Id] = user; return Task.CompletedTask; }
    }

    private sealed class FakeWorkspaces : IWorkspaceRepository
    {
        public Dictionary<Guid, Workspace> Items { get; } = [];
        public List<WorkspaceMember> Members { get; } = [];
        public Task<IReadOnlyList<Workspace>> ListForUserAsync(Guid userId, bool includeAll, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Workspace>>(Items.Values.ToList());
        public Task<Workspace?> GetByIdAsync(Guid workspaceId, CancellationToken cancellationToken = default) => Task.FromResult(Items.GetValueOrDefault(workspaceId));
        public Task<WorkspaceMember?> GetMemberAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken = default) => Task.FromResult(Members.FirstOrDefault(member => member.WorkspaceId == workspaceId && member.UserId == userId));
        public Task<IReadOnlyList<WorkspaceMember>> ListMembersAsync(Guid workspaceId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<WorkspaceMember>>(Members.Where(member => member.WorkspaceId == workspaceId).ToList());
        public Task AddAsync(Workspace workspace, CancellationToken cancellationToken = default) { Items[workspace.Id] = workspace; return Task.CompletedTask; }
        public Task AddMemberAsync(WorkspaceMember member, CancellationToken cancellationToken = default) { Members.Add(member); return Task.CompletedTask; }
    }

    private sealed class FakeGroups : IGroupRepository
    {
        public Dictionary<Guid, Group> Items { get; } = [];
        public List<GroupMember> Members { get; } = [];
        public Task<IReadOnlyList<Group>> ListByWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Group>>(Items.Values.Where(group => group.WorkspaceId == workspaceId).ToList());
        public Task<IReadOnlyList<Group>> ListManagedByUserAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Group>>(Items.Values.Where(group => group.WorkspaceId == workspaceId && Members.Any(member => member.GroupId == group.Id && member.UserId == userId && member.Role is GroupRole.Owner or GroupRole.Admin)).ToList());
        public Task<Group?> GetByIdAsync(Guid groupId, CancellationToken cancellationToken = default) => Task.FromResult(Items.GetValueOrDefault(groupId));
        public Task<GroupMember?> GetMemberAsync(Guid groupId, Guid userId, CancellationToken cancellationToken = default) => Task.FromResult(Members.FirstOrDefault(member => member.GroupId == groupId && member.UserId == userId));
        public Task<IReadOnlyList<GroupMember>> ListMembersAsync(Guid groupId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GroupMember>>(Members.Where(member => member.GroupId == groupId).ToList());
        public Task AddAsync(Group group, CancellationToken cancellationToken = default) { Items[group.Id] = group; return Task.CompletedTask; }
        public Task AddMemberAsync(GroupMember member, CancellationToken cancellationToken = default) { Members.Add(member); return Task.CompletedTask; }
    }

    private sealed class FakeChannels : IChannelRepository
    {
        public Dictionary<Guid, Channel> Items { get; } = [];
        public List<ChannelMember> Members { get; } = [];
        public List<Post> Posts { get; } = [];
        public List<PostThread> Threads { get; } = [];
        public Task<IReadOnlyList<Channel>> ListByGroupAsync(Guid groupId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Channel>>(Items.Values.Where(channel => channel.GroupId == groupId).ToList());
        public Task<Channel?> GetByIdAsync(Guid channelId, CancellationToken cancellationToken = default) => Task.FromResult(Items.GetValueOrDefault(channelId));
        public Task<ChannelMember?> GetMemberAsync(Guid channelId, Guid userId, CancellationToken cancellationToken = default) => Task.FromResult(Members.FirstOrDefault(member => member.ChannelId == channelId && member.UserId == userId));
        public Task<IReadOnlyList<ChannelMember>> ListMembersAsync(Guid channelId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ChannelMember>>(Members.Where(member => member.ChannelId == channelId).ToList());
        public Task<IReadOnlyList<Post>> ListPinnedPostsAsync(Guid channelId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Post>>(Posts.Where(post => post.ChannelId == channelId && post.PinnedAt.HasValue && !post.DeletedAt.HasValue).ToList());
        public Task<PagedResponse<Post>> ListPostsAsync(Guid channelId, int page, int pageSize, DateTimeOffset? before, DateTimeOffset? after, CancellationToken cancellationToken = default) => Task.FromResult(new PagedResponse<Post>(Posts.Where(post => post.ChannelId == channelId && !post.DeletedAt.HasValue).ToList(), page, pageSize, Posts.Count));
        public Task<Post?> GetPostByIdAsync(Guid postId, CancellationToken cancellationToken = default) => Task.FromResult(Posts.FirstOrDefault(post => post.Id == postId));
        public Task<PagedResponse<PostThread>> ListThreadsAsync(Guid postId, int page, int pageSize, DateTimeOffset? before, DateTimeOffset? after, CancellationToken cancellationToken = default)
        {
            var source = Threads.Where(thread => thread.PostId == postId && thread.DeletedAt == null);
            if (before.HasValue)
            {
                source = source.Where(thread => thread.CreatedAt < before.Value);
            }

            if (after.HasValue)
            {
                source = source.Where(thread => thread.CreatedAt > after.Value);
            }

            var filtered = source.ToList();
            var items = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
            return Task.FromResult(new PagedResponse<PostThread>(items, page, pageSize, filtered.Count));
        }
        public Task AddAsync(Channel channel, CancellationToken cancellationToken = default) { Items[channel.Id] = channel; return Task.CompletedTask; }
        public Task AddMemberAsync(ChannelMember member, CancellationToken cancellationToken = default) { Members.Add(member); return Task.CompletedTask; }
        public Task RemoveMemberAsync(ChannelMember member, CancellationToken cancellationToken = default) { Members.Remove(member); return Task.CompletedTask; }
        public Task AddPostAsync(Post post, CancellationToken cancellationToken = default) { Posts.Add(post); return Task.CompletedTask; }
        public Task AddThreadAsync(PostThread thread, CancellationToken cancellationToken = default) { Threads.Add(thread); return Task.CompletedTask; }
    }

    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid? UserIdValue { get; set; }
        public Guid? UserId => UserIdValue;
        public Guid? SessionId => null;
        public string? Email => null;
        public SystemRole? SystemRole => null;
        public bool IsAuthenticated => UserIdValue.HasValue;
    }

    private sealed class FakeClock : IClock { public DateTimeOffset UtcNow { get; } = new(2026, 6, 6, 0, 0, 0, TimeSpan.Zero); }
    private sealed class FakeAuditLogger : IAuditLogger { public Task LogAsync(AuditLogEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask; }
    private sealed class FakeUnitOfWork : IUnitOfWork { public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(1); }
}
