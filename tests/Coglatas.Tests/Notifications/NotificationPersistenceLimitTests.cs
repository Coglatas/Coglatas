using Coglatas.Application.Common.Tenancy;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Tests.Notifications;

public sealed class NotificationPersistenceLimitTests
{
    [Fact]
    public async Task CreateAsyncClampsNotificationTextToPersistenceLimitsWithoutSplittingUnicodeScalars()
    {
        var tenantId = Guid.NewGuid();
        var tenant = new CurrentTenantService();
        tenant.SetPlatformScope();
        await using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options,
            tenant);
        db.Tenants.Add(new Tenant(tenantId)
        {
            Name = "Tenant",
            DisplayName = "Tenant",
            Slug = $"tenant-{tenantId:N}",
            Status = TenantStatus.Active
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        tenant.SetTenant(tenantId, $"tenant-{tenantId:N}");

        var service = new DbNotificationService(db, FixedClock.Instance, tenant);
        var title = string.Concat(Enumerable.Repeat("🙂", Notification.TitleMaximumLength + 20));
        var body = string.Concat(Enumerable.Repeat("界", Notification.BodyMaximumLength + 200));

        var id = await service.CreateAsync(
            Guid.NewGuid(),
            NotificationType.Event,
            title,
            body,
            "Event",
            Guid.NewGuid());

        var notification = Assert.Single(db.Notifications.Local);
        Assert.Equal(id, notification.Id);
        Assert.Equal(Notification.TitleMaximumLength, notification.Title.EnumerateRunes().Count());
        Assert.Equal(Notification.BodyMaximumLength, notification.Body!.EnumerateRunes().Count());
        Assert.EndsWith("🙂", notification.Title, StringComparison.Ordinal);
        Assert.EndsWith("界", notification.Body, StringComparison.Ordinal);

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task CreateAsyncDeduplicatesUsingThePersistedClampedTitle()
    {
        var tenantId = Guid.NewGuid();
        var tenant = new CurrentTenantService();
        tenant.SetPlatformScope();
        await using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options,
            tenant);
        db.Tenants.Add(new Tenant(tenantId)
        {
            Name = "Tenant",
            DisplayName = "Tenant",
            Slug = $"tenant-{tenantId:N}",
            Status = TenantStatus.Active
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        tenant.SetTenant(tenantId, $"tenant-{tenantId:N}");

        var service = new DbNotificationService(db, FixedClock.Instance, tenant);
        var userId = Guid.NewGuid();
        var relatedEntityId = Guid.NewGuid();
        var title = string.Concat(Enumerable.Repeat("🙂", Notification.TitleMaximumLength + 20));

        var firstId = await service.CreateAsync(
            userId,
            NotificationType.Event,
            title,
            null,
            "Event",
            relatedEntityId);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var secondId = await service.CreateAsync(
            userId,
            NotificationType.Event,
            title,
            null,
            "Event",
            relatedEntityId);

        Assert.Equal(firstId, secondId);
        Assert.Single(await db.Notifications.ToListAsync());
    }

    [Fact]
    public void NotificationEntityLimitsMatchEfPersistenceConfiguration()
    {
        var tenant = TenantScope(Guid.NewGuid());
        using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options,
            tenant);
        var entity = db.Model.FindEntityType(typeof(Notification));

        Assert.NotNull(entity);
        Assert.Equal(
            Notification.TitleMaximumLength,
            entity!.FindProperty(nameof(Notification.Title))!.GetMaxLength());
        Assert.Equal(
            Notification.BodyMaximumLength,
            entity.FindProperty(nameof(Notification.Body))!.GetMaxLength());
    }

    private static CurrentTenantService TenantScope(Guid tenantId)
    {
        var tenant = new CurrentTenantService();
        tenant.SetTenant(tenantId, $"tenant-{tenantId:N}");
        return tenant;
    }

    private sealed class FixedClock : Coglatas.Application.Common.Interfaces.IClock
    {
        public static FixedClock Instance { get; } = new();
        public DateTimeOffset UtcNow => new(2026, 9, 2, 10, 30, 0, TimeSpan.Zero);
    }
}
