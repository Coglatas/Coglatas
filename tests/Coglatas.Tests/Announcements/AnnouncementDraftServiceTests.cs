using Coglatas.Application;
using Coglatas.Application.Announcements;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Realtime;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Coglatas.Tests.Announcements;

[Trait("Scope", "Issue378")]
public sealed class AnnouncementDraftServiceTests
{
    [Fact]
    public async Task ApplicationOnlyCompositionProvidesFailClosedDraftPersistence()
    {
        await using var provider = new ServiceCollection()
            .AddApplication()
            .BuildServiceProvider();
        using var scope = provider.CreateScope();
        var drafts = scope.ServiceProvider.GetRequiredService<IAnnouncementDraftRepository>();

        Assert.Null(await drafts.GetAsync(Guid.NewGuid()));
        Assert.Empty(await drafts.ClaimDueAsync(
            "announcement-test-worker",
            DateTimeOffset.UtcNow,
            1,
            TimeSpan.FromMinutes(1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            drafts.AddAsync(new AnnouncementDraft()));
    }

    [Fact]
    public async Task ImmediatePublishIsIdempotentlyQueuedThenWorkerPublishesOneDurableAnnouncement()
    {
        await using var fixture = await Fixture.CreateAsync();

        var created = await fixture.Service.CreateAsync(fixture.Request(), "draft-create-0001");
        var replayedCreate = await fixture.Service.CreateAsync(fixture.Request(), "draft-create-0001");

        Assert.True(created.IsSuccess, created.Error);
        Assert.True(replayedCreate.IsSuccess, replayedCreate.Error);
        Assert.Equal(created.Value!.Id, replayedCreate.Value!.Id);
        Assert.Equal(1, await fixture.Db.AnnouncementDrafts.CountAsync());

        var published = await fixture.Service.PublishNowAsync(
            created.Value.Id,
            new PublishAnnouncementDraftRequest(created.Value.Version),
            "draft-publish-0001");
        // Replay retains the request's original version. The accepted draft is
        // now Scheduled, but the idempotency record must still reconcile it.
        var replayedPublish = await fixture.Service.PublishNowAsync(
            created.Value.Id,
            new PublishAnnouncementDraftRequest(created.Value.Version),
            "draft-publish-0001");

        Assert.True(published.IsSuccess, published.Error);
        Assert.True(replayedPublish.IsSuccess, replayedPublish.Error);
        Assert.Equal(AnnouncementDraftStatus.Scheduled, published.Value!.Status);
        Assert.Equal(published.Value.ScheduledForUtc, replayedPublish.Value!.ScheduledForUtc);
        Assert.Equal("UTC", published.Value.ScheduleTimeZoneId);
        Assert.Empty(await fixture.Db.Announcements.ToListAsync());
        Assert.Single(await fixture.Db.AnnouncementDrafts.ToListAsync());
        Assert.Equal(2, await fixture.Db.IdempotencyRecords.CountAsync());
        Assert.Empty(fixture.Invalidations.AnnouncementChanges);

        fixture.Clock.UtcNow = published.Value.ScheduledForUtc!.Value;
        var claim = Assert.Single(await fixture.Service.ClaimDueAsync(
            "announcement-test-worker",
            fixture.Clock.UtcNow,
            10,
            TimeSpan.FromMinutes(2)));
        await fixture.Service.ProcessAsync(claim, fixture.Clock.UtcNow, TimeSpan.FromMinutes(5));

        var persisted = await fixture.Db.AnnouncementDrafts.SingleAsync();
        Assert.Equal(AnnouncementDraftStatus.Published, persisted.Status);
        Assert.NotNull(persisted.PublishedAnnouncementId);
        Assert.Single(await fixture.Db.Announcements.ToListAsync());
        Assert.Single(fixture.Invalidations.AnnouncementChanges);
    }

    [Fact]
    public async Task ScheduleResolvesIanaLocalTimeOnceAndWorkerPublishesThatDurableDraft()
    {
        await using var fixture = await Fixture.CreateAsync();
        var created = await fixture.Service.CreateAsync(fixture.Request(), "draft-create-0002");
        Assert.True(created.IsSuccess, created.Error);

        var local = new DateTime(2026, 9, 1, 9, 30, 0, DateTimeKind.Unspecified);
        var scheduled = await fixture.Service.ScheduleAsync(
            created.Value!.Id,
            new ScheduleAnnouncementDraftRequest(created.Value.Version, local, "Asia/Tokyo"),
            "draft-schedule-0002");
        var replayedSchedule = await fixture.Service.ScheduleAsync(
            created.Value.Id,
            new ScheduleAnnouncementDraftRequest(created.Value.Version, local, "Asia/Tokyo"),
            "draft-schedule-0002");

        Assert.True(scheduled.IsSuccess, scheduled.Error);
        Assert.True(replayedSchedule.IsSuccess, replayedSchedule.Error);
        Assert.Equal(AnnouncementDraftStatus.Scheduled, scheduled.Value!.Status);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 30, 0, TimeSpan.Zero), scheduled.Value.ScheduledForUtc);
        Assert.Equal(local, scheduled.Value.ScheduleLocalDateTime);
        Assert.Equal("Asia/Tokyo", scheduled.Value.ScheduleTimeZoneId);
        Assert.Equal(scheduled.Value.ScheduledForUtc, replayedSchedule.Value!.ScheduledForUtc);

        fixture.Clock.UtcNow = scheduled.Value.ScheduledForUtc!.Value;
        var claims = await fixture.Service.ClaimDueAsync("announcement-test-worker", fixture.Clock.UtcNow, 10, TimeSpan.FromMinutes(2));
        var claim = Assert.Single(claims);
        await fixture.Service.ProcessAsync(claim, fixture.Clock.UtcNow, TimeSpan.FromMinutes(5));

        var persisted = await fixture.Db.AnnouncementDrafts.SingleAsync();
        Assert.Equal(AnnouncementDraftStatus.Published, persisted.Status);
        Assert.NotNull(persisted.PublishedAnnouncementId);
        Assert.Equal(fixture.Clock.UtcNow, persisted.PublishedAtUtc);
        Assert.Single(await fixture.Db.Announcements.ToListAsync());
        Assert.Single(fixture.Invalidations.AnnouncementChanges);
    }

    [Fact]
    public async Task DueWorkerDoesNotPublishWhenAudienceAuthorizationIsLostAfterScheduling()
    {
        await using var fixture = await Fixture.CreateAsync();
        var created = await fixture.Service.CreateAsync(fixture.Request(), "draft-create-0003");
        Assert.True(created.IsSuccess, created.Error);

        var scheduled = await fixture.Service.ScheduleAsync(
            created.Value!.Id,
            new ScheduleAnnouncementDraftRequest(
                created.Value.Version,
                new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Unspecified),
                "Asia/Tokyo"),
            "draft-schedule-0003");
        Assert.True(scheduled.IsSuccess, scheduled.Error);

        fixture.Clock.UtcNow = scheduled.Value!.ScheduledForUtc!.Value;
        var claim = Assert.Single(await fixture.Service.ClaimDueAsync(
            "announcement-test-worker",
            fixture.Clock.UtcNow,
            10,
            TimeSpan.FromMinutes(2)));
        // First recheck occurs while validating the persisted content; the
        // second is the explicit publish-time audience reauthorization.
        fixture.Audiences.Outcomes.Enqueue(true);
        fixture.Audiences.Outcomes.Enqueue(false);

        await fixture.Service.ProcessAsync(claim, fixture.Clock.UtcNow, TimeSpan.FromMinutes(5));

        var persisted = await fixture.Db.AnnouncementDrafts.SingleAsync();
        Assert.Equal(AnnouncementDraftStatus.Scheduled, persisted.Status);
        Assert.Equal("AudienceNoLongerAuthorized", persisted.LastPublicationFailureCode);
        Assert.True(persisted.NextPublicationAttemptAtUtc > fixture.Clock.UtcNow);
        Assert.Null(persisted.PublicationClaimToken);
        Assert.Empty(await fixture.Db.Announcements.ToListAsync());
        Assert.Empty(fixture.Invalidations.AnnouncementChanges);
    }

    [Fact]
    public async Task EditRequiresTheCurrentVersionAndGetFailsClosedAcrossTenantOrAuthorBoundaries()
    {
        await using var fixture = await Fixture.CreateAsync();
        var created = await fixture.Service.CreateAsync(fixture.Request("Original"), "draft-create-0004");
        Assert.True(created.IsSuccess, created.Error);

        var stale = await fixture.Service.SaveAsync(
            created.Value!.Id,
            new SaveAnnouncementDraftRequest(
                ExpectedVersion: created.Value.Version - 1,
                fixture.Content("Changed")));
        Assert.False(stale.IsSuccess);
        Assert.Equal("ANNOUNCEMENT_DRAFT_STALE", stale.ErrorDetail!.Code);
        Assert.Equal("Original", (await fixture.Db.AnnouncementDrafts.SingleAsync()).Title);

        fixture.Tenant.SetTenant(Guid.NewGuid(), "other-tenant");
        var crossTenant = await fixture.Service.GetAsync(created.Value.Id);
        Assert.False(crossTenant.IsSuccess);
        Assert.Equal("ANNOUNCEMENT_DRAFT_NOT_FOUND", crossTenant.ErrorDetail!.Code);

        fixture.Tenant.SetTenant(fixture.TenantId, "fixture-tenant");
        fixture.CurrentUser.UserId = Guid.NewGuid();
        var otherAuthor = await fixture.Service.GetAsync(created.Value.Id);
        Assert.False(otherAuthor.IsSuccess);
        Assert.Equal("ANNOUNCEMENT_DRAFT_NOT_FOUND", otherAuthor.ErrorDetail!.Code);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            AppDbContext db,
            CurrentTenantService tenant,
            TestCurrentUser currentUser,
            MutableClock clock,
            TestAudienceService audiences,
            RecordingInvalidations invalidations,
            AnnouncementDraftService service,
            Guid tenantId,
            Guid workspaceId)
        {
            Db = db;
            Tenant = tenant;
            CurrentUser = currentUser;
            Clock = clock;
            Audiences = audiences;
            Invalidations = invalidations;
            Service = service;
            TenantId = tenantId;
            WorkspaceId = workspaceId;
        }

        public AppDbContext Db { get; }
        public CurrentTenantService Tenant { get; }
        public TestCurrentUser CurrentUser { get; }
        public MutableClock Clock { get; }
        public TestAudienceService Audiences { get; }
        public RecordingInvalidations Invalidations { get; }
        public AnnouncementDraftService Service { get; }
        public Guid TenantId { get; }
        private Guid WorkspaceId { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var tenantId = Guid.NewGuid();
            var workspaceId = Guid.NewGuid();
            var actorId = Guid.NewGuid();
            var tenant = new CurrentTenantService();
            var db = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseInMemoryDatabase($"announcement-draft-{Guid.NewGuid():N}")
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
            var currentUser = new TestCurrentUser(actorId);
            var clock = new MutableClock(new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.Zero));
            var audiences = new TestAudienceService();
            var invalidations = new RecordingInvalidations();
            var service = new AnnouncementDraftService(
                new AnnouncementDraftRepository(db, tenant),
                new TestAnnouncementRepository(db),
                audiences,
                new TestScheduleTimeZoneResolver(),
                new EfCreateIdempotencyCoordinator(db),
                currentUser,
                tenant,
                clock,
                new RecordingAuditLogger(),
                invalidations,
                new DbUnitOfWork(db));
            await Task.CompletedTask;
            return new Fixture(db, tenant, currentUser, clock, audiences, invalidations, service, tenantId, workspaceId);
        }

        public CreateAnnouncementDraftRequest Request(string title = "Announcement") => new(Content(title));

        public AnnouncementDraftContentRequest Content(string title = "Announcement") => new(
            new AnnouncementDraftTargetRequest(WorkspaceId, null, null),
            title,
            "A durable announcement body.",
            AnnouncementPriority.Important,
            false,
            true);

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class TestCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid? UserId { get; set; } = userId;
        public Guid? SessionId => null;
        public string Email => "author@example.test";
        public SystemRole? SystemRole => Coglatas.Domain.Enums.SystemRole.Teacher;
        public bool IsAuthenticated => UserId.HasValue;
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    private sealed class TestAudienceService : IAnnouncementAudienceService
    {
        public Queue<bool> Outcomes { get; } = [];
        private bool IsAllowed { get; } = true;

        public Task<Result<IReadOnlyList<AnnouncementAudienceOptionResponse>>> ListAsync(CancellationToken cancellationToken = default) =>
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

        private bool Next() => Outcomes.TryDequeue(out var value) ? value : IsAllowed;
    }

    private sealed class TestScheduleTimeZoneResolver : IAnnouncementScheduleTimeZoneResolver
    {
        public Task<TimeZoneInfo> ResolveAsync(
            Guid tenantId,
            Guid? workspaceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo"));
    }

    private sealed class TestAnnouncementRepository(AppDbContext db) : IAnnouncementRepository
    {
        public Task<PagedResponse<Announcement>> ListVisibleAsync(Guid userId, bool isSystemAdmin, AnnouncementListQuery query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Announcement?> GetAsync(Guid announcementId, CancellationToken cancellationToken = default) =>
            db.Announcements.SingleOrDefaultAsync(announcement => announcement.Id == announcementId, cancellationToken);

        public Task<bool> IsVisibleToUserAsync(Guid announcementId, Guid userId, bool isSystemAdmin, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> HasReadAsync(Guid announcementId, Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task AddAsync(Announcement announcement, CancellationToken cancellationToken = default) =>
            db.Announcements.AddAsync(announcement, cancellationToken).AsTask();

        public Task AddReadAsync(AnnouncementRead read, CancellationToken cancellationToken = default) =>
            db.AnnouncementReads.AddAsync(read, cancellationToken).AsTask();

        public Task<IReadOnlyList<AnnouncementTargetUser>> ListTargetUsersAsync(Announcement announcement, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AnnouncementTargetUser>>([
                new AnnouncementTargetUser(announcement.AuthorUserId, "Author", "author@example.test", false)
            ]);

        public Task<int> CountReadsAsync(Guid announcementId, CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private sealed class DbUnitOfWork(AppDbContext db) : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => db.SaveChangesAsync(cancellationToken);
    }

    private sealed class RecordingAuditLogger : IAuditLogger
    {
        public Task LogAsync(AuditLogEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingInvalidations : IBusinessInvalidationPublisher
    {
        public List<Guid> AnnouncementChanges { get; } = [];

        public Task TaskChangedAsync(TaskItem task, Guid actorUserId, string change, IEnumerable<string>? changedFields = null, IEnumerable<Guid>? affectedUserIds = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ProjectChangedAsync(Project project, Guid actorUserId, string change, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FileChangedAsync(FileObject fileObject, Attachment attachment, Guid actorUserId, string change, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task AnnouncementChangedAsync(
            Announcement announcement,
            Guid actorUserId,
            string change,
            IEnumerable<Guid> audienceUserIds,
            CancellationToken cancellationToken = default)
        {
            AnnouncementChanges.Add(announcement.Id);
            return Task.CompletedTask;
        }
    }
}
