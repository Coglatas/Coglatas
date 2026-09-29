using System.Data.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Notifications;
using Coglatas.Application.Projects;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Coglatas.Tests.PostgreSql;

/// <summary>
/// PostgreSQL contracts specific to PR07-A.  They deliberately exercise the
/// filtered logical-key index and conditional preference updates against the
/// real provider rather than EF Core InMemory semantics.
/// </summary>
[Collection("PostgreSqlTaskV1")]
public sealed class TaskV1Pr07NotificationFoundationPostgreSqlTests
{
    private const string PreviousMigration = "20260730120626_AddCanonicalGanttVersions";
    private const string LogicalKeyIndex = "IX_notifications_TenantId_UserId_LogicalKey";

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR07A")]
    public async Task FreshDatabaseAppliesNotificationFoundationAndHasNoPendingModelChanges()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            var graph = await TaskV1MigrationRawSqlSeed.CreateGraphAsync(database, "pr07a-fresh");
            var memberId = Guid.NewGuid();
            await PostgreSqlMigrationTestDatabase.ExecuteAsync(database, """
                INSERT INTO workspace_members (
                    "Id", "TenantId", "WorkspaceId", "UserId", "Role", "Status", "JoinedAt", "CreatedAt")
                VALUES (@id, @tenantId, @workspaceId, @userId, 'Member', 'Active', @now, @now);
                """,
                ("id", memberId),
                ("tenantId", graph.TenantId),
                ("workspaceId", graph.WorkspaceId),
                ("userId", graph.UserId),
                ("now", new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero)));

            await AssertFoundationSchemaAsync(database, expected: true);
            Assert.Equal(
                new TimeOnly(8, 0),
                await PostgreSqlMigrationTestDatabase.ScalarAsync<TimeOnly>(
                    database,
                    "SELECT \"DefaultTaskDeadlineDigestLocalTime\" FROM workspaces WHERE \"Id\" = @id;",
                    ("id", graph.WorkspaceId)));
            Assert.Equal(
                1L,
                await PostgreSqlMigrationTestDatabase.ScalarAsync<long>(
                    database,
                    "SELECT \"TaskNotificationSettingsVersion\" FROM workspaces WHERE \"Id\" = @id;",
                    ("id", graph.WorkspaceId)));
            Assert.Equal(
                1L,
                await PostgreSqlMigrationTestDatabase.ScalarAsync<long>(
                    database,
                    "SELECT \"TaskNotificationPreferenceVersion\" FROM workspace_members WHERE \"Id\" = @id;",
                    ("id", memberId)));
            Assert.True(
                await PostgreSqlMigrationTestDatabase.ScalarAsync<bool>(
                    database,
                    "SELECT \"TaskDeadlineDigestLocalTime\" IS NULL FROM workspace_members WHERE \"Id\" = @id;",
                    ("id", memberId)));

            await using var context = PostgreSqlMigrationTestDatabase.CreatePlatformContext(database);
            Assert.Empty(await context.Database.GetPendingMigrationsAsync());
            Assert.False(context.Database.HasPendingModelChanges());
            Assert.Contains(
                context.Model.FindEntityType(typeof(Notification))!.GetIndexes(),
                index => index.GetDatabaseName() == LogicalKeyIndex && index.IsUnique);
            Assert.False(context.Model.FindEntityType(typeof(WorkspaceMember))!
                .FindProperty(nameof(WorkspaceMember.TaskNotificationPreferenceVersion))!
                .IsConcurrencyToken);
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR07A")]
    public async Task UpgradeBackfillsDefaultsRetainsLegacyRowsAndRollbackOnlyDropsAdditiveColumns()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database, PreviousMigration);
            var graph = await TaskV1MigrationRawSqlSeed.CreateGraphAsync(database, "pr07a-upgrade");
            var memberId = Guid.NewGuid();
            var notificationId = Guid.NewGuid();
            var now = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
            await PostgreSqlMigrationTestDatabase.ExecuteAsync(database, """
                INSERT INTO workspace_members (
                    "Id", "TenantId", "WorkspaceId", "UserId", "Role", "Status", "JoinedAt", "CreatedAt")
                VALUES (@memberId, @tenantId, @workspaceId, @userId, 'Member', 'Active', @now, @now);
                INSERT INTO notifications (
                    "Id", "TenantId", "UserId", "NotificationType", "Title", "IsRead", "CreatedAt", "StateVersion")
                VALUES (@notificationId, @tenantId, @userId, 'TaskDueSoon', 'Legacy notification', false, @now, 1);
                """,
                ("memberId", memberId),
                ("notificationId", notificationId),
                ("tenantId", graph.TenantId),
                ("workspaceId", graph.WorkspaceId),
                ("userId", graph.UserId),
                ("now", now));

            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);

            await AssertFoundationSchemaAsync(database, expected: true);
            Assert.Equal(
                new TimeOnly(8, 0),
                await PostgreSqlMigrationTestDatabase.ScalarAsync<TimeOnly>(
                    database,
                    "SELECT \"DefaultTaskDeadlineDigestLocalTime\" FROM workspaces WHERE \"Id\" = @id;",
                    ("id", graph.WorkspaceId)));
            Assert.Equal(
                1L,
                await PostgreSqlMigrationTestDatabase.ScalarAsync<long>(
                    database,
                    "SELECT \"TaskNotificationSettingsVersion\" FROM workspaces WHERE \"Id\" = @id;",
                    ("id", graph.WorkspaceId)));
            Assert.True(
                await PostgreSqlMigrationTestDatabase.ScalarAsync<bool>(
                    database,
                    "SELECT \"TaskDeadlineDigestLocalTime\" IS NULL AND \"TaskNotificationPreferenceVersion\" = 1 FROM workspace_members WHERE \"Id\" = @id;",
                    ("id", memberId)));
            Assert.True(
                await PostgreSqlMigrationTestDatabase.ScalarAsync<bool>(
                    database,
                    "SELECT \"LogicalKey\" IS NULL FROM notifications WHERE \"Id\" = @id;",
                    ("id", notificationId)));

            await PostgreSqlMigrationTestDatabase.MigrateAsync(database, PreviousMigration);
            await AssertFoundationSchemaAsync(database, expected: false);
            Assert.Equal(
                1L,
                await PostgreSqlMigrationTestDatabase.ScalarAsync<long>(
                    database,
                    "SELECT COUNT(*) FROM notifications WHERE \"Id\" = @id;",
                    ("id", notificationId)));
            Assert.Equal(
                1L,
                await PostgreSqlMigrationTestDatabase.ScalarAsync<long>(
                    database,
                    "SELECT COUNT(*) FROM workspace_members WHERE \"Id\" = @id;",
                    ("id", memberId)));
            Assert.Equal(
                1L,
                await PostgreSqlMigrationTestDatabase.ScalarAsync<long>(
                    database,
                    "SELECT COUNT(*) FROM workspaces WHERE \"Id\" = @id;",
                    ("id", graph.WorkspaceId)));
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR07A")]
    public async Task ConcurrentLogicalKeyWritersReturnOneRowAndOneIdentity()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            var graph = await SeedFoundationGraphAsync(database);
            var barrier = new InitialLogicalLookupBarrier(expectedArrivals: 2);
            const string logicalKey = "task:deadline:42:event:deadline:v1";

            async Task<Guid> CreateAsync()
            {
                var tenant = new CurrentTenantService();
                tenant.SetTenant(graph.TenantA.Id, graph.TenantA.Slug);
                await using var context = CreateTenantContext(database, graph.TenantA, barrier);
                var service = new DbNotificationService(context, FixedClock.Instance, tenant);
                return await service.CreateOrGetByLogicalKeyAsync(
                    graph.UserA.Id,
                    NotificationType.TaskDueSoon,
                    "Deadline reminder",
                    null,
                    "TaskItem",
                    Guid.NewGuid(),
                    logicalKey);
            }

            var ids = await Task.WhenAll(CreateAsync(), CreateAsync());

            Assert.Equal(2, barrier.Arrivals);
            Assert.Single(ids.Distinct());
            await using var verification = CreateTenantContext(database, graph.TenantA);
            Assert.Equal(
                1,
                await verification.Notifications.CountAsync(notification =>
                    notification.UserId == graph.UserA.Id &&
                    notification.LogicalKey == logicalKey));
            Assert.Equal(
                1,
                await verification.NotificationUserStates.CountAsync(state => state.UserId == graph.UserA.Id));
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR07A")]
    public async Task LogicalKeyScopeSeparatesTenantsRecipientsEventsAndKeepsSoftDeletedAndLegacyRowsDistinct()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            var graph = await SeedFoundationGraphAsync(database);
            const string deadlineV1 = "task:42:deadline:event:v1";
            const string deadlineV2 = "task:42:deadline:event:v2";
            const string assignmentV1 = "task:42:assignment:event:v1";

            var tenantA = new CurrentTenantService();
            tenantA.SetTenant(graph.TenantA.Id, graph.TenantA.Slug);
            await using var tenantAContext = CreateTenantContext(database, graph.TenantA);
            var tenantAService = new DbNotificationService(tenantAContext, FixedClock.Instance, tenantA);
            var primary = await tenantAService.CreateOrGetByLogicalKeyAsync(
                graph.UserA.Id, NotificationType.TaskDueSoon, "Deadline", null, "TaskItem", graph.WorkspaceA.Id, deadlineV1);
            var otherRecipient = await tenantAService.CreateOrGetByLogicalKeyAsync(
                graph.UserB.Id, NotificationType.TaskDueSoon, "Deadline", null, "TaskItem", graph.WorkspaceA.Id, deadlineV1);
            var otherVersion = await tenantAService.CreateOrGetByLogicalKeyAsync(
                graph.UserA.Id, NotificationType.TaskDueSoon, "Deadline v2", null, "TaskItem", graph.WorkspaceA.Id, deadlineV2);
            var otherCategory = await tenantAService.CreateOrGetByLogicalKeyAsync(
                graph.UserA.Id, NotificationType.TaskAssigned, "Assignment", null, "TaskItem", graph.WorkspaceA.Id, assignmentV1);
            await tenantAContext.SaveChangesAsync();

            var tenantB = new CurrentTenantService();
            tenantB.SetTenant(graph.TenantB.Id, graph.TenantB.Slug);
            await using var tenantBContext = CreateTenantContext(database, graph.TenantB);
            var tenantBService = new DbNotificationService(tenantBContext, FixedClock.Instance, tenantB);
            var otherTenant = await tenantBService.CreateOrGetByLogicalKeyAsync(
                graph.UserA.Id, NotificationType.TaskDueSoon, "Deadline", null, "TaskItem", graph.WorkspaceB.Id, deadlineV1);

            Assert.Equal(5, new[] { primary, otherRecipient, otherVersion, otherCategory, otherTenant }.Distinct().Count());

            var primaryRow = await tenantAContext.Notifications.SingleAsync(notification => notification.Id == primary);
            primaryRow.DeletedAt = FixedClock.Instance.UtcNow;
            await tenantAContext.SaveChangesAsync();
            var afterSoftDelete = await tenantAService.CreateOrGetByLogicalKeyAsync(
                graph.UserA.Id, NotificationType.TaskDueSoon, "Replacement must not be created", null, "TaskItem", graph.WorkspaceA.Id, deadlineV1);
            Assert.Equal(primary, afterSoftDelete);
            Assert.Equal(
                0,
                await tenantAContext.Notifications.CountAsync(notification =>
                    notification.UserId == graph.UserA.Id &&
                    notification.LogicalKey == deadlineV1 &&
                    notification.DeletedAt == null));

            tenantAContext.Notifications.AddRange(
                new Notification
                {
                    TenantId = graph.TenantA.Id,
                    UserId = graph.UserA.Id,
                    NotificationType = NotificationType.System,
                    Title = "Legacy one",
                    CreatedAt = FixedClock.Instance.UtcNow,
                    StateVersion = 10
                },
                new Notification
                {
                    TenantId = graph.TenantA.Id,
                    UserId = graph.UserA.Id,
                    NotificationType = NotificationType.System,
                    Title = "Legacy two",
                    CreatedAt = FixedClock.Instance.UtcNow,
                    StateVersion = 11
                });
            await tenantAContext.SaveChangesAsync();
            Assert.Equal(
                2,
                await tenantAContext.Notifications.CountAsync(notification =>
                    notification.UserId == graph.UserA.Id && notification.LogicalKey == null));
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR07A")]
    public async Task PreferenceConditionalUpdateHasOneWinnerAndAConflictCanRetry()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            var graph = await SeedFoundationGraphAsync(database);

            var firstTenant = new CurrentTenantService();
            firstTenant.SetTenant(graph.TenantA.Id, graph.TenantA.Slug);
            await using var firstContext = CreateTenantContext(database, graph.TenantA);
            var first = CreatePreferenceService(firstContext, firstTenant, graph.UserA.Id);

            var secondTenant = new CurrentTenantService();
            secondTenant.SetTenant(graph.TenantA.Id, graph.TenantA.Slug);
            await using var secondContext = CreateTenantContext(database, graph.TenantA);
            var second = CreatePreferenceService(secondContext, secondTenant, graph.UserA.Id);

            var outcomes = await Task.WhenAll(
                first.UpdateAsync(graph.WorkspaceA.Id, new UpdateTaskNotificationPreferenceRequest("00:00", 1)),
                second.UpdateAsync(graph.WorkspaceA.Id, new UpdateTaskNotificationPreferenceRequest("23:45", 1)));

            Assert.Single(outcomes, result => result.IsSuccess);
            var loser = Assert.Single(outcomes, result => !result.IsSuccess);
            Assert.Equal(TaskNotificationPreferenceService.VersionConflictCode, loser.ErrorDetail?.Code);
            Assert.Equal(2L, loser.CurrentVersion);

            var retryTenant = new CurrentTenantService();
            retryTenant.SetTenant(graph.TenantA.Id, graph.TenantA.Slug);
            await using var retryContext = CreateTenantContext(database, graph.TenantA);
            var retry = CreatePreferenceService(retryContext, retryTenant, graph.UserA.Id);
            var current = await retry.GetAsync(graph.WorkspaceA.Id);
            Assert.True(current.IsSuccess);
            Assert.Equal(2L, current.Value!.Version);
            var retried = await retry.UpdateAsync(
                graph.WorkspaceA.Id,
                new UpdateTaskNotificationPreferenceRequest("00:15", current.Value.Version));
            Assert.True(retried.IsSuccess);
            Assert.Equal(3L, retried.Value!.Version);
            Assert.Equal("00:15", retried.Value.DeadlineDigestLocalTime);
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR07A")]
    public async Task PreferenceVersionDoesNotConflictWithUnrelatedRoleOrStatusSaveChanges()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            var graph = await SeedFoundationGraphAsync(database);

            await using var unrelatedUpdateContext = CreateTenantContext(database, graph.TenantA);
            var unrelatedMember = await unrelatedUpdateContext.WorkspaceMembers.SingleAsync(member =>
                member.TenantId == graph.TenantA.Id &&
                member.WorkspaceId == graph.WorkspaceA.Id &&
                member.UserId == graph.UserA.Id);
            Assert.Equal(1L, unrelatedMember.TaskNotificationPreferenceVersion);
            Assert.Null(unrelatedMember.TaskDeadlineDigestLocalTime);

            var firstTenant = new CurrentTenantService();
            firstTenant.SetTenant(graph.TenantA.Id, graph.TenantA.Slug);
            await using (var preferenceContext = CreateTenantContext(database, graph.TenantA))
            {
                var preferences = new TaskNotificationPreferenceRepository(preferenceContext, firstTenant);
                Assert.True(await preferences.TryUpdateAsync(
                    graph.WorkspaceA.Id,
                    graph.UserA.Id,
                    expectedVersion: 1,
                    deadlineDigestLocalTime: new TimeOnly(0, 15),
                    updatedAt: FixedClock.Instance.UtcNow));
            }

            unrelatedMember.Role = WorkspaceRole.Admin;
            var roleSaveException = await Record.ExceptionAsync(
                () => unrelatedUpdateContext.SaveChangesAsync());
            Assert.Null(roleSaveException);

            await using (var afterRole = CreateTenantContext(database, graph.TenantA))
            {
                var persisted = await afterRole.WorkspaceMembers.SingleAsync(member => member.Id == unrelatedMember.Id);
                Assert.Equal(WorkspaceRole.Admin, persisted.Role);
                Assert.Equal(new TimeOnly(0, 15), persisted.TaskDeadlineDigestLocalTime);
                Assert.Equal(2L, persisted.TaskNotificationPreferenceVersion);
            }

            var secondTenant = new CurrentTenantService();
            secondTenant.SetTenant(graph.TenantA.Id, graph.TenantA.Slug);
            await using (var preferenceContext = CreateTenantContext(database, graph.TenantA))
            {
                var preferences = new TaskNotificationPreferenceRepository(preferenceContext, secondTenant);
                Assert.True(await preferences.TryUpdateAsync(
                    graph.WorkspaceA.Id,
                    graph.UserA.Id,
                    expectedVersion: 2,
                    deadlineDigestLocalTime: new TimeOnly(0, 30),
                    updatedAt: FixedClock.Instance.UtcNow));
            }

            unrelatedMember.Status = MembershipStatus.Suspended;
            var statusSaveException = await Record.ExceptionAsync(
                () => unrelatedUpdateContext.SaveChangesAsync());
            Assert.Null(statusSaveException);

            await using var verification = CreateTenantContext(database, graph.TenantA);
            var final = await verification.WorkspaceMembers.SingleAsync(member => member.Id == unrelatedMember.Id);
            Assert.Equal(WorkspaceRole.Admin, final.Role);
            Assert.Equal(MembershipStatus.Suspended, final.Status);
            Assert.Equal(new TimeOnly(0, 30), final.TaskDeadlineDigestLocalTime);
            Assert.Equal(3L, final.TaskNotificationPreferenceVersion);
        });
    }

    private static async Task AssertFoundationSchemaAsync(string connectionString, bool expected)
    {
        foreach (var (table, column) in new[]
                 {
                     ("notifications", "LogicalKey"),
                     ("workspaces", "DefaultTaskDeadlineDigestLocalTime"),
                     ("workspaces", "TaskNotificationSettingsVersion"),
                     ("workspace_members", "TaskDeadlineDigestLocalTime"),
                     ("workspace_members", "TaskNotificationPreferenceVersion")
                 })
        {
            Assert.Equal(
                expected,
                await PostgreSqlMigrationTestDatabase.ScalarAsync<bool>(
                    connectionString,
                    "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = @table AND column_name = @column);",
                    ("table", table),
                    ("column", column)));
        }

        Assert.Equal(
            expected,
            await PostgreSqlMigrationTestDatabase.ScalarAsync<bool>(
                connectionString,
                "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE tablename = 'notifications' AND indexname = @indexName AND indexdef LIKE '%WHERE (\"LogicalKey\" IS NOT NULL)%');",
                ("indexName", LogicalKeyIndex)));
    }

    private static async Task<FoundationGraph> SeedFoundationGraphAsync(string connectionString)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var tenantA = new Tenant { Name = $"PR07 A {suffix}", DisplayName = "PR07 A", Slug = $"pr07a-{suffix}" };
        var tenantB = new Tenant { Name = $"PR07 B {suffix}", DisplayName = "PR07 B", Slug = $"pr07b-{suffix}" };
        var userA = UserFor("a", suffix);
        var userB = UserFor("b", suffix);

        await using var platform = PostgreSqlMigrationTestDatabase.CreatePlatformContext(connectionString);
        platform.AddRange(tenantA, tenantB, userA, userB);
        await platform.SaveChangesAsync();

        var workspaceA = new Workspace
        {
            TenantId = tenantA.Id,
            Name = "PR07 A workspace",
            Slug = $"pr07a-workspace-{suffix}",
            TimeZone = "UTC",
            CreatedByUserId = userA.Id,
            Status = WorkspaceStatus.Active
        };
        var workspaceB = new Workspace
        {
            TenantId = tenantB.Id,
            Name = "PR07 B workspace",
            Slug = $"pr07b-workspace-{suffix}",
            TimeZone = "UTC",
            CreatedByUserId = userA.Id,
            Status = WorkspaceStatus.Active
        };
        platform.AddRange(
            workspaceA,
            workspaceB,
            new TenantUser { TenantId = tenantA.Id, UserId = userA.Id, Role = TenantUserRole.Member, Status = TenantUserStatus.Active, JoinedAt = FixedClock.Instance.UtcNow },
            new TenantUser { TenantId = tenantA.Id, UserId = userB.Id, Role = TenantUserRole.Member, Status = TenantUserStatus.Active, JoinedAt = FixedClock.Instance.UtcNow },
            new TenantUser { TenantId = tenantB.Id, UserId = userA.Id, Role = TenantUserRole.Member, Status = TenantUserStatus.Active, JoinedAt = FixedClock.Instance.UtcNow },
            new WorkspaceMember { TenantId = tenantA.Id, WorkspaceId = workspaceA.Id, UserId = userA.Id, Role = WorkspaceRole.Owner, Status = MembershipStatus.Active, JoinedAt = FixedClock.Instance.UtcNow },
            new WorkspaceMember { TenantId = tenantA.Id, WorkspaceId = workspaceA.Id, UserId = userB.Id, Role = WorkspaceRole.Member, Status = MembershipStatus.Active, JoinedAt = FixedClock.Instance.UtcNow },
            new WorkspaceMember { TenantId = tenantB.Id, WorkspaceId = workspaceB.Id, UserId = userA.Id, Role = WorkspaceRole.Owner, Status = MembershipStatus.Active, JoinedAt = FixedClock.Instance.UtcNow });
        await platform.SaveChangesAsync();

        return new FoundationGraph(tenantA, tenantB, userA, userB, workspaceA, workspaceB);
    }

    private static User UserFor(string suffix, string seed) => new()
    {
        DisplayName = $"PR07 user {suffix}",
        Email = $"pr07-{suffix}-{seed}@example.test",
        NormalizedEmail = $"PR07-{suffix}-{seed}@EXAMPLE.TEST",
        PasswordHash = "hash",
        Status = UserStatus.Active
    };

    private static AppDbContext CreateTenantContext(
        string connectionString,
        Tenant tenant,
        params IInterceptor[] interceptors)
    {
        var currentTenant = new CurrentTenantService();
        currentTenant.SetTenant(tenant.Id, tenant.Slug);
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString);
        if (interceptors.Length > 0)
        {
            options.AddInterceptors(interceptors);
        }

        return new AppDbContext(options.Options, currentTenant);
    }

    private static TaskNotificationPreferenceService CreatePreferenceService(
        AppDbContext context,
        CurrentTenantService tenant,
        Guid userId) =>
        new(
            new TestCurrentUser(userId),
            tenant,
            FixedClock.Instance,
            UtcTimeZoneResolver.Instance,
            new TaskNotificationPreferenceRepository(context, tenant));

    private sealed record FoundationGraph(
        Tenant TenantA,
        Tenant TenantB,
        User UserA,
        User UserB,
        Workspace WorkspaceA,
        Workspace WorkspaceB);

    private sealed class TestCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid? UserId => userId;
        public Guid? SessionId => null;
        public string Email => "pr07@example.test";
        public SystemRole? SystemRole => Coglatas.Domain.Enums.SystemRole.User;
        public bool IsAuthenticated => true;
    }

    private sealed class UtcTimeZoneResolver : ITaskWorkspaceTimeZoneResolver
    {
        public static readonly UtcTimeZoneResolver Instance = new();

        public Task<TimeZoneInfo> ResolveAsync(Guid tenantId, Guid workspaceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(TimeZoneInfo.Utc);
    }

    private sealed class FixedClock : IClock
    {
        public static readonly FixedClock Instance = new();
        public DateTimeOffset UtcNow => new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class InitialLogicalLookupBarrier(int expectedArrivals) : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;
        private int _released;

        public int Arrivals => Volatile.Read(ref _arrivals);

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (!IsLogicalLookup(command.CommandText) || Volatile.Read(ref _released) != 0)
            {
                return result;
            }

            if (Interlocked.Increment(ref _arrivals) == expectedArrivals)
            {
                Interlocked.Exchange(ref _released, 1);
                _release.TrySetResult();
            }

            await _release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return result;
        }

        private static bool IsLogicalLookup(string commandText) =>
            commandText.Contains("FROM notifications", StringComparison.OrdinalIgnoreCase) &&
            commandText.Contains("\"LogicalKey\"", StringComparison.Ordinal);
    }
}
