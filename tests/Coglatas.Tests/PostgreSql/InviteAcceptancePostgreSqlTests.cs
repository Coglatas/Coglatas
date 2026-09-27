using Coglatas.Application.Auth;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Audit;
using Coglatas.Infrastructure.Persistence;
using Coglatas.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Tests.PostgreSql;

[Trait("Category", "PostgreSQLIntegration")]
[Trait("Scope", "Issue527")]
public sealed class InviteAcceptancePostgreSqlTests
{
    private const string Token = "issue-527-invite-token";
    private const string InviteEmail = "invitee@example.com";
    private const string InvitePassword = "InvitePassword123";
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 3, 0, 0, TimeSpan.Zero);

    [PostgreSqlFact]
    public async Task NewUserRegistrationCommitsUserMembershipsInviteSessionAndMetadataOnlyAuditTogether()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            var graph = await SeedInviteAsync(database);

            await using (var scope = CreateTenantScope(database, graph.Tenant))
            {
                var service = CreateService(scope.Context, scope.CurrentTenant);
                var result = await service.RegisterByInviteAsync(new RegisterByInviteRequest(
                    Token,
                    "Invitee",
                    InviteEmail,
                    InvitePassword));

                Assert.True(result.IsSuccess, result.Error);
                Assert.Equal(graph.Workspace.Id, result.Value!.CurrentWorkspace?.Id);
            }

            await using var verify = PostgreSqlMigrationTestDatabase.CreatePlatformContext(database);
            var invite = await verify.Invites.SingleAsync(item => item.Id == graph.Invite.Id);
            var user = await verify.Users.SingleAsync(item => item.NormalizedEmail == InviteEmail.ToUpperInvariant());
            var tenantMembership = await verify.TenantUsers.SingleAsync(item => item.TenantId == graph.Tenant.Id && item.UserId == user.Id);
            var workspaceMembership = await verify.WorkspaceMembers.SingleAsync(item => item.WorkspaceId == graph.Workspace.Id && item.UserId == user.Id);

            Assert.NotNull(invite.AcceptedAt);
            Assert.Equal(TenantUserStatus.Active, tenantMembership.Status);
            Assert.Equal(TenantUserRole.Member, tenantMembership.Role);
            Assert.Equal(MembershipStatus.Active, workspaceMembership.Status);
            Assert.Equal(WorkspaceRole.Member, workspaceMembership.Role);
            Assert.Equal(1, await verify.Sessions.CountAsync(item => item.UserId == user.Id));

            var auditRows = await verify.AuditLogs
                .IgnoreQueryFilters()
                .Where(item => item.Action == "InviteAccepted")
                .ToListAsync();
            Assert.NotEmpty(auditRows);
            Assert.All(auditRows, row =>
            {
                Assert.DoesNotContain(Token, row.MetadataJson ?? string.Empty, StringComparison.Ordinal);
                Assert.DoesNotContain(InviteEmail, row.MetadataJson ?? string.Empty, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(InvitePassword, row.MetadataJson ?? string.Empty, StringComparison.Ordinal);
            });
        });
    }

    [PostgreSqlFact]
    public async Task ConcurrentWorkspaceAdminAcceptanceForExistingEligibleUserSerializesWithoutTenantRoleEscalation()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            var graph = await SeedInviteAsync(database, seedExistingEligibleUser: true, role: WorkspaceRole.Admin);
            var originalPasswordHash = graph.ExistingUser!.PasswordHash;

            await using var firstScope = CreateTenantScope(database, graph.Tenant);
            await using var secondScope = CreateTenantScope(database, graph.Tenant);
            var firstService = CreateService(firstScope.Context, firstScope.CurrentTenant);
            var secondService = CreateService(secondScope.Context, secondScope.CurrentTenant);

            var results = await Task.WhenAll(
                firstService.AcceptInviteAsync(new AcceptInviteRequest(Token, "Ignored display name", InvitePassword)),
                secondService.AcceptInviteAsync(new AcceptInviteRequest(Token, "Ignored display name", InvitePassword)));

            var success = Assert.Single(results, result => result.IsSuccess);
            var denied = Assert.Single(results, result => !result.IsSuccess);
            Assert.NotNull(success.Value);
            Assert.Equal("Invite has already been used.", denied.Error);

            await using var verify = PostgreSqlMigrationTestDatabase.CreatePlatformContext(database);
            var persistedUser = await verify.Users.SingleAsync(item => item.Id == graph.ExistingUser.Id);
            var tenantMembership = await verify.TenantUsers.SingleAsync(item => item.TenantId == graph.Tenant.Id && item.UserId == persistedUser.Id);
            var workspaceMembership = await verify.WorkspaceMembers.SingleAsync(item => item.WorkspaceId == graph.Workspace.Id && item.UserId == persistedUser.Id);

            Assert.Equal(1, await verify.Users.CountAsync(item => item.NormalizedEmail == InviteEmail.ToUpperInvariant()));
            Assert.Equal(1, await verify.Sessions.CountAsync(item => item.UserId == persistedUser.Id));
            Assert.Equal(originalPasswordHash, persistedUser.PasswordHash);
            Assert.Equal(TenantUserRole.Member, tenantMembership.Role);
            Assert.Equal(TenantUserStatus.Active, tenantMembership.Status);
            Assert.Equal(WorkspaceRole.Admin, workspaceMembership.Role);
            Assert.Equal(MembershipStatus.Active, workspaceMembership.Status);
            Assert.NotNull((await verify.Invites.SingleAsync(item => item.Id == graph.Invite.Id)).AcceptedAt);
        });
    }

    [PostgreSqlFact]
    public async Task AdministrativeTenantLifecycleStatesCannotBeReactivatedByInvite()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        var blockedStatuses = new[]
        {
            TenantUserStatus.Suspended,
            TenantUserStatus.Left,
            TenantUserStatus.Archived
        };

        foreach (var blockedStatus in blockedStatuses)
        {
            await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
            {
                await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
                var graph = await SeedInviteAsync(
                    database,
                    seedExistingEligibleUser: true,
                    existingTenantStatus: blockedStatus);

                await using (var scope = CreateTenantScope(database, graph.Tenant))
                {
                    var service = CreateService(scope.Context, scope.CurrentTenant);
                    var result = await service.AcceptInviteAsync(new AcceptInviteRequest(Token, "Ignored", InvitePassword));

                    Assert.False(result.IsSuccess);
                    Assert.Equal("Invite is invalid or expired.", result.Error);
                }

                await using var verify = PostgreSqlMigrationTestDatabase.CreatePlatformContext(database);
                var tenantMembership = await verify.TenantUsers.SingleAsync(item =>
                    item.TenantId == graph.Tenant.Id && item.UserId == graph.ExistingUser!.Id);
                var workspaceMembership = await verify.WorkspaceMembers.SingleAsync(item =>
                    item.WorkspaceId == graph.Workspace.Id && item.UserId == graph.ExistingUser!.Id);

                Assert.Equal(blockedStatus, tenantMembership.Status);
                Assert.Equal(MembershipStatus.Active, workspaceMembership.Status);
                Assert.False(await verify.Sessions.AnyAsync(item => item.UserId == graph.ExistingUser!.Id));
                Assert.Null((await verify.Invites.SingleAsync(item => item.Id == graph.Invite.Id)).AcceptedAt);

                var denialMetadata = await verify.AuditLogs
                    .IgnoreQueryFilters()
                    .Where(item => item.Action == "InviteAcceptanceDenied")
                    .Select(item => item.MetadataJson)
                    .ToListAsync();
                Assert.Contains(denialMetadata, metadata =>
                    metadata?.Contains("TenantMembershipUnavailable", StringComparison.Ordinal) == true);
            });
        }
    }

    [PostgreSqlFact]
    public async Task SuspendedWorkspaceMembershipCannotBeReactivatedByInvite()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            var graph = await SeedInviteAsync(
                database,
                seedExistingEligibleUser: true,
                existingWorkspaceStatus: MembershipStatus.Suspended);

            await using (var scope = CreateTenantScope(database, graph.Tenant))
            {
                var service = CreateService(scope.Context, scope.CurrentTenant);
                var result = await service.AcceptInviteAsync(new AcceptInviteRequest(Token, "Ignored", InvitePassword));

                Assert.False(result.IsSuccess);
                Assert.Equal("Invite is invalid or expired.", result.Error);
            }

            await using var verify = PostgreSqlMigrationTestDatabase.CreatePlatformContext(database);
            var tenantMembership = await verify.TenantUsers.SingleAsync(item =>
                item.TenantId == graph.Tenant.Id && item.UserId == graph.ExistingUser!.Id);
            var workspaceMembership = await verify.WorkspaceMembers.SingleAsync(item =>
                item.WorkspaceId == graph.Workspace.Id && item.UserId == graph.ExistingUser!.Id);

            Assert.Equal(TenantUserStatus.Active, tenantMembership.Status);
            Assert.Equal(MembershipStatus.Suspended, workspaceMembership.Status);
            Assert.False(await verify.Sessions.AnyAsync(item => item.UserId == graph.ExistingUser!.Id));
            Assert.Null((await verify.Invites.SingleAsync(item => item.Id == graph.Invite.Id)).AcceptedAt);

            var denialMetadata = await verify.AuditLogs
                .IgnoreQueryFilters()
                .Where(item => item.Action == "InviteAcceptanceDenied")
                .Select(item => item.MetadataJson)
                .ToListAsync();
            Assert.Contains(denialMetadata, metadata =>
                metadata?.Contains("WorkspaceMembershipUnavailable", StringComparison.Ordinal) == true);
        });
    }

    [PostgreSqlFact]
    public async Task InvitedTenantAndPendingWorkspaceMembershipCanActivateFromInvite()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            var graph = await SeedInviteAsync(
                database,
                seedExistingEligibleUser: true,
                role: WorkspaceRole.Admin,
                existingTenantStatus: TenantUserStatus.Invited,
                existingWorkspaceStatus: MembershipStatus.Pending);

            await using (var scope = CreateTenantScope(database, graph.Tenant))
            {
                var service = CreateService(scope.Context, scope.CurrentTenant);
                var result = await service.AcceptInviteAsync(new AcceptInviteRequest(Token, "Ignored", InvitePassword));

                Assert.True(result.IsSuccess, result.Error);
            }

            await using var verify = PostgreSqlMigrationTestDatabase.CreatePlatformContext(database);
            var tenantMembership = await verify.TenantUsers.SingleAsync(item =>
                item.TenantId == graph.Tenant.Id && item.UserId == graph.ExistingUser!.Id);
            var workspaceMembership = await verify.WorkspaceMembers.SingleAsync(item =>
                item.WorkspaceId == graph.Workspace.Id && item.UserId == graph.ExistingUser!.Id);

            Assert.Equal(TenantUserStatus.Active, tenantMembership.Status);
            Assert.Equal(TenantUserRole.Member, tenantMembership.Role);
            Assert.Equal(MembershipStatus.Active, workspaceMembership.Status);
            Assert.Equal(WorkspaceRole.Admin, workspaceMembership.Role);
            Assert.Equal(1, await verify.Sessions.CountAsync(item => item.UserId == graph.ExistingUser!.Id));
            Assert.NotNull((await verify.Invites.SingleAsync(item => item.Id == graph.Invite.Id)).AcceptedAt);
        });
    }

    [PostgreSqlFact]
    public async Task CrossTenantInviteFailsClosedWithoutCreatingIdentityOrMembershipState()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            var graph = await SeedInviteAsync(database, crossTenantWorkspace: true);

            await using (var scope = CreateTenantScope(database, graph.Tenant))
            {
                var service = CreateService(scope.Context, scope.CurrentTenant);
                var result = await service.AcceptInviteAsync(new AcceptInviteRequest(Token, "Invitee", InvitePassword));

                Assert.False(result.IsSuccess);
                Assert.Equal("Invite is invalid or expired.", result.Error);
            }

            await using var verify = PostgreSqlMigrationTestDatabase.CreatePlatformContext(database);
            Assert.False(await verify.Users.AnyAsync(item => item.NormalizedEmail == InviteEmail.ToUpperInvariant()));
            Assert.False(await verify.TenantUsers.AnyAsync(item => item.TenantId == graph.Tenant.Id));
            Assert.False(await verify.WorkspaceMembers.AnyAsync(item => item.UserId != graph.Inviter.Id));
            Assert.False(await verify.Sessions.AnyAsync());
            Assert.Null((await verify.Invites.SingleAsync(item => item.Id == graph.Invite.Id)).AcceptedAt);

            var denialRows = await verify.AuditLogs
                .IgnoreQueryFilters()
                .Where(item => item.Action == "InviteAcceptanceDenied")
                .ToListAsync();
            Assert.NotEmpty(denialRows);
            Assert.All(denialRows, row =>
            {
                Assert.DoesNotContain(Token, row.MetadataJson ?? string.Empty, StringComparison.Ordinal);
                Assert.DoesNotContain(InviteEmail, row.MetadataJson ?? string.Empty, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(InvitePassword, row.MetadataJson ?? string.Empty, StringComparison.Ordinal);
            });
        });
    }

    [PostgreSqlFact]
    public async Task PersistenceFailureRollsBackInviteUserMembershipAndSessionTogether()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            var graph = await SeedInviteAsync(database);
            var overlongDisplayName = new string('x', 121);

            await using (var scope = CreateTenantScope(database, graph.Tenant))
            {
                var service = CreateService(scope.Context, scope.CurrentTenant);
                await Assert.ThrowsAsync<DbUpdateException>(() =>
                    service.AcceptInviteAsync(new AcceptInviteRequest(Token, overlongDisplayName, InvitePassword)));
            }

            await using var verify = PostgreSqlMigrationTestDatabase.CreatePlatformContext(database);
            Assert.False(await verify.Users.AnyAsync(item => item.NormalizedEmail == InviteEmail.ToUpperInvariant()));
            Assert.False(await verify.TenantUsers.AnyAsync(item => item.TenantId == graph.Tenant.Id));
            Assert.False(await verify.WorkspaceMembers.AnyAsync(item => item.WorkspaceId == graph.Workspace.Id));
            Assert.False(await verify.Sessions.AnyAsync());
            Assert.Null((await verify.Invites.SingleAsync(item => item.Id == graph.Invite.Id)).AcceptedAt);
            Assert.False(await verify.AuditLogs.IgnoreQueryFilters().AnyAsync(item => item.Action == "InviteAccepted"));
        });
    }

    private static AuthService CreateService(AppDbContext context, CurrentTenantService currentTenant)
    {
        var currentUser = new AnonymousCurrentUser();
        var clock = new FixedClock(Now);
        return new AuthService(
            new UserRepository(context),
            new InviteRepository(context),
            new TenantRepository(context),
            new WorkspaceRepository(context),
            new SessionRepository(context),
            new NoopUserSessionService(),
            new Pbkdf2PasswordHasher(),
            new Sha256TokenHasher(),
            new DbAuditLogger(context, clock, currentUser, currentTenant),
            currentUser,
            clock,
            new EfUnitOfWork(context),
            new AuthSecurityOptions());
    }

    private static TenantScope CreateTenantScope(string connectionString, Tenant tenant)
    {
        var currentTenant = new CurrentTenantService();
        currentTenant.SetTenant(tenant.Id, tenant.Slug);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new TenantScope(new AppDbContext(options, currentTenant), currentTenant);
    }

    private static async Task<InviteGraph> SeedInviteAsync(
        string connectionString,
        bool seedExistingEligibleUser = false,
        bool crossTenantWorkspace = false,
        WorkspaceRole role = WorkspaceRole.Member,
        TenantUserStatus existingTenantStatus = TenantUserStatus.Active,
        MembershipStatus existingWorkspaceStatus = MembershipStatus.Active)
    {
        var hasher = new Pbkdf2PasswordHasher();
        var tokenHasher = new Sha256TokenHasher();
        await using var context = PostgreSqlMigrationTestDatabase.CreatePlatformContext(connectionString);

        var tenant = new Tenant
        {
            Name = "Issue 527 Tenant",
            DisplayName = "Issue 527 Tenant",
            Slug = $"issue-527-{Guid.NewGuid():N}",
            Status = TenantStatus.Active
        };
        var workspaceTenant = crossTenantWorkspace
            ? new Tenant
            {
                Name = "Other Tenant",
                DisplayName = "Other Tenant",
                Slug = $"issue-527-other-{Guid.NewGuid():N}",
                Status = TenantStatus.Active
            }
            : tenant;
        var inviter = new User
        {
            DisplayName = "Inviter",
            Email = $"inviter-{Guid.NewGuid():N}@example.com",
            NormalizedEmail = $"INVITER-{Guid.NewGuid():N}@EXAMPLE.COM",
            PasswordHash = hasher.HashPassword("InviterPassword123"),
            SystemRole = SystemRole.User,
            Status = UserStatus.Active
        };
        // Keep Email/NormalizedEmail a true normalized pair while retaining a unique value.
        inviter.NormalizedEmail = inviter.Email.ToUpperInvariant();

        var workspace = new Workspace
        {
            TenantId = workspaceTenant.Id,
            Name = "Invite Workspace",
            Slug = $"invite-workspace-{Guid.NewGuid():N}",
            Status = WorkspaceStatus.Active,
            CreatedByUserId = inviter.Id
        };
        var invite = new Invite
        {
            TenantId = tenant.Id,
            WorkspaceId = workspace.Id,
            Email = InviteEmail,
            NormalizedEmail = InviteEmail.ToUpperInvariant(),
            TokenHash = tokenHasher.HashToken(Token),
            Role = role,
            ExpiresAt = Now.AddDays(1),
            InvitedByUserId = inviter.Id
        };

        context.Tenants.Add(tenant);
        if (crossTenantWorkspace)
        {
            context.Tenants.Add(workspaceTenant);
        }
        context.Users.Add(inviter);
        context.Workspaces.Add(workspace);
        context.Invites.Add(invite);

        User? existingUser = null;
        if (seedExistingEligibleUser)
        {
            existingUser = new User
            {
                DisplayName = "Existing Invitee",
                Email = InviteEmail,
                NormalizedEmail = InviteEmail.ToUpperInvariant(),
                PasswordHash = hasher.HashPassword("ExistingPassword123"),
                SystemRole = SystemRole.User,
                Status = UserStatus.Active
            };
            context.Users.Add(existingUser);
            context.TenantUsers.Add(new TenantUser
            {
                TenantId = tenant.Id,
                UserId = existingUser.Id,
                Role = TenantUserRole.Member,
                Status = existingTenantStatus,
                JoinedAt = Now.AddDays(-2),
                InvitedByUserId = inviter.Id
            });
            context.WorkspaceMembers.Add(new WorkspaceMember
            {
                TenantId = tenant.Id,
                WorkspaceId = workspace.Id,
                UserId = existingUser.Id,
                Role = WorkspaceRole.Member,
                Status = existingWorkspaceStatus,
                JoinedAt = Now.AddDays(-2)
            });
        }

        await context.SaveChangesAsync();
        return new InviteGraph(tenant, workspace, inviter, invite, existingUser);
    }

    private sealed record InviteGraph(
        Tenant Tenant,
        Workspace Workspace,
        User Inviter,
        Invite Invite,
        User? ExistingUser);

    private sealed class TenantScope(AppDbContext context, CurrentTenantService currentTenant) : IAsyncDisposable
    {
        public AppDbContext Context { get; } = context;
        public CurrentTenantService CurrentTenant { get; } = currentTenant;

        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class AnonymousCurrentUser : ICurrentUser
    {
        public Guid? UserId => null;
        public Guid? SessionId => null;
        public string? Email => null;
        public SystemRole? SystemRole => null;
        public bool IsAuthenticated => false;
    }

    private sealed class NoopUserSessionService : IUserSessionService
    {
        public Task<SessionValidationResult> ValidateSessionAsync(
            Guid userId,
            Guid sessionId,
            Guid? tenantId,
            bool requireActiveTenantMembership,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(SessionValidationResult.Success());

        public Task<Result> RevokeSessionAsync(
            Guid sessionId,
            Guid? actorUserId,
            string reason,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

        public Task<Result<int>> RevokeUserSessionsAsync(
            Guid userId,
            Guid? actorUserId,
            string reason,
            Guid? exceptSessionId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<int>.Success(0));
    }
}
