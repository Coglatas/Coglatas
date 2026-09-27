using Coglatas.Application.Announcements;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;

namespace Coglatas.Tests.Announcements;

public sealed class AnnouncementPaginationSafetyTests
{
    [Fact]
    public async Task ListAsyncBoundsLargePositivePageBeforeRepositoryOffsetCalculation()
    {
        var userId = Guid.NewGuid();
        var repository = new CapturingAnnouncementRepository();
        var service = new AnnouncementService(
            repository,
            null!,
            null!,
            null!,
            new NonAdminUserRepository(),
            null!,
            null!,
            null!,
            new AuthenticatedUser(userId),
            null!,
            null!,
            null!,
            null!,
            null!);

        var result = await service.ListAsync(new AnnouncementListQuery(
            Page: 551_091_223,
            PageSize: 1_044_319));

        Assert.True(result.IsSuccess, result.Error);
        var normalized = Assert.IsType<AnnouncementListQuery>(repository.LastQuery);
        Assert.Equal(100, normalized.PageSize);
        Assert.True(normalized.Page >= 1);
        Assert.True(
            (normalized.Page - 1L) * normalized.PageSize <= int.MaxValue,
            "Normalized announcement pagination must remain representable by EF Core Skip(int).");
    }

    private sealed class CapturingAnnouncementRepository : IAnnouncementRepository
    {
        public AnnouncementListQuery? LastQuery { get; private set; }

        public Task<PagedResponse<Announcement>> ListVisibleAsync(
            Guid userId,
            bool isSystemAdmin,
            AnnouncementListQuery query,
            CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            return Task.FromResult(new PagedResponse<Announcement>([], query.Page, query.PageSize, 0));
        }

        public Task<Announcement?> GetAsync(Guid announcementId, CancellationToken cancellationToken = default) =>
            Task.FromResult<Announcement?>(null);

        public Task<bool> IsVisibleToUserAsync(
            Guid announcementId,
            Guid userId,
            bool isSystemAdmin,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> HasReadAsync(Guid announcementId, Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task AddAsync(Announcement announcement, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task AddReadAsync(AnnouncementRead read, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<AnnouncementTargetUser>> ListTargetUsersAsync(
            Announcement announcement,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AnnouncementTargetUser>>([]);

        public Task<int> CountReadsAsync(Guid announcementId, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    private sealed class NonAdminUserRepository : IUserRepository
    {
        public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<User?>(null);

        public Task<User?> GetByNormalizedEmailAsync(
            string normalizedEmail,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<User?>(null);

        public Task AddAsync(User user, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class AuthenticatedUser(Guid userId) : ICurrentUser
    {
        public Guid? UserId => userId;
        public Guid? SessionId => null;
        public string? Email => null;
        public SystemRole? SystemRole => Coglatas.Domain.Enums.SystemRole.User;
        public bool IsAuthenticated => true;
    }
}
