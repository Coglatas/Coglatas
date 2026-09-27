using Coglatas.Application.Announcements;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Infrastructure.Persistence;

public sealed class AnnouncementRepository(
    AppDbContext dbContext,
    IClock clock,
    ICurrentTenant currentTenant) : IAnnouncementRepository
{
    public async Task<PagedResponse<Announcement>> ListVisibleAsync(Guid userId, bool isSystemAdmin, AnnouncementListQuery query, CancellationToken cancellationToken = default)
    {
        var source = dbContext.VisibleAnnouncementsFor(userId, isSystemAdmin, clock.UtcNow)
            .Where(announcement =>
                (!query.WorkspaceId.HasValue || announcement.WorkspaceId == query.WorkspaceId) &&
                (!query.GroupId.HasValue || announcement.GroupId == query.GroupId) &&
                (!query.ChannelId.HasValue || announcement.ChannelId == query.ChannelId))
            .OrderByDescending(announcement => announcement.IsPinned)
            .ThenByDescending(announcement => announcement.PublishedAt);

        var total = await source.CountAsync(cancellationToken);
        var offset = (query.Page - 1L) * query.PageSize;
        if (offset > int.MaxValue)
        {
            return new PagedResponse<Announcement>([], query.Page, query.PageSize, total);
        }

        var items = await source
            .Skip((int)Math.Max(0L, offset))
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResponse<Announcement>(items, query.Page, query.PageSize, total);
    }

    public Task<Announcement?> GetAsync(Guid announcementId, CancellationToken cancellationToken = default)
    {
        return dbContext.Announcements.FirstOrDefaultAsync(announcement => announcement.Id == announcementId, cancellationToken);
    }

    public Task<bool> IsVisibleToUserAsync(Guid announcementId, Guid userId, bool isSystemAdmin, CancellationToken cancellationToken = default)
    {
        return dbContext.VisibleAnnouncementsFor(userId, isSystemAdmin, clock.UtcNow)
            .AnyAsync(announcement => announcement.Id == announcementId, cancellationToken);
    }

    public Task<bool> HasReadAsync(Guid announcementId, Guid userId, CancellationToken cancellationToken = default)
    {
        return dbContext.AnnouncementReads.AnyAsync(read => read.AnnouncementId == announcementId && read.UserId == userId, cancellationToken);
    }

    public async Task AddAsync(Announcement announcement, CancellationToken cancellationToken = default)
    {
        if (announcement.TenantId == Guid.Empty)
        {
            if (!currentTenant.IsAvailable || currentTenant.IsPlatformScope)
            {
                throw new InvalidOperationException("A tenant context is required to create an announcement.");
            }

            announcement.TenantId = currentTenant.TenantId;
        }
        else if (currentTenant is { IsAvailable: true, IsPlatformScope: false })
        {
            if (announcement.TenantId != currentTenant.TenantId)
            {
                throw new InvalidOperationException("Announcement TenantId does not match the current tenant context.");
            }
        }

        await dbContext.Announcements.AddAsync(announcement, cancellationToken);
    }

    public async Task AddReadAsync(AnnouncementRead read, CancellationToken cancellationToken = default)
    {
        await dbContext.AnnouncementReads.AddAsync(read, cancellationToken);
    }

    public async Task<IReadOnlyList<AnnouncementTargetUser>> ListTargetUsersAsync(Announcement announcement, CancellationToken cancellationToken = default)
    {
        var activeTenantUserIds = dbContext.TenantUsers
            .Where(member => member.TenantId == announcement.TenantId && member.Status == TenantUserStatus.Active)
            .Select(member => member.UserId);

        IQueryable<Guid> userIds;
        var deliveryLogicalKey = AnnouncementDistributionContract.DeliveryLogicalKey(announcement.Id);
        var frozenRecipientIds = dbContext.Notifications
            .AsNoTracking()
            .Where(notification =>
                notification.TenantId == announcement.TenantId &&
                notification.NotificationType == NotificationType.Announcement &&
                notification.RelatedEntityType == "Announcement" &&
                notification.RelatedEntityId == announcement.Id &&
                notification.LogicalKey == deliveryLogicalKey)
            .Select(notification => notification.UserId);
        var hasFrozenCohort = await dbContext.AuditLogs
            .AsNoTracking()
            .AnyAsync(log =>
                log.TenantId == announcement.TenantId &&
                log.Action == AnnouncementDistributionContract.FrozenCohortAuditAction &&
                log.EntityType == "Announcement" &&
                log.EntityId == announcement.Id,
                cancellationToken);

        if (hasFrozenCohort)
        {
            // #388 freezes the de-duplicated recipient cohort at dispatch. The
            // audit marker exists even for an empty cohort; recipient rows are
            // the per-user membership ledger. Soft-deleting the visible
            // notification does not remove the row, so read-status/reminders
            // cannot drift to users who join a scope after publication.
            userIds = frozenRecipientIds;
        }
        else if (announcement.ChannelId.HasValue)
        {
            var channel = await dbContext.Channels
                .AsNoTracking()
                .FirstOrDefaultAsync(item =>
                    item.Id == announcement.ChannelId.Value &&
                    item.TenantId == announcement.TenantId &&
                    item.DeletedAt == null &&
                    item.Status == ChannelStatus.Active,
                    cancellationToken);
            if (channel is null ||
                announcement.GroupId != channel.GroupId ||
                announcement.WorkspaceId != channel.WorkspaceId)
            {
                return [];
            }

            var groupIsActive = await dbContext.Groups.AnyAsync(group =>
                group.Id == channel.GroupId &&
                group.TenantId == announcement.TenantId &&
                group.WorkspaceId == channel.WorkspaceId &&
                group.DeletedAt == null &&
                group.Status == GroupStatus.Active,
                cancellationToken);
            var workspaceIsActive = await dbContext.Workspaces.AnyAsync(workspace =>
                workspace.Id == channel.WorkspaceId &&
                workspace.TenantId == announcement.TenantId &&
                workspace.DeletedAt == null &&
                workspace.Status == WorkspaceStatus.Active,
                cancellationToken);
            if (!groupIsActive || !workspaceIsActive)
            {
                return [];
            }

            if (channel.Type is ChannelType.Public or ChannelType.Announcement)
            {
                userIds = dbContext.GroupMembers
                    .Where(member =>
                        member.TenantId == announcement.TenantId &&
                        member.GroupId == channel.GroupId &&
                        dbContext.WorkspaceMembers.Any(workspaceMember =>
                            workspaceMember.TenantId == announcement.TenantId &&
                            workspaceMember.WorkspaceId == channel.WorkspaceId &&
                            workspaceMember.UserId == member.UserId &&
                            workspaceMember.Status == MembershipStatus.Active))
                    .Select(member => member.UserId);
            }
            else
            {
                userIds = dbContext.ChannelMembers
                    .Where(member =>
                        member.TenantId == announcement.TenantId &&
                        member.ChannelId == channel.Id &&
                        dbContext.WorkspaceMembers.Any(workspaceMember =>
                            workspaceMember.TenantId == announcement.TenantId &&
                            workspaceMember.WorkspaceId == channel.WorkspaceId &&
                            workspaceMember.UserId == member.UserId &&
                            workspaceMember.Status == MembershipStatus.Active))
                    .Select(member => member.UserId);
            }
        }
        else if (announcement.GroupId.HasValue)
        {
            var group = await dbContext.Groups
                .AsNoTracking()
                .FirstOrDefaultAsync(item =>
                    item.Id == announcement.GroupId.Value &&
                    item.TenantId == announcement.TenantId &&
                    item.DeletedAt == null &&
                    item.Status == GroupStatus.Active,
                    cancellationToken);
            if (group is null || announcement.WorkspaceId != group.WorkspaceId)
            {
                return [];
            }

            var workspaceIsActive = await dbContext.Workspaces.AnyAsync(workspace =>
                workspace.Id == group.WorkspaceId &&
                workspace.TenantId == announcement.TenantId &&
                workspace.DeletedAt == null &&
                workspace.Status == WorkspaceStatus.Active,
                cancellationToken);
            if (!workspaceIsActive)
            {
                return [];
            }

            userIds = dbContext.GroupMembers
                .Where(member =>
                    member.TenantId == announcement.TenantId &&
                    member.GroupId == group.Id &&
                    dbContext.WorkspaceMembers.Any(workspaceMember =>
                        workspaceMember.TenantId == announcement.TenantId &&
                        workspaceMember.WorkspaceId == group.WorkspaceId &&
                        workspaceMember.UserId == member.UserId &&
                        workspaceMember.Status == MembershipStatus.Active))
                .Select(member => member.UserId);
        }
        else if (announcement.WorkspaceId.HasValue)
        {
            var workspaceIsActive = await dbContext.Workspaces.AnyAsync(workspace =>
                workspace.Id == announcement.WorkspaceId.Value &&
                workspace.TenantId == announcement.TenantId &&
                workspace.DeletedAt == null &&
                workspace.Status == WorkspaceStatus.Active,
                cancellationToken);
            if (!workspaceIsActive)
            {
                return [];
            }

            userIds = dbContext.WorkspaceMembers
                .Where(member =>
                    member.TenantId == announcement.TenantId &&
                    member.WorkspaceId == announcement.WorkspaceId.Value &&
                    member.Status == MembershipStatus.Active)
                .Select(member => member.UserId);
        }
        else
        {
            userIds = activeTenantUserIds;
        }

        userIds = userIds
            .Where(userId => activeTenantUserIds.Contains(userId))
            .Distinct();

        return await dbContext.Users
            .AsNoTracking()
            .Where(user =>
                userIds.Contains(user.Id) &&
                user.Status == UserStatus.Active &&
                user.DeletedAt == null)
            .OrderBy(user => user.DisplayName)
            .ThenBy(user => user.Id)
            .Select(user => new AnnouncementTargetUser(
                user.Id,
                user.DisplayName,
                user.Email,
                dbContext.AnnouncementReads.Any(read => read.AnnouncementId == announcement.Id && read.UserId == user.Id)))
            .ToListAsync(cancellationToken);
    }

    public Task<int> CountReadsAsync(Guid announcementId, CancellationToken cancellationToken = default)
    {
        return dbContext.AnnouncementReads.CountAsync(read => read.AnnouncementId == announcementId, cancellationToken);
    }
}
