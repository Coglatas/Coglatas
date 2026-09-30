using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Realtime;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Tests.PostgreSql;

[Trait("Category", "PostgreSQLIntegration")]
[Trait("Scope", "TaskV1PR07C")]
public sealed class TaskV1Pr07CNotificationVersionConcurrencyPostgreSqlTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 3, 5, 0, 0, TimeSpan.Zero);

    [PostgreSqlFact]
    public async Task DigestAndImmediateTaskSignalsCannotCommitTheSameRecipientStateVersion()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();

        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
            var suffix = Guid.NewGuid().ToString("N");
            var tenant = new Tenant
            {
                Name = $"PR07-C notification version {suffix}",
                DisplayName = "PR07-C notification version",
                Slug = $"pr07c-notification-version-{suffix}",
                Status = TenantStatus.Active
            };
            var recipient = new User
            {
                DisplayName = "PR07-C version recipient",
                Email = $"pr07c-version-{suffix}@example.test",
                NormalizedEmail = $"PR07C-VERSION-{suffix}@EXAMPLE.TEST",
                PasswordHash = "hash",
                Status = UserStatus.Active
            };

            await using (var platform = PostgreSqlMigrationTestDatabase.CreatePlatformContext(database))
            {
                platform.AddRange(tenant, recipient);
                await platform.SaveChangesAsync();
            }

            await using (var seed = CreateTenantContext(database, tenant))
            {
                seed.NotificationUserStates.Add(new NotificationUserState
                {
                    TenantId = tenant.Id,
                    UserId = recipient.Id,
                    Version = 0,
                    UpdatedAt = Now.AddMinutes(-1)
                });
                await seed.SaveChangesAsync();
            }

            const string digestKey = "task-deadline-digest:workspace:11111111111111111111111111111111:date:2026-08-03:policy:1";
            const string immediateKey = "task:22222222222222222222222222222222:event:TaskDeadlineChanged:version:1";
            await using var digestContext = CreateTenantContext(database, tenant);
            await using var immediateContext = CreateTenantContext(database, tenant);
            Assert.True(digestContext.Model
                .FindEntityType(typeof(NotificationUserState))!
                .FindProperty(nameof(NotificationUserState.Version))!
                .IsConcurrencyToken);
            var digestService = CreateNotificationService(digestContext, tenant);
            var immediateService = CreateNotificationService(immediateContext, tenant);

            await Task.WhenAll(
                digestService.StageTaskDeadlineDigestByLogicalKeyAsync(
                    recipient.Id,
                    Guid.Parse("33333333-3333-3333-3333-333333333333"),
                    digestKey),
                immediateService.StageTaskByLogicalKeyAsync(
                    recipient.Id,
                    NotificationType.TaskDueSoon,
                    "Task deadline changed",
                    Guid.Parse("22222222-2222-2222-2222-222222222222"),
                    immediateKey));

            Assert.All(new[] { digestContext, immediateContext }, context =>
            {
                var entry = context.Entry(context.NotificationUserStates.Local.Single());
                Assert.Equal(0L, entry.Property(state => state.Version).OriginalValue);
                Assert.Equal(1L, entry.Property(state => state.Version).CurrentValue);
            });

            var saveResults = await Task.WhenAll(
                CaptureSaveAsync(digestContext),
                CaptureSaveAsync(immediateContext));
            Assert.Single(saveResults, result => result is null);
            Assert.Single(saveResults, result => result is DbUpdateConcurrencyException);

            // Retry both logical intents in a clean unit of work. The winner is
            // reused and the rolled-back loser receives the next version.
            await using (var retry = CreateTenantContext(database, tenant))
            {
                var retryService = CreateNotificationService(retry, tenant);
                await retryService.StageTaskDeadlineDigestByLogicalKeyAsync(
                    recipient.Id,
                    Guid.Parse("33333333-3333-3333-3333-333333333333"),
                    digestKey);
                await retryService.StageTaskByLogicalKeyAsync(
                    recipient.Id,
                    NotificationType.TaskDueSoon,
                    "Task deadline changed",
                    Guid.Parse("22222222-2222-2222-2222-222222222222"),
                    immediateKey);
                await retry.SaveChangesAsync();
            }

            await using var verification = CreateTenantContext(database, tenant);
            var notifications = await verification.Notifications
                .AsNoTracking()
                .OrderBy(notification => notification.StateVersion)
                .ToListAsync();
            Assert.Equal(2, notifications.Count);
            Assert.Equal([1L, 2L], notifications.Select(notification => notification.StateVersion));
            Assert.Equal(2, (await verification.NotificationUserStates.AsNoTracking().SingleAsync()).Version);

            var signals = await verification.OutboxEvents
                .AsNoTracking()
                .Where(item => item.EventType == "Notifications.NotificationCreated.v1")
                .OrderBy(item => item.AggregateVersion)
                .ToListAsync();
            Assert.Equal(2, signals.Count);
            Assert.Equal([1L, 2L], signals.Select(signal => signal.AggregateVersion));
        });
    }

    private static async Task<Exception?> CaptureSaveAsync(AppDbContext dbContext)
    {
        try
        {
            await dbContext.SaveChangesAsync();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static DbNotificationService CreateNotificationService(AppDbContext dbContext, Tenant tenant)
    {
        var currentTenant = TenantScope(tenant);
        var clock = new FixedClock();
        var outbox = new TransactionalOutbox(new OutboxEventRepository(dbContext), currentTenant, clock);
        return new DbNotificationService(dbContext, clock, currentTenant, outbox);
    }

    private static AppDbContext CreateTenantContext(string connectionString, Tenant tenant) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options,
            TenantScope(tenant));

    private static CurrentTenantService TenantScope(Tenant tenant)
    {
        var currentTenant = new CurrentTenantService();
        currentTenant.SetTenant(tenant.Id, tenant.Slug);
        return currentTenant;
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }
}
