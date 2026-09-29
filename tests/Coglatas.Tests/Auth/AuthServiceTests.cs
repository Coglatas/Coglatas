using Coglatas.Application.Auth;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Security;

namespace Coglatas.Tests.Auth;

public sealed class AuthServiceTests
{
    [Fact]
    public void PasswordHashingVerifiesCorrectPassword()
    {
        var hasher = new Pbkdf2PasswordHasher();
        var hash = hasher.HashPassword("CorrectHorse123");

        Assert.True(hasher.VerifyPassword(hash, "CorrectHorse123"));
        Assert.False(hasher.VerifyPassword(hash, "wrong-password"));
        Assert.DoesNotContain("CorrectHorse123", hash, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpiredInviteCannotBeUsed()
    {
        var fixture = AuthFixture.Create();
        fixture.AddInvite("invite-token", "student@example.com", expiresAt: fixture.Clock.UtcNow.AddMinutes(-1));

        var result = await fixture.Service.RegisterByInviteAsync(new RegisterByInviteRequest(
            "invite-token",
            "Student",
            "student@example.com",
            "Password123"));

        Assert.False(result.IsSuccess);
        Assert.DoesNotContain(fixture.Users.Values, user => user.Email == "student@example.com");
    }

    [Fact]
    public async Task AcceptedInviteCannotBeReused()
    {
        var fixture = AuthFixture.Create();
        fixture.AddInvite(
            "invite-token",
            "student@example.com",
            expiresAt: fixture.Clock.UtcNow.AddDays(1),
            acceptedAt: fixture.Clock.UtcNow);

        var result = await fixture.Service.RegisterByInviteAsync(new RegisterByInviteRequest(
            "invite-token",
            "Student",
            "student@example.com",
            "Password123"));

        Assert.False(result.IsSuccess);
        Assert.DoesNotContain(fixture.Users.Values, user => user.Email == "student@example.com");
    }

    [Fact]
    public async Task RevokedInviteCannotBeUsed()
    {
        var fixture = AuthFixture.Create();
        fixture.AddInvite(
            "invite-token",
            "student@example.com",
            expiresAt: fixture.Clock.UtcNow.AddDays(1),
            revokedAt: fixture.Clock.UtcNow);

        var result = await fixture.Service.RegisterByInviteAsync(new RegisterByInviteRequest(
            "invite-token",
            "Student",
            "student@example.com",
            "Password123"));

        Assert.False(result.IsSuccess);
        Assert.DoesNotContain(fixture.Users.Values, user => user.Email == "student@example.com");
    }

    [Fact]
    public async Task ValidInviteCanBeValidatedWithoutReturningToken()
    {
        var fixture = AuthFixture.Create();
        fixture.AddInvite("invite-token", "student@example.com", expiresAt: fixture.Clock.UtcNow.AddDays(1));

        var result = await fixture.Service.ValidateInviteAsync("invite-token");

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.Valid);
        Assert.Equal("student@example.com", result.Value.Email);
        Assert.Equal("Member", result.Value.Role);
        Assert.Equal("Coglatas Portal", result.Value.TenantName);
        Assert.Equal("Default Workspace", result.Value.WorkspaceName);
    }

    [Fact]
    public async Task ExpiredInviteValidationReturnsDistinctExpiredError()
    {
        var fixture = AuthFixture.Create();
        fixture.AddInvite("invite-token", "student@example.com", expiresAt: fixture.Clock.UtcNow.AddMinutes(-1));

        var result = await fixture.Service.ValidateInviteAsync("invite-token");

        Assert.False(result.IsSuccess);
        Assert.Equal("Invite has expired.", result.Error);
    }

    [Fact]
    public async Task RevokedInviteValidationReturnsDistinctRevokedError()
    {
        var fixture = AuthFixture.Create();
        fixture.AddInvite(
            "invite-token",
            "student@example.com",
            expiresAt: fixture.Clock.UtcNow.AddDays(1),
            revokedAt: fixture.Clock.UtcNow);

        var result = await fixture.Service.ValidateInviteAsync("invite-token");

        Assert.False(result.IsSuccess);
        Assert.Equal("Invite was revoked.", result.Error);
    }

    [Fact]
    public async Task UsedInviteValidationReturnsDistinctUsedError()
    {
        var fixture = AuthFixture.Create();
        fixture.AddInvite(
            "invite-token",
            "student@example.com",
            expiresAt: fixture.Clock.UtcNow.AddDays(1),
            acceptedAt: fixture.Clock.UtcNow);

        var result = await fixture.Service.ValidateInviteAsync("invite-token");

        Assert.False(result.IsSuccess);
        Assert.Equal("Invite has already been used.", result.Error);
    }

    [Fact]
    public async Task AcceptInviteCreatesUserMembershipsSessionAndMarksInviteUsed()
    {
        var fixture = AuthFixture.Create();
        var invite = fixture.AddInvite("invite-token", "student@example.com", expiresAt: fixture.Clock.UtcNow.AddDays(1));

        var result = await fixture.Service.AcceptInviteAsync(new AcceptInviteRequest(
            "invite-token",
            "Student",
            "Password123"));

        Assert.True(result.IsSuccess);
        Assert.Equal(fixture.Clock.UtcNow, invite.AcceptedAt);
        var user = Assert.Single(fixture.Users.Values, user => user.Email == "student@example.com");
        Assert.True(fixture.PasswordHasher.VerifyPassword(user.PasswordHash, "Password123"));
        Assert.Contains(fixture.TenantUsers, membership => membership.TenantId == invite.TenantId && membership.UserId == user.Id && membership.Status == TenantUserStatus.Active);
        Assert.Contains(fixture.WorkspaceMembers, membership => membership.WorkspaceId == invite.WorkspaceId && membership.UserId == user.Id && membership.Status == MembershipStatus.Active);
        Assert.Single(fixture.Sessions);
        Assert.Equal(invite.WorkspaceId, result.Value!.CurrentWorkspace?.Id);
        Assert.Contains(result.Value.Workspaces, workspace => workspace.Id == invite.WorkspaceId);
    }

    [Fact]
    public async Task UsedInviteCannotBeAcceptedAgain()
    {
        var fixture = AuthFixture.Create();
        fixture.AddInvite("invite-token", "student@example.com", expiresAt: fixture.Clock.UtcNow.AddDays(1));

        var first = await fixture.Service.AcceptInviteAsync(new AcceptInviteRequest("invite-token", "Student", "Password123"));
        var second = await fixture.Service.AcceptInviteAsync(new AcceptInviteRequest("invite-token", "Student", "Password123"));

        Assert.True(first.IsSuccess);
        Assert.False(second.IsSuccess);
        Assert.Equal("Invite has already been used.", second.Error);
        Assert.Single(fixture.Sessions);
    }

    [Fact]
    public async Task ExpiredInviteCannotBeAccepted()
    {
        var fixture = AuthFixture.Create();
        fixture.AddInvite("invite-token", "student@example.com", expiresAt: fixture.Clock.UtcNow.AddMinutes(-1));

        var result = await fixture.Service.AcceptInviteAsync(new AcceptInviteRequest(
            "invite-token",
            "Student",
            "Password123"));

        Assert.False(result.IsSuccess);
        Assert.Equal("Invite has expired.", result.Error);
        Assert.DoesNotContain(fixture.Users.Values, user => user.Email == "student@example.com");
        Assert.Empty(fixture.Sessions);
    }

    [Fact]
    public async Task AcceptedInviteUserCanLoginWithCreatedPassword()
    {
        var fixture = AuthFixture.Create();
        fixture.AddInvite("invite-token", "student@example.com", expiresAt: fixture.Clock.UtcNow.AddDays(1));

        var accept = await fixture.Service.AcceptInviteAsync(new AcceptInviteRequest(
            "invite-token",
            "Student",
            "Password123"));
        var login = await fixture.Service.LoginAsync(new LoginRequest("student@example.com", "Password123"));

        Assert.True(accept.IsSuccess);
        Assert.True(login.IsSuccess);
        Assert.Equal("student@example.com", login.Value!.Email);
        Assert.Equal(2, fixture.Sessions.Count);
    }

    [Fact]
    public async Task SuspendedUserCannotLogin()
    {
        var fixture = AuthFixture.Create();
        fixture.AddUser("student@example.com", "Password123", UserStatus.Suspended);

        var result = await fixture.Service.LoginAsync(new LoginRequest("student@example.com", "Password123"));

        Assert.False(result.IsSuccess);
        Assert.Equal("Invalid email or password.", result.Error);
        Assert.Empty(fixture.Sessions);
    }

    [Fact]
    public async Task FailedLoginSecurityMetadataDoesNotStoreSubmittedEmail()
    {
        var fixture = AuthFixture.Create();
        fixture.AddUser("student@example.com", "Password123", UserStatus.Active);

        var result = await fixture.Service.LoginAsync(new LoginRequest("student@example.com", "wrong-password"));

        Assert.False(result.IsSuccess);
        var securityEvent = Assert.Single(fixture.AuditLogger.Entries, entry => entry.EntityType == "SecurityEvent" && entry.Action == "LoginFailure");
        Assert.NotNull(securityEvent.Metadata);
        Assert.DoesNotContain(securityEvent.Metadata!, pair => string.Equals(pair.Key, "email", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(securityEvent.Metadata!.Values, value => string.Equals(value?.ToString(), "student@example.com", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(securityEvent.Metadata!, pair => pair.Key == "emailProvided" && pair.Value is true);
    }

    [Fact]
    public async Task ArchivedUserCannotLogin()
    {
        var fixture = AuthFixture.Create();
        fixture.AddUser("student@example.com", "Password123", UserStatus.Archived);

        var result = await fixture.Service.LoginAsync(new LoginRequest("student@example.com", "Password123"));

        Assert.False(result.IsSuccess);
        Assert.Equal("Invalid email or password.", result.Error);
        Assert.Empty(fixture.Sessions);
    }

    [Fact]
    public async Task DeletedUserCannotLogin()
    {
        var fixture = AuthFixture.Create();
        var user = fixture.AddUser("student@example.com", "Password123", UserStatus.Active);
        user.MarkDeleted(fixture.Clock.UtcNow);

        var result = await fixture.Service.LoginAsync(new LoginRequest("student@example.com", "Password123"));

        Assert.False(result.IsSuccess);
        Assert.Equal("Invalid email or password.", result.Error);
        Assert.Empty(fixture.Sessions);
    }

    [Fact]
    public async Task FailedPasswordAttemptsLockActiveUser()
    {
        var fixture = AuthFixture.Create(new AuthSecurityOptions
        {
            LoginLockoutEnabled = true,
            MaxFailedLoginAttempts = 2,
            LoginLockoutDurationMinutes = 30
        });
        var user = fixture.AddUser("student@example.com", "Password123", UserStatus.Active);

        var first = await fixture.Service.LoginAsync(new LoginRequest("student@example.com", "wrong-password"));
        var second = await fixture.Service.LoginAsync(new LoginRequest("student@example.com", "wrong-password"));
        var lockedOut = await fixture.Service.LoginAsync(new LoginRequest("student@example.com", "Password123"));

        Assert.False(first.IsSuccess);
        Assert.False(second.IsSuccess);
        Assert.False(lockedOut.IsSuccess);
        Assert.Equal(2, user.FailedLoginAttempts);
        Assert.Equal(fixture.Clock.UtcNow.AddMinutes(30), user.LockoutEndAt);
        Assert.Empty(fixture.Sessions);
    }

    [Fact]
    public async Task SuccessfulLoginResetsFailedAttemptState()
    {
        var fixture = AuthFixture.Create(new AuthSecurityOptions
        {
            LoginLockoutEnabled = true,
            MaxFailedLoginAttempts = 3,
            LoginLockoutDurationMinutes = 15
        });
        var user = fixture.AddUser("student@example.com", "Password123", UserStatus.Active);

        await fixture.Service.LoginAsync(new LoginRequest("student@example.com", "wrong-password"));
        var result = await fixture.Service.LoginAsync(new LoginRequest("student@example.com", "Password123"));

        Assert.True(result.IsSuccess);
        Assert.Equal(0, user.FailedLoginAttempts);
        Assert.Null(user.LockoutEndAt);
        Assert.Single(fixture.Sessions);
    }

    [Fact]
    public async Task NormalUserLoginReceivesProjectNavigationCapabilitiesWithoutAdminGrants()
    {
        var fixture = AuthFixture.Create();
        fixture.AddUser("student@example.com", "Password123", UserStatus.Active);

        var result = await fixture.Service.LoginAsync(new LoginRequest("student@example.com", "Password123"));

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Contains("workspace:view", result.Value.Capabilities);
        Assert.Contains("announcements:view", result.Value.Capabilities);
        Assert.Contains("projects:view", result.Value.Capabilities);
        Assert.Contains("files:view", result.Value.Capabilities);
        Assert.Contains("account:view", result.Value.Capabilities);
        Assert.DoesNotContain("admin:access", result.Value.Capabilities);
        Assert.DoesNotContain("invite:create", result.Value.Capabilities);
    }

    [Fact]
    public async Task WorkspaceContextAutoSelectsOnlyExactlyOneAuthorizedActiveWorkspace()
    {
        var fixture = AuthFixture.Create();
        var user = fixture.AddUser("student@example.com", "Password123", UserStatus.Active);
        var soleActiveWorkspace = fixture.AddWorkspaceForUser(user, "Sole active Workspace");
        fixture.AddWorkspaceForUser(
            user,
            "Archived Workspace",
            WorkspaceStatus.Archived);

        var login = await fixture.Service.LoginAsync(
            new LoginRequest("student@example.com", "Password123"));

        Assert.True(login.IsSuccess, login.Error);
        Assert.Equal(soleActiveWorkspace.Id, login.Value!.CurrentWorkspace?.Id);
        Assert.Equal(soleActiveWorkspace.Id, Assert.Single(login.Value.Workspaces).Id);

        var secondActiveWorkspace = fixture.AddWorkspaceForUser(user, "Second active Workspace");
        fixture.CurrentUser.SetUser(user);

        var currentUser = await fixture.Service.GetCurrentUserAsync();

        Assert.True(currentUser.IsSuccess, currentUser.Error);
        Assert.Null(currentUser.Value!.CurrentWorkspace);
        Assert.Equal(2, currentUser.Value.Workspaces.Count);
        Assert.Contains(currentUser.Value.Workspaces, workspace => workspace.Id == soleActiveWorkspace.Id);
        Assert.Contains(currentUser.Value.Workspaces, workspace => workspace.Id == secondActiveWorkspace.Id);
    }

    [Fact]
    public async Task ExpiredLockoutIsClearedBeforePasswordVerification()
    {
        var fixture = AuthFixture.Create(new AuthSecurityOptions
        {
            LoginLockoutEnabled = true,
            MaxFailedLoginAttempts = 2,
            LoginLockoutDurationMinutes = 30
        });
        var user = fixture.AddUser("student@example.com", "Password123", UserStatus.Active);

        await fixture.Service.LoginAsync(new LoginRequest("student@example.com", "wrong-password"));
        await fixture.Service.LoginAsync(new LoginRequest("student@example.com", "wrong-password"));
        fixture.Clock.Advance(TimeSpan.FromMinutes(31));

        var result = await fixture.Service.LoginAsync(new LoginRequest("student@example.com", "Password123"));

        Assert.True(result.IsSuccess);
        Assert.Equal(0, user.FailedLoginAttempts);
        Assert.Null(user.LockoutEndAt);
        Assert.Single(fixture.Sessions);
    }

    private sealed class AuthFixture
    {
        private AuthFixture(AuthSecurityOptions? securityOptions)
        {
            CurrentUser = new FakeCurrentUser();
            Service = new AuthService(
                new FakeUserRepository(Users),
                new FakeInviteRepository(Invites),
                new FakeTenantRepository(Tenants, TenantUsers),
                new FakeWorkspaceRepository(Workspaces, WorkspaceMembers),
                new FakeSessionRepository(Sessions),
                new FakeUserSessionService(),
                PasswordHasher,
                TokenHasher,
                AuditLogger,
                CurrentUser,
                Clock,
                new FakeUnitOfWork(),
                securityOptions ?? new AuthSecurityOptions());
        }

        public Dictionary<Guid, User> Users { get; } = [];
        private Dictionary<string, Invite> Invites { get; } = [];
        private Dictionary<Guid, Tenant> Tenants { get; } = [];
        private Dictionary<Guid, Workspace> Workspaces { get; } = [];
        public List<TenantUser> TenantUsers { get; } = [];
        public List<WorkspaceMember> WorkspaceMembers { get; } = [];
        public List<Session> Sessions { get; } = [];
        public FakeClock Clock { get; } = new(new DateTimeOffset(2026, 6, 6, 0, 0, 0, TimeSpan.Zero));
        public Pbkdf2PasswordHasher PasswordHasher { get; } = new();
        private Sha256TokenHasher TokenHasher { get; } = new();
        public FakeAuditLogger AuditLogger { get; } = new();
        public FakeCurrentUser CurrentUser { get; }
        public AuthService Service { get; }

        public static AuthFixture Create(AuthSecurityOptions? securityOptions = null) => new(securityOptions);

        public User AddUser(string email, string password, UserStatus status)
        {
            var user = new User
            {
                DisplayName = "Student",
                Email = email,
                NormalizedEmail = email.ToUpperInvariant(),
                PasswordHash = PasswordHasher.HashPassword(password),
                SystemRole = SystemRole.User,
                Status = status
            };

            Users[user.Id] = user;
            return user;
        }

        public Workspace AddWorkspaceForUser(
            User user,
            string name,
            WorkspaceStatus status = WorkspaceStatus.Active)
        {
            var tenant = Tenants.Values.FirstOrDefault();
            if (tenant is null)
            {
                tenant = new Tenant
                {
                    Name = "Workspace context Tenant",
                    DisplayName = "Workspace context Tenant",
                    Slug = $"workspace-context-{Guid.NewGuid():N}",
                    Status = TenantStatus.Active
                };
                Tenants[tenant.Id] = tenant;
            }

            var workspace = new Workspace
            {
                TenantId = tenant.Id,
                Name = name,
                Slug = $"workspace-{Guid.NewGuid():N}",
                Status = status,
                CreatedByUserId = user.Id,
                CreatedAt = Clock.UtcNow.AddMinutes(Workspaces.Count)
            };
            Workspaces[workspace.Id] = workspace;
            WorkspaceMembers.Add(new WorkspaceMember
            {
                TenantId = workspace.TenantId,
                WorkspaceId = workspace.Id,
                UserId = user.Id,
                Role = WorkspaceRole.Member,
                Status = MembershipStatus.Active,
                JoinedAt = Clock.UtcNow,
                CreatedAt = Clock.UtcNow
            });

            return workspace;
        }

        public Invite AddInvite(
            string token,
            string email,
            DateTimeOffset expiresAt,
            DateTimeOffset? acceptedAt = null,
            DateTimeOffset? revokedAt = null)
        {
            var tenant = new Tenant
            {
                Name = "Coglatas Portal",
                DisplayName = "Coglatas Portal",
                Slug = "coglatas-portal",
                Status = TenantStatus.Active
            };
            Tenants[tenant.Id] = tenant;
            var workspace = new Workspace
            {
                TenantId = tenant.Id,
                Name = "Default Workspace",
                Slug = "default-workspace",
                Status = WorkspaceStatus.Active,
                CreatedByUserId = Guid.NewGuid()
            };
            var workspaceId = workspace.Id;
            Workspaces[workspaceId] = workspace;
            var invite = new Invite
            {
                TenantId = tenant.Id,
                WorkspaceId = workspaceId,
                Email = email,
                NormalizedEmail = email.ToUpperInvariant(),
                TokenHash = TokenHasher.HashToken(token),
                Role = WorkspaceRole.Member,
                ExpiresAt = expiresAt,
                AcceptedAt = acceptedAt,
                RevokedAt = revokedAt,
                InvitedByUserId = Guid.NewGuid()
            };

            Invites[invite.TokenHash] = invite;
            return invite;
        }
    }

    private sealed class FakeUserRepository(Dictionary<Guid, User> users) : IUserRepository
    {
        public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            users.TryGetValue(id, out var user);
            return Task.FromResult(user);
        }

        public Task<User?> GetByNormalizedEmailAsync(string normalizedEmail, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(users.Values.FirstOrDefault(user => user.NormalizedEmail == normalizedEmail));
        }

        public Task AddAsync(User user, CancellationToken cancellationToken = default)
        {
            users[user.Id] = user;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeInviteRepository(Dictionary<string, Invite> invites) : IInviteRepository
    {
        public Task<Invite?> GetByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default)
        {
            invites.TryGetValue(tokenHash, out var invite);
            return Task.FromResult(invite);
        }
    }

    private sealed class FakeSessionRepository(List<Session> sessions) : ISessionRepository
    {
        public Task AddAsync(Session session, CancellationToken cancellationToken = default)
        {
            sessions.Add(session);
            return Task.CompletedTask;
        }

        public Task<Session?> GetByIdWithUserAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(sessions.FirstOrDefault(item => item.Id == sessionId));
        }

        public Task<bool> RevokeAsync(Guid sessionId, DateTimeOffset revokedAt, CancellationToken cancellationToken = default)
        {
            var session = sessions.FirstOrDefault(item => item.Id == sessionId);
            if (session is not null)
            {
                session.RevokedAt = revokedAt;
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }

        public Task<int> RevokeUserSessionsAsync(Guid userId, DateTimeOffset revokedAt, Guid? exceptSessionId = null, CancellationToken cancellationToken = default)
        {
            var revoked = 0;
            foreach (var session in sessions.Where(item => item.UserId == userId && item.Id != exceptSessionId && !item.RevokedAt.HasValue))
            {
                session.RevokedAt = revokedAt;
                revoked++;
            }

            return Task.FromResult(revoked);
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

    private sealed class FakeAuditLogger : IAuditLogger
    {
        public List<AuditLogEntry> Entries { get; } = [];

        public Task LogAsync(AuditLogEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTenantRepository(
        Dictionary<Guid, Tenant> tenants,
        List<TenantUser> tenantUsers) : ITenantRepository
    {
        public Task<IReadOnlyList<Tenant>> ListTenantsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<Tenant>>(tenants.Values.ToList());
        }

        public Task<Tenant?> GetTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
        {
            tenants.TryGetValue(tenantId, out var tenant);
            return Task.FromResult(tenant);
        }

        public Task<Tenant?> GetTenantBySlugAsync(string slug, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(tenants.Values.FirstOrDefault(tenant => tenant.Slug == slug));
        }

        public Task<Tenant?> GetTenantByPrimaryDomainAsync(string primaryDomain, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(tenants.Values.FirstOrDefault(tenant => tenant.PrimaryDomain == primaryDomain));
        }

        public Task AddTenantAsync(Tenant tenant, CancellationToken cancellationToken = default)
        {
            tenants[tenant.Id] = tenant;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TenantUser>> ListTenantUsersAsync(Guid tenantId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<TenantUser>>(tenantUsers.Where(item => item.TenantId == tenantId).ToList());
        }

        public Task<IReadOnlyList<TenantUser>> ListUserTenantMembershipsAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<TenantUser>>(tenantUsers.Where(item => item.UserId == userId).ToList());
        }

        public Task<TenantUser?> GetTenantUserAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(tenantUsers.FirstOrDefault(item => item.TenantId == tenantId && item.UserId == userId));
        }

        public Task AddTenantUserAsync(TenantUser tenantUser, CancellationToken cancellationToken = default)
        {
            tenantUsers.Add(tenantUser);
            return Task.CompletedTask;
        }

        public Task<User?> GetUserAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<User?>(null);
        }
    }

    private sealed class FakeWorkspaceRepository(
        Dictionary<Guid, Workspace> workspaces,
        List<WorkspaceMember> members) : IWorkspaceRepository
    {
        public Task<IReadOnlyList<Workspace>> ListForUserAsync(Guid userId, bool includeAll, CancellationToken cancellationToken = default)
        {
            var workspaceIds = members
                .Where(item => item.UserId == userId && item.Status == MembershipStatus.Active)
                .Select(item => item.WorkspaceId)
                .Distinct()
                .ToHashSet();

            var items = workspaces.Values
                .Where(workspace => includeAll || workspaceIds.Contains(workspace.Id))
                .ToList();

            return Task.FromResult<IReadOnlyList<Workspace>>(items);
        }

        public Task<Workspace?> GetByIdAsync(Guid workspaceId, CancellationToken cancellationToken = default)
        {
            workspaces.TryGetValue(workspaceId, out var workspace);
            return Task.FromResult(workspace);
        }

        public Task<WorkspaceMember?> GetMemberAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(members.FirstOrDefault(item => item.WorkspaceId == workspaceId && item.UserId == userId));
        }

        public Task<IReadOnlyList<WorkspaceMember>> ListMembersAsync(Guid workspaceId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<WorkspaceMember>>(members.Where(item => item.WorkspaceId == workspaceId).ToList());
        }

        public Task AddAsync(Workspace workspace, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task AddMemberAsync(WorkspaceMember member, CancellationToken cancellationToken = default)
        {
            members.Add(member);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCurrentUser : ICurrentUser
    {
        private User? _user;

        public Guid? UserId => _user?.Id;
        public Guid? SessionId => _user is null ? null : Guid.NewGuid();
        public string? Email => _user?.Email;
        public SystemRole? SystemRole => _user?.SystemRole;
        public bool IsAuthenticated => _user is not null;

        public void SetUser(User currentUser)
        {
            _user = currentUser;
        }
    }

    private sealed class FakeClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = utcNow;

        public void Advance(TimeSpan amount)
        {
            UtcNow = UtcNow.Add(amount);
        }
    }

    private sealed class FakeUnitOfWork : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(1);
        }
    }
}
