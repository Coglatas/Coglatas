using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Workspaces;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;

namespace Coglatas.Tests.Workspaces;

public sealed class Wpc02AWorkspaceAuthorizationTests
{
    [Fact]
    public async Task ArchivedCurrentMemberCanReadHistoricalWorkspace()
    {
        var fixture = Fixture.Create(WorkspaceStatus.Archived);
        var member = fixture.AddUser(SystemRole.User);
        fixture.AddMember(member.Id, WorkspaceRole.Member);

        Assert.True(await fixture.Authorization.CanViewWorkspace(member.Id, fixture.Workspace.Id));
        Assert.False(await fixture.Authorization.CanManageWorkspace(member.Id, fixture.Workspace.Id));
    }

    [Fact]
    public async Task ArchivedWorkspaceAdminCannotRestore()
    {
        var fixture = Fixture.Create(WorkspaceStatus.Archived);
        var admin = fixture.AddUser(SystemRole.User);
        fixture.AddMember(admin.Id, WorkspaceRole.Admin);

        Assert.True(await fixture.Authorization.CanViewWorkspace(admin.Id, fixture.Workspace.Id));
        Assert.True(await fixture.Authorization.CanGovernWorkspace(admin.Id, fixture.Workspace.Id));
        Assert.False(await fixture.Authorization.CanManageWorkspace(admin.Id, fixture.Workspace.Id));
        Assert.False(await fixture.Authorization.CanRestoreWorkspace(admin.Id, fixture.Workspace.Id));
    }

    [Fact]
    public async Task ArchivedWorkspaceOwnerCanRestoreButCannotPerformOrdinaryMutation()
    {
        var fixture = Fixture.Create(WorkspaceStatus.Archived);
        var owner = fixture.AddUser(SystemRole.User);
        fixture.AddMember(owner.Id, WorkspaceRole.Owner);

        Assert.True(await fixture.Authorization.CanViewWorkspace(owner.Id, fixture.Workspace.Id));
        Assert.True(await fixture.Authorization.CanGovernWorkspace(owner.Id, fixture.Workspace.Id));
        Assert.True(await fixture.Authorization.CanRestoreWorkspace(owner.Id, fixture.Workspace.Id));
        Assert.False(await fixture.Authorization.CanManageWorkspace(owner.Id, fixture.Workspace.Id));
        Assert.False(await fixture.Authorization.CanContributeWorkspace(owner.Id, fixture.Workspace.Id));
    }

    [Fact]
    public async Task SystemAdminWithoutCurrentMembershipHasNoArchivedAccess()
    {
        var fixture = Fixture.Create(WorkspaceStatus.Archived);
        var systemAdmin = fixture.AddUser(SystemRole.SystemAdmin);

        Assert.False(await fixture.Authorization.CanViewWorkspace(systemAdmin.Id, fixture.Workspace.Id));
        Assert.False(await fixture.Authorization.CanGovernWorkspace(systemAdmin.Id, fixture.Workspace.Id));
        Assert.False(await fixture.Authorization.CanManageWorkspace(systemAdmin.Id, fixture.Workspace.Id));
        Assert.False(await fixture.Authorization.CanRestoreWorkspace(systemAdmin.Id, fixture.Workspace.Id));
    }

    [Fact]
    public async Task ActiveWorkspaceRetainsSystemAdminOperationalCompatibility()
    {
        var fixture = Fixture.Create(WorkspaceStatus.Active);
        var systemAdmin = fixture.AddUser(SystemRole.SystemAdmin);

        Assert.True(await fixture.Authorization.CanViewWorkspace(systemAdmin.Id, fixture.Workspace.Id));
        Assert.True(await fixture.Authorization.CanGovernWorkspace(systemAdmin.Id, fixture.Workspace.Id));
        Assert.True(await fixture.Authorization.CanManageWorkspace(systemAdmin.Id, fixture.Workspace.Id));
        Assert.True(await fixture.Authorization.CanContributeWorkspace(systemAdmin.Id, fixture.Workspace.Id));
    }

    private sealed class Fixture
    {
        private Fixture(WorkspaceStatus status)
        {
            Workspace.Status = status;
            Workspaces.Items[Workspace.Id] = Workspace;
            Authorization = new WorkspaceAuthorizationService(Users, Workspaces);
        }

        private FakeUsers Users { get; } = new();
        private FakeWorkspaces Workspaces { get; } = new();
        public WorkspaceAuthorizationService Authorization { get; }
        public Workspace Workspace { get; } = new()
        {
            Name = "Workspace",
            Slug = "workspace",
            CreatedByUserId = Guid.NewGuid()
        };

        public static Fixture Create(WorkspaceStatus status) => new(status);

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

        public void AddMember(Guid userId, WorkspaceRole role, MembershipStatus status = MembershipStatus.Active)
        {
            Workspaces.Members.Add(new WorkspaceMember
            {
                WorkspaceId = Workspace.Id,
                UserId = userId,
                User = Users.Items[userId],
                Role = role,
                Status = status,
                JoinedAt = DateTimeOffset.UtcNow
            });
        }
    }

    private sealed class FakeUsers : IUserRepository
    {
        public Dictionary<Guid, User> Items { get; } = [];
        public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Items.GetValueOrDefault(id));
        public Task<User?> GetByNormalizedEmailAsync(string normalizedEmail, CancellationToken cancellationToken = default) =>
            Task.FromResult(Items.Values.FirstOrDefault(user => user.NormalizedEmail == normalizedEmail));
        public Task AddAsync(User user, CancellationToken cancellationToken = default)
        {
            Items[user.Id] = user;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeWorkspaces : IWorkspaceRepository
    {
        public Dictionary<Guid, Workspace> Items { get; } = [];
        public List<WorkspaceMember> Members { get; } = [];

        public Task<IReadOnlyList<Workspace>> ListForUserAsync(Guid userId, bool includeAll, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Workspace>>(Items.Values.ToList());
        public Task<Workspace?> GetByIdAsync(Guid workspaceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Items.GetValueOrDefault(workspaceId));
        public Task<WorkspaceMember?> GetMemberAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Members.FirstOrDefault(member => member.WorkspaceId == workspaceId && member.UserId == userId));
        public Task<IReadOnlyList<WorkspaceMember>> ListMembersAsync(Guid workspaceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WorkspaceMember>>(Members.Where(member => member.WorkspaceId == workspaceId).ToList());
        public Task AddAsync(Workspace workspace, CancellationToken cancellationToken = default)
        {
            Items[workspace.Id] = workspace;
            return Task.CompletedTask;
        }
        public Task AddMemberAsync(WorkspaceMember member, CancellationToken cancellationToken = default)
        {
            Members.Add(member);
            return Task.CompletedTask;
        }
    }
}
