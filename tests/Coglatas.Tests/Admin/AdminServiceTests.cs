using Coglatas.Application.Admin;
using Coglatas.Application.Auth;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Realtime;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Security;

namespace Coglatas.Tests.Admin;

public sealed class AdminServiceTests
{
    [Fact]
    public async Task NormalUserCannotAccessAdminUsers()
    {
        var fixture = AdminFixture.Create(SystemRole.User);

        var result = await fixture.Service.ListUsersAsync(1, 50);

        Assert.False(result.IsSuccess);
        Assert.Equal("SystemAdmin access is required.", result.Error);
    }

    [Fact]
    public async Task NormalUserCannotCreateInvite()
    {
        var fixture = AdminFixture.Create(SystemRole.User);

        var result = await fixture.Service.CreateInviteAsync(new CreateInviteRequest(
            Guid.NewGuid(),
            "new-user@example.com",
            WorkspaceRole.Member,
            null));

        Assert.False(result.IsSuccess);
        Assert.Equal("SystemAdmin access is required.", result.Error);
        Assert.Empty(fixture.Repository.Invites);
    }

    [Fact]
    public async Task CreateInviteReturnsRawTokenOnceAndStoresOnlyHash()
    {
        var fixture = AdminFixture.Create(SystemRole.SystemAdmin);

        var result = await fixture.Service.CreateInviteAsync(new CreateInviteRequest(
            Guid.NewGuid(),
            "new-user@example.com",
            WorkspaceRole.Member,
            null));

        Assert.True(result.IsSuccess);
        Assert.False(string.IsNullOrWhiteSpace(result.Value?.InviteToken));
        var storedInvite = Assert.Single(fixture.Repository.Invites);
        Assert.NotEqual(result.Value!.InviteToken, storedInvite.TokenHash);
        Assert.Equal(new Sha256TokenHasher().HashToken(result.Value.InviteToken!), storedInvite.TokenHash);
        Assert.Equal(fixture.Clock.UtcNow.AddDays(7), storedInvite.ExpiresAt);
        Assert.Equal(fixture.Repository.WorkspaceTenantId, storedInvite.TenantId);
    }

    [Fact]
    public async Task CreateInviteFailsWhenWorkspaceTenantContextIsMissing()
    {
        var fixture = AdminFixture.Create(SystemRole.SystemAdmin);
        fixture.Repository.WorkspaceTenantId = Guid.Empty;

        var result = await fixture.Service.CreateInviteAsync(new CreateInviteRequest(
            Guid.NewGuid(),
            "new-user@example.com",
            WorkspaceRole.Member,
            null));

        Assert.False(result.IsSuccess);
        Assert.Equal("Workspace tenant context is missing.", result.Error);
        Assert.Empty(fixture.Repository.Invites);
    }

    [Fact]
    public async Task CreateInviteFailsWhenWorkspaceDoesNotExist()
    {
        var fixture = AdminFixture.Create(SystemRole.SystemAdmin);
        fixture.Repository.WorkspaceExists = false;

        var result = await fixture.Service.CreateInviteAsync(new CreateInviteRequest(
            Guid.NewGuid(),
            "new-user@example.com",
            WorkspaceRole.Member,
            null));

        Assert.False(result.IsSuccess);
        Assert.Equal("Workspace not found.", result.Error);
        Assert.Empty(fixture.Repository.Invites);
    }

    [Fact]
    public async Task LastSystemAdminDemotionIsPrevented()
    {
        var fixture = AdminFixture.Create(SystemRole.SystemAdmin);

        var result = await fixture.Service.ChangeSystemRoleAsync(
            fixture.ActorUser.Id,
            new ChangeSystemRoleRequest(SystemRole.User));

        Assert.False(result.IsSuccess);
        Assert.Equal(SystemRole.SystemAdmin, fixture.ActorUser.SystemRole);
    }

    [Fact]
    public async Task SensitiveSettingValueIsNotReturned()
    {
        var fixture = AdminFixture.Create(SystemRole.SystemAdmin);

        var updated = await fixture.Service.UpdateSettingAsync(
            "IntegrationSecret",
            new UpdateSystemSettingRequest("secret-value", "String", "External integration secret.", true));
        var fetched = await fixture.Service.GetSettingAsync("IntegrationSecret");

        Assert.True(updated.IsSuccess);
        Assert.True(fetched.IsSuccess);
        Assert.Equal("********", updated.Value?.Value);
        Assert.Equal("********", fetched.Value?.Value);
        Assert.Equal("secret-value", fixture.Repository.Settings.Single().Value);
    }

    [Fact]
    [Trait("Scope", "WPC01")]
    public async Task SystemAdminArchiveCannotRewriteDeletedProjectLifecycle()
    {
        var fixture = AdminFixture.Create(SystemRole.SystemAdmin);
        var project = new Project
        {
            Status = ProjectStatus.Deleted,
            Name = "Deleted Project",
            Slug = "deleted-project"
        };
        var deletedAt = fixture.Clock.UtcNow.AddDays(-1);
        project.MarkDeleted(deletedAt, fixture.ActorUser.Id, "historical deletion");
        fixture.Repository.Project = project;

        var result = await fixture.Service.ArchiveProjectAsync(project.Id);

        Assert.False(result.IsSuccess);
        Assert.Equal("InvalidStateTransition", result.ErrorDetail?.Code);
        Assert.Equal(ProjectStatus.Deleted, project.Status);
        Assert.Equal(deletedAt, project.DeletedAt);
        Assert.Empty(fixture.Audit.Entries);
        Assert.Empty(fixture.AuthorizationChanges.Items);
        Assert.Equal(0, fixture.UnitOfWork.SaveCount);
    }

    [Fact]
    [Trait("Scope", "WPC01")]
    public async Task SystemAdminArchiveProjectPublishesTransactionalAuthorizationInvalidation()
    {
        var fixture = AdminFixture.Create(SystemRole.SystemAdmin);
        var memberId = Guid.NewGuid();
        var secondSystemAdmin = new User
        {
            DisplayName = "Second system administrator",
            Email = "second-system-admin@example.com",
            NormalizedEmail = "SECOND-SYSTEM-ADMIN@EXAMPLE.COM",
            SystemRole = SystemRole.SystemAdmin,
            Status = UserStatus.Active
        };
        fixture.Repository.Users[secondSystemAdmin.Id] = secondSystemAdmin;
        var project = new Project
        {
            TenantId = fixture.Tenant.TenantId,
            WorkspaceId = Guid.NewGuid(),
            Status = ProjectStatus.Active,
            Name = "Operational Project",
            Slug = "operational-project"
        };
        fixture.Repository.Project = project;
        fixture.Workspaces.Members.AddRange(
        [
            new WorkspaceMember
            {
                TenantId = project.TenantId,
                WorkspaceId = project.WorkspaceId,
                UserId = memberId,
                Status = MembershipStatus.Active
            },
            new WorkspaceMember
            {
                TenantId = project.TenantId,
                WorkspaceId = project.WorkspaceId,
                UserId = Guid.NewGuid(),
                Status = MembershipStatus.Suspended
            }
        ]);

        var result = await fixture.Service.ArchiveProjectAsync(project.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(ProjectStatus.Archived, project.Status);
        Assert.Equal(fixture.Clock.UtcNow, project.DeletedAt);
        Assert.Contains(fixture.Audit.Entries, item =>
            item.Action == "DataArchived" && item.EntityId == project.Id);
        Assert.Equal(1, fixture.UnitOfWork.SaveCount);
        Assert.Equal(
            new[] { fixture.ActorUser.Id, memberId, secondSystemAdmin.Id }.Order().ToArray(),
            fixture.AuthorizationChanges.Items.Select(item => item.UserId).Order().ToArray());
        Assert.All(fixture.AuthorizationChanges.Items, item =>
        {
            Assert.Equal(project.TenantId, item.TenantId);
            Assert.Equal("project", item.ScopeType);
            Assert.Equal(project.Id, item.ScopeId);
            Assert.Equal("archived", item.Change);
        });
    }

    [Fact]
    [Trait("Scope", "WPC01")]
    public async Task SystemAdminArchiveProjectDoesNotSaveWhenRequiredInvalidationCannotBeStaged()
    {
        var fixture = AdminFixture.Create(SystemRole.SystemAdmin);
        var project = new Project
        {
            TenantId = fixture.Tenant.TenantId,
            WorkspaceId = Guid.NewGuid(),
            Status = ProjectStatus.Active,
            Name = "Operational Project",
            Slug = "operational-project"
        };
        fixture.Repository.Project = project;
        fixture.AuthorizationChanges.ThrowOnPublish = true;

        await Assert.ThrowsAsync<RequiredOutboxStagingException>(() =>
            fixture.Service.ArchiveProjectAsync(project.Id));

        Assert.Equal(0, fixture.UnitOfWork.SaveCount);
        Assert.Empty(fixture.AuthorizationChanges.Items);
    }

    private sealed class AdminFixture
    {
        private AdminFixture(SystemRole actorRole)
        {
            ActorUser = new User
            {
                DisplayName = "Actor",
                Email = "actor@example.com",
                NormalizedEmail = "ACTOR@EXAMPLE.COM",
                SystemRole = actorRole,
                Status = UserStatus.Active
            };

            Repository.Users[ActorUser.Id] = ActorUser;
            Tenant.SetTenant(Repository.WorkspaceTenantId, "admin-test");
            Service = new AdminService(
                Repository,
                new Sha256TokenHasher(),
                Audit,
                new FakeCurrentUser(ActorUser),
                Clock,
                new FakeUserSessionService(),
                UnitOfWork,
                Tenant,
                AuthorizationChanges,
                workspaces: Workspaces);
        }

        public FakeAdminRepository Repository { get; } = new();
        public FakeClock Clock { get; } = new(new DateTimeOffset(2026, 6, 7, 0, 0, 0, TimeSpan.Zero));
        public FakeAuditLogger Audit { get; } = new();
        public FakeUnitOfWork UnitOfWork { get; } = new();
        public CurrentTenantService Tenant { get; } = new();
        public RecordingAuthorizationChanges AuthorizationChanges { get; } = new();
        public FakeWorkspaceRepository Workspaces { get; } = new();
        public User ActorUser { get; }
        public AdminService Service { get; }

        public static AdminFixture Create(SystemRole actorRole) => new(actorRole);
    }

    private sealed class RecordingAuthorizationChanges : IAuthorizationStateChangePublisher
    {
        public List<(Guid TenantId, Guid UserId, string ScopeType, Guid? ScopeId, string Change)> Items { get; } = [];
        public bool ThrowOnPublish { get; set; }

        public Task PublishAsync(
            Guid tenantId,
            Guid affectedUserId,
            string scopeType,
            Guid? scopeId,
            string change,
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnPublish)
            {
                throw new RequiredOutboxStagingException();
            }

            Items.Add((tenantId, affectedUserId, scopeType, scopeId, change));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeWorkspaceRepository : IWorkspaceRepository
    {
        public List<WorkspaceMember> Members { get; } = [];

        public Task<IReadOnlyList<Workspace>> ListForUserAsync(Guid userId, bool includeAll, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Workspace>>([]);

        public Task<Workspace?> GetByIdAsync(Guid workspaceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<Workspace?>(null);

        public Task<WorkspaceMember?> GetMemberAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Members.FirstOrDefault(item => item.WorkspaceId == workspaceId && item.UserId == userId));

        public Task<IReadOnlyList<WorkspaceMember>> ListMembersAsync(Guid workspaceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WorkspaceMember>>(Members.Where(item => item.WorkspaceId == workspaceId).ToArray());

        public Task AddAsync(Workspace workspace, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task AddMemberAsync(WorkspaceMember member, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeAdminRepository : IAdminRepository
    {
        public Dictionary<Guid, User> Users { get; } = [];
        public List<SystemSetting> Settings { get; } = [];
        public List<Invite> Invites { get; } = [];
        public bool WorkspaceExists { get; set; } = true;
        public Guid WorkspaceTenantId { get; set; } = Guid.Parse("11111111-1111-1111-1111-111111111111");
        public Project? Project { get; set; }

        public Task<PagedResponse<AdminUserListItemResponse>> ListUsersAsync(int page, int pageSize, CancellationToken cancellationToken = default)
        {
            var items = Users.Values
                .Select(user => new AdminUserListItemResponse(user.Id, user.DisplayName, user.Email, user.SystemRole, user.Status, user.LastLoginAt, user.CreatedAt, user.UpdatedAt, user.DeletedAt))
                .ToList();
            return Task.FromResult(new PagedResponse<AdminUserListItemResponse>(items, page, pageSize, items.Count));
        }

        public Task<User?> GetUserAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            Users.TryGetValue(userId, out var user);
            return Task.FromResult(user);
        }

        public Task<User?> GetUserByNormalizedEmailAsync(string normalizedEmail, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Users.Values.FirstOrDefault(user => user.NormalizedEmail == normalizedEmail));
        }

        public Task<int> CountSystemAdminsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Users.Values.Count(user => user.SystemRole == SystemRole.SystemAdmin && user.Status == UserStatus.Active && !user.DeletedAt.HasValue));
        }

        public Task<int> CountSystemAdminsExcludingAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Users.Values.Count(user => user.Id != userId && user.SystemRole == SystemRole.SystemAdmin && user.Status == UserStatus.Active && !user.DeletedAt.HasValue));
        }

        public Task<IReadOnlyList<Guid>> ListActiveSystemAdminIdsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>>(Users.Values
                .Where(user => user.SystemRole == SystemRole.SystemAdmin && user.Status == UserStatus.Active && !user.DeletedAt.HasValue)
                .Select(user => user.Id)
                .ToArray());

        public Task<PagedResponse<AdminInviteResponse>> ListInvitesAsync(int page, int pageSize, CancellationToken cancellationToken = default)
        {
            var items = Invites.Select(invite => new AdminInviteResponse(invite.Id, invite.WorkspaceId, invite.Email, invite.Role, invite.ExpiresAt, invite.AcceptedAt, invite.RevokedAt, invite.InvitedByUserId, invite.CreatedAt)).ToList();
            return Task.FromResult(new PagedResponse<AdminInviteResponse>(items, page, pageSize, items.Count));
        }

        public Task AddInviteAsync(Invite invite, CancellationToken cancellationToken = default)
        {
            Invites.Add(invite);
            return Task.CompletedTask;
        }

        public Task<Invite?> GetInviteAsync(Guid inviteId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Invites.FirstOrDefault(invite => invite.Id == inviteId));
        }

        public Task<Workspace?> GetWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken = default)
        {
            if (!WorkspaceExists)
            {
                return Task.FromResult<Workspace?>(null);
            }

            return Task.FromResult<Workspace?>(new Workspace
            {
                TenantId = WorkspaceTenantId,
                CreatedByUserId = Guid.NewGuid()
            });
        }

        public Task<Group?> GetGroupAsync(Guid groupId, CancellationToken cancellationToken = default) => Task.FromResult<Group?>(null);

        public Task<Project?> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Project?.Id == projectId ? Project : null);

        public Task<Channel?> GetChannelAsync(Guid channelId, CancellationToken cancellationToken = default) => Task.FromResult<Channel?>(null);

        public Task<IReadOnlyList<SystemSetting>> ListSettingsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<SystemSetting>>(Settings);
        }

        public Task<SystemSetting?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Settings.FirstOrDefault(setting => setting.Key == key));
        }

        public Task AddSettingAsync(SystemSetting setting, CancellationToken cancellationToken = default)
        {
            Settings.Add(setting);
            return Task.CompletedTask;
        }

        public Task<AdminDashboardSnapshot> GetDashboardSnapshotAsync(int recentCount, DateOnly today, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AdminDashboardSnapshot(0, 0, 0, 0, 0, 0, 0, 0, [], []));
        }
    }

    private sealed class FakeAuditLogger : IAuditLogger
    {
        public List<AuditLogEntry> Entries { get; } = [];

        public Task LogAsync(AuditLogEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeUserSessionService : IUserSessionService
    {
        public Task<SessionValidationResult> ValidateSessionAsync(Guid userId, Guid sessionId, Guid? tenantId, bool requireActiveTenantMembership, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SessionValidationResult.Success());
        }

        public Task<Result> RevokeSessionAsync(Guid sessionId, Guid? actorUserId, string reason, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Success());
        }

        public Task<Result<int>> RevokeUserSessionsAsync(Guid userId, Guid? actorUserId, string reason, Guid? exceptSessionId = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<int>.Success(0));
        }
    }

    private sealed class FakeCurrentUser(User user) : ICurrentUser
    {
        public Guid? UserId => user.Id;
        public Guid? SessionId => Guid.NewGuid();
        public string Email => user.Email;
        public SystemRole? SystemRole => user.SystemRole;
        public bool IsAuthenticated => true;
    }

    private sealed class FakeClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class FakeUnitOfWork : IUnitOfWork
    {
        public int SaveCount { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return Task.FromResult(1);
        }
    }
}
