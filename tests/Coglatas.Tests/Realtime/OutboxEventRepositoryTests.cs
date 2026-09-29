using Coglatas.Application.Common.Tenancy;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Tests.Realtime;

public sealed class OutboxEventRepositoryTests
{
    [Fact]
    public async Task ClaimFailureAndDeadLetterTransitionsPreserveEventIdentity()
    {
        var tenant = new CurrentTenantService();
        tenant.SetPlatformScope();
        await using var dbContext = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
            tenant);
        var repository = new OutboxEventRepository(dbContext);
        var eventId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await repository.AddAsync(new OutboxEvent(eventId)
        {
            TenantId = Guid.NewGuid(),
            EventType = "Projects.TaskChanged.v1",
            PayloadSchemaVersion = 1,
            AggregateType = "Task",
            AggregateId = Guid.NewGuid(),
            OccurredAt = now,
            PayloadJson = "{}",
            RoutingJson = "[]",
            Status = OutboxEventStatus.Pending,
            NextAttemptAt = now
        });
        await dbContext.SaveChangesAsync();

        var claimed = await repository.ClaimDueAsync("test-worker", now, 10, TimeSpan.FromMinutes(1));

        var item = Assert.Single(claimed);
        Assert.Equal(eventId, item.Id);
        Assert.Equal(OutboxEventStatus.Processing, item.Status);
        Assert.True(item.LockToken.HasValue);

        Assert.True(await repository.MarkFailureAsync(item.Id, item.LockToken!.Value, now, false, null, "InvalidOutboxContract", "safe", 10));

        var deadLetter = await repository.GetByIdAsync(eventId);
        Assert.NotNull(deadLetter);
        Assert.Equal(eventId, deadLetter.Id);
        Assert.Equal(OutboxEventStatus.DeadLetter, deadLetter.Status);
        Assert.Equal(1, deadLetter.AttemptCount);
        Assert.NotNull(deadLetter.DeadLetteredAt);
        Assert.Null(deadLetter.LockToken);
    }

    [Fact]
    public async Task StaleProcessingLockIsRecoveredWithoutDeletingPendingWork()
    {
        var tenant = new CurrentTenantService();
        tenant.SetPlatformScope();
        await using var dbContext = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
            tenant);
        var repository = new OutboxEventRepository(dbContext);
        var now = DateTimeOffset.UtcNow;
        var eventItem = new OutboxEvent(Guid.NewGuid())
        {
            TenantId = Guid.NewGuid(),
            EventType = "Projects.TaskChanged.v1",
            PayloadSchemaVersion = 1,
            AggregateType = "Task",
            AggregateId = Guid.NewGuid(),
            OccurredAt = now.AddMinutes(-5),
            PayloadJson = "{}",
            RoutingJson = "[]",
            Status = OutboxEventStatus.Processing,
            LockedAt = now.AddMinutes(-5),
            LockOwner = "failed-worker",
            LockToken = Guid.NewGuid()
        };
        await repository.AddAsync(eventItem);
        await dbContext.SaveChangesAsync();

        Assert.Equal(1, await repository.RecoverStaleLocksAsync(now.AddMinutes(-1), now, 10));

        Assert.Equal(OutboxEventStatus.RetryScheduled, eventItem.Status);
        Assert.Equal(1, eventItem.AttemptCount);
        Assert.Equal(now, eventItem.NextAttemptAt);
        Assert.Null(eventItem.LockedAt);
    }
}
