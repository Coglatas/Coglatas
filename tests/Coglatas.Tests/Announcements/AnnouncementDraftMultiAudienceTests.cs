using Coglatas.Application.Announcements;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Realtime;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Tests.Announcements;

[Trait("Scope", "Issue388")]
public sealed class AnnouncementDraftMultiAudienceTests
{
    [Fact]
    public async Task DispatchResolvesAllTargetsAndDeduplicatesOverlappingRecipientsIntoOneAnnouncement()
    {
        await using var fixture = await Fixture.CreateAsync();
        var created = await fixture.Service.CreateAsync(fixture.MultiTargetRequest(), "multi-create-0001");
        Assert.True(created.IsSuccess, created.Error);
        Assert.Equal(2, created.Value!.Targets.Count);
        Assert.Equal(2, fixture.Distribution.DraftTargets[created.Value.Id].Count);

        var queued = await fixture.Service.PublishNowAsync(
            created.Value.Id,
            new PublishAnnouncementDraftRequest(created.Value.Version),
            "multi-publish-0001");
        Assert.True(queued.IsSuccess, queued.Error);

        fixture.Clock.UtcNow = queued.Value!.ScheduledForUtc!.Value;
        var claim = Assert.Single(await fixture.Service.ClaimDueAsync(
            "issue388-worker",
            fixture.Clock.UtcNow,
            10,
            TimeSpan.FromMinutes(2)));
        await fixture.Service.ProcessAsync(claim, fixture.Clock.UtcNow, TimeSpan.FromMinutes(5));

        var announcement = Assert.Single(await fixture.Db.Announcements.ToListAsync());
        Assert.Equal("Multi audience update", announcement.Title);
        Assert.Equal("One canonical body for every target.", announcement.Body);
        Assert.Equal(2, fixture.Distribution.PublishedTargets[announcement.Id].Count);

        var recipientIds = fixture.Notifications.Deliveries.Select(delivery => delivery.UserId).ToArray();
        Assert.Equal(3, recipientIds.Length);
        Assert.Equal(3, recipientIds.Distinct().Count());
        Assert.Contains(fixture.OverlapUserId, recipientIds);
        Assert.All(
            fixture.Notifications.Deliveries,
            delivery => Assert.Equal(
                AnnouncementDistributionContract.DeliveryLogicalKey(announcement.Id),
                delivery.LogicalKey));
    }

    [Fact]
    public async Task PublicationFailsClosedWhenAnySelectedTargetIsNoLongerAuthorized()
    {
        await using var fixture = await Fixture.CreateAsync();
        var created = await fixture.Service.CreateAsync(fixture.MultiTargetRequest(), "multi-create-0002");
        Assert.True(created.IsSuccess, created.Error);
        var queued = await fixture.Service.PublishNowAsync(
            created.Value!.Id,
            new PublishAnnouncementDraftRequest(created.Value.Version),
            "multi-publish-0002");
        Assert.True(queued.IsSuccess, queued.Error);

        fixture.Clock.UtcNow = queued.Value!.ScheduledForUtc!.Value;
        var claim = Assert.Single(await fixture.Service.ClaimDueAsync(
            "issue388-worker",
            fixture.Clock.UtcNow,
            10,
            TimeSpan.FromMinutes(2)));
        fixture.Audiences.Outcomes.Enqueue(true);
        fixture.Audiences.Outcomes.Enqueue(false);

        await fixture.Service.ProcessAsync(claim, fixture.Clock.UtcNow, TimeSpan.FromMinutes(5));

        Assert.Empty(await fixture.Db.Announcements.ToListAsync());
        Assert.Empty(fixture.Notifications.Deliveries);
        var persisted = await fixture.Db.AnnouncementDrafts.SingleAsync();
        Assert.Equal(AnnouncementDraftStatus.Scheduled, persisted.Status);
        Assert.NotNull(persisted.LastPublicationFailureCode);
        Assert.Null(persisted.PublicationClaimToken);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            AppDbContext db,
            CurrentTenantService tenant,
            MutableClock clock,
            TestAudienceService audiences,
            TestDistributionStore distribution,
            RecordingNotificationService notifications,
            AnnouncementDraftService service,
            Guid workspaceId,
            Guid groupId,
            Guid channelId,
            Guid overlapUserId)
        {
            Db = db;
            Tenant = tenant;
            Clock = clock;
            Audiences = audiences;
            Distribution = distribution;
            Notifications = notifications;
            Service = service;
            WorkspaceId = workspaceId;
            GroupId = groupId;
            ChannelId = channelId;
            OverlapUserId = overlapUserId;
        }

        public AppDbContext Db { get; }
        public CurrentTenantService Tenant { get; }
        public MutableClock Clock { get; }
        public TestAudienceService Audiences { get; }
        public TestDistributionStore Distribution { get; }
        public RecordingNotificationService Notifications { get; }
        public AnnouncementDraftService Service { get; }
        public Guid WorkspaceId { get; }
        public Guid GroupId { get; }
        public Guid ChannelId { get; }
        public Guid OverlapUserId { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var tenantId = Guid.NewGuid();
            var workspaceId = Guid.NewGuid();
            var groupId = Guid.NewGuid();
            var channelId = Guid.NewGuid();
            var actorId = Guid.NewGuid();
            var overlapUserId = Guid.NewGuid();
            var groupOnlyUserId = Guid.NewGuid();
            var channelOnlyUserId = Guid.NewGuid();
            var tenant = new CurrentTenantService();
            var db = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseInMemoryDatabase($"announcement-multi-{Guid.NewGuid():N}")
                    .Options,
                tenant);
            tenant.SetPlatformScope();
            db.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Name = "Fixture tenant",
                DisplayName = "Fixture tenant",
                Slug = "fixture-tenant",
                Status = TenantStatus.Active
            });
            await db.SaveChangesAsync();
            tenant.SetTenant(tenantId, "fixture-tenant");

            var clock = new MutableClock(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
            var audiences = new TestAudienceService();
            var distribution = new TestDistributionStore(db);
            var notifications = new RecordingNotificationService();
            var announcementRepository = new TestAnnouncementRepository(
                db,
                groupId,
                channelId,
                overlapUserId,
                groupOnlyUserId,
                channelOnlyUserId);
            var service = new AnnouncementDraftService(
                new AnnouncementDraftRepository(db, tenant),
                announcementRepository,
                audiences,
                new TestScheduleTimeZoneResolver(),
                new EfCreateIdempotencyCoordinator(db),
                new TestCurrentUser(actorId),
                tenant,
                clock,
                new RecordingAuditLogger(),
                new RecordingInvalidations(),
                new DbUnitOfWork(db),
                notifications,
                distribution);

            return new Fixture(
                db,
                tenant,
                clock,
                audiences,
                distribution,
                notifications,
                service,
                workspaceId,
                groupId,
                channelId,
                overlapUserId);
        }

        public CreateAnnouncementDraftRequest MultiTargetRequest() => new(
            new AnnouncementDraftContentRequest(
                new AnnouncementDraftTargetRequest(WorkspaceId, GroupId, null),
                "Multi audience update",
                "One canonical body for every target.",
                AnnouncementPriority.Important,
                false,
                true,
                Targets:
                [
                    new AnnouncementDraftTargetRequest(WorkspaceId, GroupId, null),
                    new AnnouncementDraftTargetRequest(WorkspaceId, GroupId, ChannelId)
                ]));

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class TestCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid? UserId => userId;
        public Guid? SessionId => null;
        public string? Email => "author@example.test";
        public SystemRole? SystemRole => Coglatas.Domain.Enums.SystemRole.Teacher;
        public bool IsAuthenticated => true;
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    private sealed class TestAudienceService : IAnnouncementAudienceService
    {
        public Queue<bool> Outcomes { get; } = [];

        public Task<Result<IReadOnlyList<AnnouncementAudienceOptionResponse>>> ListAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IReadOnlyList<AnnouncementAudienceOptionResponse>>.Success([]));

        public Task<Result<bool>> IsAuthorizedAsync(
            Guid? workspaceId,
            Guid? groupId,
            Guid? channelId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<bool>.Success(Next()));

        public Task<Result<bool>> IsAuthorizedForActorAsync(
            Guid actorUserId,
            Guid? workspaceId,
            Guid? groupId,
            Guid? channelId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<bool>.Success(actorUserId != Guid.Empty && Next()));

        private bool Next() => !Outcomes.TryDequeue(out var value) || value;
    }

    private sealed class TestScheduleTimeZoneResolver : IAnnouncementScheduleTimeZoneResolver
    {
        public Task<TimeZoneInfo> ResolveAsync(
            Guid tenantId,
            Guid? workspaceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo"));
    }

    private sealed class TestAnnouncementRepository(
        AppDbContext db,
        Guid groupId,
        Guid channelId,
        Guid overlapUserId,
        Guid groupOnlyUserId,
        Guid channelOnlyUserId) : IAnnouncementRepository
    {
        public Task<PagedResponse<Announcement>> ListVisibleAsync(
            Guid userId,
            bool isSystemAdmin,
            AnnouncementListQuery query,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Announcement?> GetAsync(Guid announcementId, CancellationToken cancellationToken = default) =>
            db.Announcements.SingleOrDefaultAsync(announcement => announcement.Id == announcementId, cancellationToken);

        public Task<bool> IsVisibleToUserAsync(
            Guid announcementId,
            Guid userId,
            bool isSystemAdmin,
            CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<bool> HasReadAsync(Guid announcementId, Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task AddAsync(Announcement announcement, CancellationToken cancellationToken = default) =>
            db.Announcements.AddAsync(announcement, cancellationToken).AsTask();

        public Task AddReadAsync(AnnouncementRead read, CancellationToken cancellationToken = default) =>
            db.AnnouncementReads.AddAsync(read, cancellationToken).AsTask();

        public Task<IReadOnlyList<AnnouncementTargetUser>> ListTargetUsersAsync(
            Announcement announcement,
            CancellationToken cancellationToken = default)
        {
            if (announcement.ChannelId == channelId)
            {
                return Task.FromResult<IReadOnlyList<AnnouncementTargetUser>>([
                    User(overlapUserId, "Overlap"),
                    User(channelOnlyUserId, "Channel only")
                ]);
            }
            if (announcement.GroupId == groupId)
            {
                return Task.FromResult<IReadOnlyList<AnnouncementTargetUser>>([
                    User(overlapUserId, "Overlap"),
                    User(groupOnlyUserId, "Group only")
                ]);
            }
            return Task.FromResult<IReadOnlyList<AnnouncementTargetUser>>([]);
        }

        public Task<int> CountReadsAsync(Guid announcementId, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        private static AnnouncementTargetUser User(Guid id, string name) =>
            new(id, name, $"{id:N}@example.test", false);
    }

    private sealed class TestDistributionStore(AppDbContext db) : IAnnouncementDistributionStore
    {
        public Dictionary<Guid, IReadOnlyList<AnnouncementDraftTargetRequest>> DraftTargets { get; } = [];
        public Dictionary<Guid, IReadOnlyList<AnnouncementDraftTargetRequest>> PublishedTargets { get; } = [];

        public Task StageCreatedDraftTargetsAsync(
            Guid tenantId,
            Guid draftId,
            IReadOnlyList<AnnouncementDraftTargetRequest> targets,
            CancellationToken cancellationToken = default)
        {
            DraftTargets[draftId] = targets.ToArray();
            return Task.CompletedTask;
        }

        public async Task CommitDraftSaveAsync(
            Guid tenantId,
            Guid draftId,
            IReadOnlyList<AnnouncementDraftTargetRequest> targets,
            CancellationToken cancellationToken = default)
        {
            DraftTargets[draftId] = targets.ToArray();
            await db.SaveChangesAsync(cancellationToken);
        }

        public Task<IReadOnlyList<AnnouncementDraftTargetRequest>> GetDraftTargetsAsync(
            Guid tenantId,
            Guid draftId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                DraftTargets.TryGetValue(draftId, out var targets)
                    ? targets
                    : (IReadOnlyList<AnnouncementDraftTargetRequest>)[]);

        public Task<IReadOnlyList<AnnouncementDraftTargetRequest>> GetAnnouncementTargetsAsync(
            Guid tenantId,
            Guid announcementId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                PublishedTargets.TryGetValue(announcementId, out var targets)
                    ? targets
                    : (IReadOnlyList<AnnouncementDraftTargetRequest>)[]);

        public async Task CommitPublicationAsync(
            Guid tenantId,
            Guid announcementId,
            IReadOnlyList<AnnouncementDraftTargetRequest> targets,
            Func<CancellationToken, Task> stagePublication,
            CancellationToken cancellationToken = default)
        {
            await stagePublication(cancellationToken);
            PublishedTargets[announcementId] = targets.ToArray();
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed class RecordingNotificationService : INotificationService
    {
        public List<Delivery> Deliveries { get; } = [];

        public Task<Guid> CreateOrGetByLogicalKeyAsync(
            Guid userId,
            NotificationType type,
            string title,
            string? body,
            string? relatedEntityType,
            Guid? relatedEntityId,
            string logicalKey,
            CancellationToken cancellationToken = default)
        {
            var existing = Deliveries.FirstOrDefault(delivery =>
                delivery.UserId == userId && delivery.LogicalKey == logicalKey);
            if (existing is not null)
            {
                return Task.FromResult(existing.Id);
            }
            var delivery = new Delivery(Guid.NewGuid(), userId, logicalKey, relatedEntityId);
            Deliveries.Add(delivery);
            return Task.FromResult(delivery.Id);
        }

        public Task NotifyAsync(
            Guid recipientUserId,
            string title,
            string? body,
            string sourceType,
            Guid sourceId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public sealed record Delivery(Guid Id, Guid UserId, string LogicalKey, Guid? AnnouncementId);
    }

    private sealed class DbUnitOfWork(AppDbContext db) : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            db.SaveChangesAsync(cancellationToken);
    }

    private sealed class RecordingAuditLogger : IAuditLogger
    {
        public Task LogAsync(AuditLogEntry entry, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingInvalidations : IBusinessInvalidationPublisher
    {
        public Task TaskChangedAsync(
            TaskItem task,
            Guid actorUserId,
            string change,
            IEnumerable<string>? changedFields = null,
            IEnumerable<Guid>? affectedUserIds = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ProjectChangedAsync(
            Project project,
            Guid actorUserId,
            string change,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task FileChangedAsync(
            FileObject fileObject,
            Attachment attachment,
            Guid actorUserId,
            string change,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task AnnouncementChangedAsync(
            Announcement announcement,
            Guid actorUserId,
            string change,
            IEnumerable<Guid> audienceUserIds,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
