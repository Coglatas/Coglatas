using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Realtime;
using Coglatas.Application.Tenancy;
using Coglatas.Application.Workspaces;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Coglatas.Tests.PostgreSql;

[Trait("Scope", "WPC02B")]
public sealed class Wpc02BCapabilityGrantWorkspaceGeneralPostgreSqlTests
{
    private const string Wpc02ABaseMigration = "20260816041835_Wpc02AProjectVisibilityAndActivationProvenance";

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task MigrationAppliesRollsBackAndReappliesWithCleanModel()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async testConnectionString =>
        {
            await using (var context = PostgreSqlMigrationTestDatabase.CreatePlatformContext(testConnectionString))
            {
                await context.GetService<IMigrator>().MigrateAsync();
                Assert.Empty(await context.Database.GetPendingMigrationsAsync());
                Assert.False(context.Database.HasPendingModelChanges());
            }

            Assert.True(await TableExistsAsync(testConnectionString, "capability_grants"));
            Assert.True(await ColumnExistsAsync(testConnectionString, "conversations", "DefaultKind"));
            Assert.True(await ColumnExistsAsync(testConnectionString, "conversations", "Visibility"));

            await PostgreSqlMigrationTestDatabase.MigrateAsync(testConnectionString, Wpc02ABaseMigration);
            Assert.False(await TableExistsAsync(testConnectionString, "capability_grants"));
            Assert.False(await ColumnExistsAsync(testConnectionString, "conversations", "DefaultKind"));
            Assert.False(await ColumnExistsAsync(testConnectionString, "conversations", "Visibility"));

            await PostgreSqlMigrationTestDatabase.MigrateAsync(testConnectionString);
            await using var reapplied = PostgreSqlMigrationTestDatabase.CreatePlatformContext(testConnectionString);
            Assert.Empty(await reapplied.Database.GetPendingMigrationsAsync());
            Assert.False(reapplied.Database.HasPendingModelChanges());
            Assert.True(await TableExistsAsync(testConnectionString, "capability_grants"));
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task PersistenceEnforcesCapabilitySlotAndCanonicalDefaultIdentity()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async testConnectionString =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(testConnectionString);
            var currentTenant = new CurrentTenantService();
            var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(testConnectionString).Options;
            await using var dbContext = new AppDbContext(options, currentTenant);
            var graph = await SeedTenantWorkspaceAsync(dbContext, currentTenant, "identity");

            var ordinaryNamedGeneral = NewConversation(
                graph.Tenant.Id,
                graph.Workspace.Id,
                graph.User.Id,
                "general",
                defaultKind: null);
            var canonical = NewConversation(
                graph.Tenant.Id,
                graph.Workspace.Id,
                graph.User.Id,
                "general",
                ConversationDefaultKind.WorkspaceGeneral);
            dbContext.Conversations.AddRange(ordinaryNamedGeneral, canonical);
            await dbContext.SaveChangesAsync();

            var duplicateCanonical = NewConversation(
                graph.Tenant.Id,
                graph.Workspace.Id,
                graph.User.Id,
                "renamed-general",
                ConversationDefaultKind.WorkspaceGeneral);
            dbContext.Conversations.Add(duplicateCanonical);
            await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());
            dbContext.Entry(duplicateCanonical).State = EntityState.Detached;

            var now = DateTimeOffset.UtcNow;
            var firstGrant = NewTenantGrant(graph.Tenant.Id, graph.User.Id, now);
            dbContext.Set<CapabilityGrant>().Add(firstGrant);
            await dbContext.SaveChangesAsync();

            var duplicateGrant = NewTenantGrant(graph.Tenant.Id, graph.User.Id, now.AddSeconds(1));
            dbContext.Set<CapabilityGrant>().Add(duplicateGrant);
            await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());
            dbContext.Entry(duplicateGrant).State = EntityState.Detached;

            var invalidTenantScope = NewTenantGrant(graph.Tenant.Id, graph.User.Id, now.AddSeconds(2));
            invalidTenantScope.ScopeId = Guid.NewGuid();
            invalidTenantScope.CapabilityKey = "workspace.other";
            dbContext.Set<CapabilityGrant>().Add(invalidTenantScope);
            await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task EvaluatorRevalidatesGrantMembershipTenantAndWorkspaceScope()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async testConnectionString =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(testConnectionString);
            var currentTenant = new CurrentTenantService();
            var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(testConnectionString).Options;
            var now = new DateTimeOffset(2026, 8, 17, 3, 0, 0, TimeSpan.Zero);
            var clock = new FixedClock(now);

            await using var dbContext = new AppDbContext(options, currentTenant);
            var graph = await SeedTenantWorkspaceAsync(dbContext, currentTenant, "evaluator");
            var evaluator = new CapabilityGrantEvaluator(
                new CapabilityGrantRepository(dbContext),
                new TenantRepository(dbContext),
                new WorkspaceRepository(dbContext),
                currentTenant,
                clock);

            var workspaceCreate = NewTenantGrant(graph.Tenant.Id, graph.User.Id, now.AddMinutes(-1));
            dbContext.Set<CapabilityGrant>().Add(workspaceCreate);
            await dbContext.SaveChangesAsync();
            Assert.True(await HasWorkspaceCreateAsync(evaluator, graph));

            workspaceCreate.RevokedAt = now;
            workspaceCreate.VersionNo++;
            await dbContext.SaveChangesAsync();
            Assert.False(await HasWorkspaceCreateAsync(evaluator, graph));

            workspaceCreate.RevokedAt = null;
            workspaceCreate.ExpiresAt = now;
            workspaceCreate.VersionNo++;
            await dbContext.SaveChangesAsync();
            Assert.False(await HasWorkspaceCreateAsync(evaluator, graph));

            workspaceCreate.ExpiresAt = now.AddHours(1);
            workspaceCreate.VersionNo++;
            graph.TenantUser.Status = TenantUserStatus.Suspended;
            await dbContext.SaveChangesAsync();
            Assert.False(await HasWorkspaceCreateAsync(evaluator, graph));

            graph.TenantUser.Status = TenantUserStatus.Active;
            await dbContext.SaveChangesAsync();
            var projectCreate = new CapabilityGrant
            {
                TenantId = graph.Tenant.Id,
                SubjectUserId = graph.User.Id,
                CapabilityKey = CapabilityKeys.ProjectCreate,
                ScopeType = CapabilityScopeType.Workspace,
                ScopeId = graph.Workspace.Id,
                GrantedByUserId = graph.User.Id,
                GrantedAt = now.AddMinutes(-1),
                ExpiresAt = now.AddHours(1),
                VersionNo = 1
            };
            dbContext.Set<CapabilityGrant>().Add(projectCreate);
            await dbContext.SaveChangesAsync();
            Assert.True(await evaluator.HasActiveGrantAsync(
                graph.User.Id,
                graph.Tenant.Id,
                CapabilityKeys.ProjectCreate,
                CapabilityScopeType.Workspace,
                graph.Workspace.Id));

            graph.Workspace.Status = WorkspaceStatus.Deleted;
            graph.Workspace.MarkDeleted(now);
            await dbContext.SaveChangesAsync();
            Assert.False(await evaluator.HasActiveGrantAsync(
                graph.User.Id,
                graph.Tenant.Id,
                CapabilityKeys.ProjectCreate,
                CapabilityScopeType.Workspace,
                graph.Workspace.Id));
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task WorkspaceGeneralProvisioningAndMembershipSyncUseLeastPrivilege()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async testConnectionString =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(testConnectionString);
            var currentTenant = new CurrentTenantService();
            var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(testConnectionString).Options;
            var now = new DateTimeOffset(2026, 8, 17, 4, 0, 0, TimeSpan.Zero);
            var clock = new FixedClock(now);
            var changes = new RecordingAuthorizationStateChangePublisher();

            await using var dbContext = new AppDbContext(options, currentTenant);
            var graph = await SeedTenantWorkspaceAsync(dbContext, currentTenant, "general");
            var store = new DefaultConversationStore(dbContext);
            var required = new WorkspaceGeneralRequiredInitialization(store, currentTenant, clock, changes);
            var staged = await required.StageAsync(graph.Workspace, graph.User.Id);
            Assert.True(staged.IsSuccess, staged.Error);
            await dbContext.SaveChangesAsync();

            var general = await dbContext.Conversations.SingleAsync(conversation =>
                conversation.WorkspaceId == graph.Workspace.Id &&
                conversation.DefaultKind == ConversationDefaultKind.WorkspaceGeneral);
            Assert.Equal(ConversationType.WorkspaceChannel, general.Type);
            Assert.Equal("general", general.Title);
            Assert.Equal(ConversationVisibility.PublicWithinScope, general.Visibility);
            Assert.Null(general.ProjectId);

            var creator = await dbContext.ConversationMembers.SingleAsync(member =>
                member.ConversationId == general.Id && member.UserId == graph.User.Id);
            Assert.Equal(ConversationMemberRole.Admin, creator.Role);
            Assert.True(creator.CanRead);
            Assert.True(creator.CanPost);
            Assert.True(creator.CanManageMembers);
            Assert.True(creator.CanCreateThread);

            var secondUser = NewUser("readonly");
            currentTenant.SetPlatformScope();
            dbContext.Users.Add(secondUser);
            await dbContext.SaveChangesAsync();
            currentTenant.SetTenant(graph.Tenant.Id, graph.Tenant.Slug);

            var tenantMembership = new TenantUser
            {
                TenantId = graph.Tenant.Id,
                UserId = secondUser.Id,
                Role = TenantUserRole.Member,
                Status = TenantUserStatus.Active,
                JoinedAt = now
            };
            var workspaceMembership = new WorkspaceMember
            {
                TenantId = graph.Tenant.Id,
                WorkspaceId = graph.Workspace.Id,
                UserId = secondUser.Id,
                Role = WorkspaceRole.ReadOnly,
                Status = MembershipStatus.Active,
                JoinedAt = now
            };
            dbContext.TenantUsers.Add(tenantMembership);
            dbContext.WorkspaceMembers.Add(workspaceMembership);
            await dbContext.SaveChangesAsync();

            var synchronizer = new WorkspaceGeneralMembershipSynchronizer(store, currentTenant, clock, changes);
            Assert.True((await synchronizer.StageAsync(workspaceMembership, graph.User.Id)).IsSuccess);
            await dbContext.SaveChangesAsync();

            var participant = await dbContext.ConversationMembers.SingleAsync(member =>
                member.ConversationId == general.Id && member.UserId == secondUser.Id);
            Assert.Equal(ConversationMemberRole.ReadOnly, participant.Role);
            Assert.True(participant.CanRead);
            Assert.False(participant.CanPost);
            Assert.False(participant.CanManageMembers);
            Assert.False(participant.CanCreateThread);

            workspaceMembership.Role = WorkspaceRole.Owner;
            Assert.True((await synchronizer.StageAsync(workspaceMembership, graph.User.Id)).IsSuccess);
            await dbContext.SaveChangesAsync();
            Assert.Equal(ConversationMemberRole.Member, participant.Role);
            Assert.True(participant.CanPost);
            Assert.False(participant.CanManageMembers);

            participant.Role = ConversationMemberRole.Admin;
            participant.CanManageMembers = true;
            workspaceMembership.Role = WorkspaceRole.Member;
            Assert.True((await synchronizer.StageAsync(workspaceMembership, graph.User.Id)).IsSuccess);
            await dbContext.SaveChangesAsync();
            Assert.Equal(ConversationMemberRole.Admin, participant.Role);
            Assert.True(participant.CanManageMembers);

            workspaceMembership.Status = MembershipStatus.Suspended;
            Assert.True((await synchronizer.StageAsync(workspaceMembership, graph.User.Id)).IsSuccess);
            await dbContext.SaveChangesAsync();
            Assert.False(participant.CanRead);
            Assert.False(participant.CanPost);
            Assert.False(participant.CanManageMembers);
            Assert.False(participant.CanCreateThread);
            Assert.NotNull(participant.RemovedAt);
            Assert.Contains(changes.Events, item =>
                item.AffectedUserId == secondUser.Id && item.ScopeId == general.Id && item.Change == "revoked");
        });
    }

    private static Task<bool> HasWorkspaceCreateAsync(CapabilityGrantEvaluator evaluator, SeedGraph graph) =>
        evaluator.HasActiveGrantAsync(
            graph.User.Id,
            graph.Tenant.Id,
            CapabilityKeys.WorkspaceCreate,
            CapabilityScopeType.Tenant,
            graph.Tenant.Id);

    private static async Task<SeedGraph> SeedTenantWorkspaceAsync(
        AppDbContext dbContext,
        CurrentTenantService currentTenant,
        string suffix)
    {
        var runId = Guid.NewGuid().ToString("N");
        var tenant = new Tenant
        {
            Name = $"WPC02B Tenant {suffix} {runId}",
            DisplayName = $"WPC02B Tenant {suffix}",
            Slug = $"wpc02b-{suffix}-{runId}"
        };
        var user = NewUser($"owner-{suffix}-{runId}");

        currentTenant.SetPlatformScope();
        dbContext.Tenants.Add(tenant);
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        currentTenant.SetTenant(tenant.Id, tenant.Slug);
        var tenantUser = new TenantUser
        {
            TenantId = tenant.Id,
            UserId = user.Id,
            Role = TenantUserRole.Owner,
            Status = TenantUserStatus.Active,
            JoinedAt = DateTimeOffset.UtcNow
        };
        var workspace = new Workspace
        {
            TenantId = tenant.Id,
            Name = $"WPC02B Workspace {suffix}",
            Slug = $"wpc02b-workspace-{suffix}-{runId}",
            CreatedByUserId = user.Id,
            Status = WorkspaceStatus.Active
        };
        var workspaceMember = new WorkspaceMember
        {
            TenantId = tenant.Id,
            WorkspaceId = workspace.Id,
            UserId = user.Id,
            Role = WorkspaceRole.Owner,
            Status = MembershipStatus.Active,
            JoinedAt = DateTimeOffset.UtcNow
        };
        dbContext.TenantUsers.Add(tenantUser);
        dbContext.Workspaces.Add(workspace);
        dbContext.WorkspaceMembers.Add(workspaceMember);
        await dbContext.SaveChangesAsync();

        return new SeedGraph(tenant, user, tenantUser, workspace);
    }

    private static User NewUser(string suffix) => new()
    {
        DisplayName = $"WPC02B User {suffix}",
        Email = $"{suffix}@example.test".ToLowerInvariant(),
        NormalizedEmail = $"{suffix}@example.test".ToUpperInvariant(),
        Status = UserStatus.Active
    };

    private static Conversation NewConversation(
        Guid tenantId,
        Guid workspaceId,
        Guid userId,
        string title,
        ConversationDefaultKind? defaultKind) => new()
    {
        TenantId = tenantId,
        WorkspaceId = workspaceId,
        ProjectId = null,
        Type = ConversationType.WorkspaceChannel,
        Title = title,
        Visibility = ConversationVisibility.PublicWithinScope,
        DefaultKind = defaultKind,
        CreatedByUserId = userId
    };

    private static CapabilityGrant NewTenantGrant(Guid tenantId, Guid userId, DateTimeOffset grantedAt) => new()
    {
        TenantId = tenantId,
        SubjectUserId = userId,
        CapabilityKey = CapabilityKeys.WorkspaceCreate,
        ScopeType = CapabilityScopeType.Tenant,
        ScopeId = tenantId,
        GrantedByUserId = userId,
        GrantedAt = grantedAt,
        VersionNo = 1
    };

    private static Task<bool> TableExistsAsync(string connectionString, string tableName) =>
        PostgreSqlMigrationTestDatabase.ScalarAsync<bool>(
            connectionString,
            "SELECT to_regclass(current_schema() || '.' || @tableName) IS NOT NULL;",
            ("tableName", tableName));

    private static Task<bool> ColumnExistsAsync(string connectionString, string tableName, string columnName) =>
        PostgreSqlMigrationTestDatabase.ScalarAsync<bool>(
            connectionString,
            """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.columns
                WHERE table_schema = current_schema()
                  AND table_name = @tableName
                  AND column_name = @columnName);
            """,
            ("tableName", tableName),
            ("columnName", columnName));

    private sealed record SeedGraph(
        Tenant Tenant,
        User User,
        TenantUser TenantUser,
        Workspace Workspace);

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class RecordingAuthorizationStateChangePublisher : IAuthorizationStateChangePublisher
    {
        public List<RecordedAuthorizationChange> Events { get; } = [];

        public Task PublishAsync(
            Guid tenantId,
            Guid affectedUserId,
            string scopeType,
            Guid? scopeId,
            string change,
            CancellationToken cancellationToken = default)
        {
            Events.Add(new RecordedAuthorizationChange(tenantId, affectedUserId, scopeType, scopeId, change));
            return Task.CompletedTask;
        }
    }

    private sealed record RecordedAuthorizationChange(
        Guid TenantId,
        Guid AffectedUserId,
        string ScopeType,
        Guid? ScopeId,
        string Change);
}
