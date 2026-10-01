using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Tenancy;
using Coglatas.Application.Workspaces;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Infrastructure.Persistence;

public sealed class WorkspaceDashboardQuery(
    AppDbContext dbContext,
    IMessagingRepository messaging,
    IClock clock) : IWorkspaceDashboardQuery
{
    public bool IsAvailable =>
        string.Equals(
            dbContext.Database.ProviderName,
            "Npgsql.EntityFrameworkCore.PostgreSQL",
            StringComparison.Ordinal);

    public async Task<IReadOnlyList<WorkspaceDashboardListItemResponse>> ListAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException(
                "The Workspace dashboard projection requires the PostgreSQL authorization query provider.");
        }

        var now = clock.UtcNow;
        var activeSystemAdminIds = dbContext.Users
            .AsNoTracking()
            .Where(user =>
                user.Id == userId &&
                user.SystemRole == SystemRole.SystemAdmin &&
                user.Status == UserStatus.Active &&
                user.DeletedAt == null)
            .Select(user => user.Id);

        var rows = await dbContext.Workspaces
            .AsNoTracking()
            .Where(workspace =>
                workspace.DeletedAt == null &&
                workspace.Status == WorkspaceStatus.Active &&
                (activeSystemAdminIds.Contains(userId) ||
                 dbContext.WorkspaceMembers.Any(member =>
                     member.WorkspaceId == workspace.Id &&
                     member.UserId == userId &&
                     member.Status == MembershipStatus.Active)))
            .OrderBy(workspace => workspace.Name)
            .ThenBy(workspace => workspace.Id)
            .Select(workspace => new WorkspaceDashboardRow(
                workspace.Id,
                workspace.Name,
                workspace.Description,
                workspace.Icon,
                workspace.Status,
                workspace.CreatedAt,
                workspace.UpdatedAt,
                dbContext.WorkspaceMembers
                    .Where(member =>
                        member.WorkspaceId == workspace.Id &&
                        member.UserId == userId &&
                        member.Status == MembershipStatus.Active)
                    .Select(member => (WorkspaceRole?)member.Role)
                    .FirstOrDefault(),
                activeSystemAdminIds.Contains(userId),
                dbContext.TenantUsers.Any(tenantUser =>
                    tenantUser.TenantId == workspace.TenantId &&
                    tenantUser.UserId == userId &&
                    tenantUser.Status == TenantUserStatus.Active &&
                    dbContext.Users.Any(user =>
                        user.Id == userId &&
                        user.Status == UserStatus.Active &&
                        user.DeletedAt == null) &&
                    dbContext.Tenants.Any(tenant =>
                        tenant.Id == workspace.TenantId &&
                        tenant.Status == TenantStatus.Active &&
                        tenant.DeletedAt == null)),
                dbContext.Set<CapabilityGrant>().Any(grant =>
                    grant.TenantId == workspace.TenantId &&
                    grant.SubjectUserId == userId &&
                    grant.CapabilityKey == CapabilityKeys.ProjectCreate &&
                    ((grant.ScopeType == CapabilityScopeType.Workspace && grant.ScopeId == workspace.Id) ||
                     (grant.ScopeType == CapabilityScopeType.Tenant && grant.ScopeId == workspace.TenantId)) &&
                    grant.VersionNo > 0 &&
                    grant.GrantedAt <= now &&
                    grant.RevokedAt == null &&
                    (!grant.ExpiresAt.HasValue || grant.ExpiresAt > now)),
                dbContext.Groups.Any(group =>
                    group.TenantId == workspace.TenantId &&
                    group.WorkspaceId == workspace.Id &&
                    group.Status == GroupStatus.Active &&
                    group.DeletedAt == null &&
                    dbContext.GroupMembers.Any(member =>
                        member.TenantId == workspace.TenantId &&
                        member.GroupId == group.Id &&
                        member.UserId == userId &&
                        (member.Role == GroupRole.Owner || member.Role == GroupRole.Admin))),
                dbContext.ProjectMembers
                    .Where(projectMember =>
                        dbContext.Users.Any(user =>
                            user.Id == projectMember.UserId &&
                            user.Status == UserStatus.Active &&
                            user.DeletedAt == null) &&
                        dbContext.Projects.Any(project =>
                            project.Id == projectMember.ProjectId &&
                            project.WorkspaceId == workspace.Id &&
                            project.DeletedAt == null &&
                            project.Status != ProjectStatus.Archived) &&
                        !dbContext.WorkspaceMembers.Any(workspaceMember =>
                            workspaceMember.WorkspaceId == workspace.Id &&
                            workspaceMember.UserId == projectMember.UserId &&
                            workspaceMember.Status == MembershipStatus.Active))
                    .Select(projectMember => projectMember.UserId)
                    .Distinct()
                    .Count(),
                dbContext.WorkspaceMembers
                    .Where(member =>
                        member.WorkspaceId == workspace.Id &&
                        member.Status == MembershipStatus.Active)
                    .OrderBy(member => member.JoinedAt)
                    .ThenBy(member => member.UserId)
                    .Select(member => new WorkspaceMemberPreviewResponse(
                        member.UserId,
                        dbContext.Users
                            .Where(user => user.Id == member.UserId)
                            .Select(user => user.DisplayName)
                            .FirstOrDefault() ?? "Member"))
                    .Take(3)
                    .ToList(),
                dbContext.TaskItems
                    .Where(task =>
                        task.WorkspaceId == workspace.Id &&
                        task.DeletedAt == null &&
                        task.ReviewerUserId == userId &&
                        task.ReviewStatus == TaskReviewStatus.Submitted &&
                        task.ReviewResolvedAt == null &&
                        dbContext.VisibleProjectsFor(userId).Any(project => project.Id == task.ProjectId))
                    .OrderByDescending(task => task.ReviewSubmittedAt ?? task.CreatedAt)
                    .ThenBy(task => task.Id)
                    .Select(task => new WorkspaceAttentionRow(
                        workspace.Id,
                        task.Id,
                        task.ProjectId,
                        task.Id,
                        WorkspaceNeedsAttentionKind.ReviewRequired,
                        task.ReviewSubmittedAt ?? task.CreatedAt))
                    .ToList(),
                dbContext.TaskExecutionRuns
                    .Where(run =>
                        run.WorkspaceId == workspace.Id &&
                        run.Status == TaskExecutionRunStatus.Failed &&
                        dbContext.VisibleProjectsFor(userId).Any(project => project.Id == run.ProjectId) &&
                        dbContext.TaskItems.Any(task =>
                            task.Id == run.TaskItemId &&
                            task.DeletedAt == null &&
                            task.Status != TaskItemStatus.Completed &&
                            task.Status != TaskItemStatus.Cancelled &&
                            (run.RequestedByUserId == userId || task.PrimaryAssigneeUserId == userId)) &&
                        !dbContext.TaskExecutionRuns.Any(laterRun =>
                            laterRun.TaskItemId == run.TaskItemId &&
                            laterRun.RequestedAtUtc > run.RequestedAtUtc))
                    .OrderByDescending(run => run.FinishedAtUtc ?? run.RequestedAtUtc)
                    .ThenBy(run => run.Id)
                    .Select(run => new WorkspaceAttentionRow(
                        workspace.Id,
                        run.Id,
                        run.ProjectId,
                        run.TaskItemId,
                        WorkspaceNeedsAttentionKind.ResearchFailed,
                        run.FinishedAtUtc ?? run.RequestedAtUtc))
                    .ToList()))
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return [];
        }

        var workspaceIds = rows.Select(row => row.Id).ToArray();
        var isSystemAdmin = rows[0].HasSystemAdminAccess;

        var announcementCounts = await dbContext
            .VisibleAnnouncementsFor(userId, isSystemAdmin, clock.UtcNow)
            .Where(announcement =>
                announcement.WorkspaceId.HasValue &&
                Enumerable.Contains(workspaceIds, announcement.WorkspaceId.Value) &&
                !dbContext.AnnouncementReads.Any(read =>
                    read.AnnouncementId == announcement.Id &&
                    read.UserId == userId))
            .GroupBy(announcement => announcement.WorkspaceId!.Value)
            .Select(group => new WorkspaceCount(group.Key, group.Count()))
            .ToDictionaryAsync(item => item.WorkspaceId, item => item.Count, cancellationToken);

        var readableConversationIds = messaging.QueryReadableConversationIds(userId)
            ?? throw new InvalidOperationException(
                "The Workspace dashboard projection requires the canonical PostgreSQL Conversation read scope.");
        var conversationCounts = await dbContext.Conversations
            .AsNoTracking()
            .Where(conversation =>
                Enumerable.Contains(workspaceIds, conversation.WorkspaceId) &&
                readableConversationIds.Contains(conversation.Id) &&
                dbContext.Messages.Any(message =>
                    message.ConversationId == conversation.Id &&
                    message.AuthorUserId != userId &&
                    message.DeletedAt == null &&
                    (!dbContext.ReadStates.Any(readState =>
                         readState.ConversationId == conversation.Id &&
                         readState.UserId == userId) ||
                     dbContext.ReadStates.Any(readState =>
                         readState.ConversationId == conversation.Id &&
                         readState.UserId == userId &&
                         message.CreatedAt > readState.LastReadAt))))
            .GroupBy(conversation => conversation.WorkspaceId)
            .Select(group => new WorkspaceCount(group.Key, group.Count()))
            .ToDictionaryAsync(item => item.WorkspaceId, item => item.Count, cancellationToken);

        var projectCounts = await dbContext.VisibleProjectsFor(userId)
            .Where(project =>
                Enumerable.Contains(workspaceIds, project.WorkspaceId) &&
                (project.Status == ProjectStatus.Active ||
                 project.Status == ProjectStatus.Review))
            .GroupBy(project => new { project.WorkspaceId, project.Status })
            .Select(group => new WorkspaceProjectCount(
                group.Key.WorkspaceId,
                group.Key.Status,
                group.Count()))
            .ToListAsync(cancellationToken);

        var runningProjectCounts = projectCounts
            .Where(item => item.Status == ProjectStatus.Active)
            .ToDictionary(item => item.WorkspaceId, item => item.Count);
        var needsReviewProjectCounts = projectCounts
            .Where(item => item.Status == ProjectStatus.Review)
            .ToDictionary(item => item.WorkspaceId, item => item.Count);

        return rows
            .Select(row =>
            {
                var needsAttentionItems = row.ReviewAttention
                    .Concat(row.FailedResearchAttention)
                    .GroupBy(item => new { item.Kind, item.TaskId })
                    .Select(items => items
                        .OrderByDescending(item => item.OccurredAt)
                        .ThenBy(item => item.Id)
                        .First())
                    .OrderByDescending(item => item.OccurredAt)
                    .ThenBy(item => item.Id)
                    .Select(item => new WorkspaceNeedsAttentionItemResponse(
                        item.Id,
                        item.Kind,
                        $"/projects/{item.ProjectId:D}/tasks/{item.TaskId:D}",
                        item.OccurredAt))
                    .ToList();

                return ToResponse(
                    row,
                    announcementCounts.GetValueOrDefault(row.Id),
                    conversationCounts.GetValueOrDefault(row.Id),
                    runningProjectCounts.GetValueOrDefault(row.Id),
                    needsReviewProjectCounts.GetValueOrDefault(row.Id),
                    needsAttentionItems);
            })
            .ToList();
    }

    private static WorkspaceDashboardListItemResponse ToResponse(
        WorkspaceDashboardRow row,
        int unreadAnnouncementCount,
        int unreadConversationCount,
        int runningProjectCount,
        int needsReviewProjectCount,
        IReadOnlyList<WorkspaceNeedsAttentionItemResponse> needsAttentionItems)
    {
        // Navigation is read-only. Quick-create mutation affordances are projected
        // separately and must match the ungrouped production create boundary used
        // by this Quick Create flow rather than a broader read/group authority.
        var hasCurrentWorkspaceReadAccess =
            row.CurrentUserRole.HasValue || row.HasSystemAdminAccess;
        var hasActiveWorkspaceMembership =
            row.HasActiveTenantMembership && row.CurrentUserRole.HasValue;
        var hasWorkspaceGovernanceAuthority =
            row.CurrentUserRole is WorkspaceRole.Owner or WorkspaceRole.Admin;
        var canManageSharing = row.HasSystemAdminAccess || hasWorkspaceGovernanceAuthority;
        var canInspectSharing = canManageSharing;
        var canCreateProject =
            hasActiveWorkspaceMembership &&
            (hasWorkspaceGovernanceAuthority || row.HasDelegatedProjectCreate);
        var canOpenProjectCreate =
            hasActiveWorkspaceMembership &&
            (hasWorkspaceGovernanceAuthority ||
             row.HasDelegatedProjectCreate ||
             row.HasManagedActiveGroup);
        var canAddFiles =
            row.HasSystemAdminAccess ||
            (row.HasActiveTenantMembership &&
             row.CurrentUserRole is WorkspaceRole.Owner or
                 WorkspaceRole.Admin or
                 WorkspaceRole.Adviser or
                 WorkspaceRole.Member);

        return new WorkspaceDashboardListItemResponse(
            row.Id,
            row.Name,
            row.Description,
            row.Icon,
            row.Status,
            row.CreatedAt,
            row.UpdatedAt ?? row.CreatedAt,
            row.CurrentUserRole,
            row.CurrentUserRole.HasValue
                ? WorkspaceDashboardAccessSource.WorkspaceMembership
                : WorkspaceDashboardAccessSource.SystemAdmin,
            hasCurrentWorkspaceReadAccess,
            hasCurrentWorkspaceReadAccess,
            hasCurrentWorkspaceReadAccess,
            canCreateProject,
            canAddFiles,
            unreadAnnouncementCount,
            unreadConversationCount,
            runningProjectCount + needsReviewProjectCount,
            runningProjectCount,
            needsReviewProjectCount,
            canOpenProjectCreate,
            row.ExternalShareCount > 0,
            canInspectSharing ? row.ExternalShareCount : null,
            canInspectSharing,
            canManageSharing,
            row.MemberPreview,
            needsAttentionItems.Count,
            needsAttentionItems);
    }

    private sealed record WorkspaceDashboardRow(
        Guid Id,
        string Name,
        string? Description,
        string? Icon,
        WorkspaceStatus Status,
        DateTimeOffset CreatedAt,
        DateTimeOffset? UpdatedAt,
        WorkspaceRole? CurrentUserRole,
        bool HasSystemAdminAccess,
        bool HasActiveTenantMembership,
        bool HasDelegatedProjectCreate,
        bool HasManagedActiveGroup,
        int ExternalShareCount,
        IReadOnlyList<WorkspaceMemberPreviewResponse> MemberPreview,
        IReadOnlyList<WorkspaceAttentionRow> ReviewAttention,
        IReadOnlyList<WorkspaceAttentionRow> FailedResearchAttention);

    private sealed record WorkspaceCount(Guid WorkspaceId, int Count);

    private sealed record WorkspaceProjectCount(
        Guid WorkspaceId,
        ProjectStatus Status,
        int Count);

    private sealed record WorkspaceAttentionRow(
        Guid WorkspaceId,
        Guid Id,
        Guid ProjectId,
        Guid TaskId,
        WorkspaceNeedsAttentionKind Kind,
        DateTimeOffset OccurredAt);
}
