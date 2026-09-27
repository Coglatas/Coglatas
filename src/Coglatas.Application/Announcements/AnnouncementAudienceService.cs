using Coglatas.Application.Channels;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Groups;
using Coglatas.Application.Workspaces;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;

namespace Coglatas.Application.Announcements;

public sealed class AnnouncementAudienceService(
    IAnnouncementRepository announcements,
    IWorkspaceRepository workspaces,
    IGroupRepository groups,
    IChannelRepository channels,
    IUserRepository users,
    ITenantRepository tenants,
    IWorkspaceAuthorizationService workspaceAuthorization,
    IGroupAuthorizationService groupAuthorization,
    IChannelAuthorizationService channelAuthorization,
    IAnnouncementScheduleTimeZoneResolver scheduleTimeZones,
    ICurrentUser currentUser,
    ICurrentTenant currentTenant) : IAnnouncementAudienceService
{
    public async Task<Result<IReadOnlyList<AnnouncementAudienceOptionResponse>>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!TryCurrentActor(out var userId, out var contextError))
        {
            return Result<IReadOnlyList<AnnouncementAudienceOptionResponse>>.Failure(contextError!);
        }

        var isSystemAdmin = await IsSystemAdminAsync(userId, cancellationToken);
        if (!isSystemAdmin && !await HasActiveTenantMembershipAsync(userId, cancellationToken))
        {
            return Result<IReadOnlyList<AnnouncementAudienceOptionResponse>>.Success([]);
        }

        var options = new List<AnnouncementAudienceOptionResponse>();

        if (isSystemAdmin)
        {
            options.Add(await CreateOptionAsync(
                "global",
                "global",
                null,
                null,
                null,
                "テナント全体",
                cancellationToken));
        }

        var visibleWorkspaces = await workspaces.ListForUserAsync(userId, isSystemAdmin, cancellationToken);
        foreach (var workspace in visibleWorkspaces)
        {
            if (workspace.DeletedAt.HasValue || workspace.Status != WorkspaceStatus.Active)
            {
                continue;
            }

            if (await AnnouncementScopeAuthorization.CanCreateWorkspaceAsync(workspaceAuthorization, userId, workspace.Id, IsTeacherAsync, cancellationToken))
            {
                options.Add(await CreateOptionAsync(
                    $"workspace:{workspace.Id:D}",
                    "workspace",
                    workspace.Id,
                    null,
                    null,
                    workspace.Name,
                    cancellationToken));
            }

            var workspaceGroups = await groups.ListByWorkspaceAsync(workspace.Id, cancellationToken);
            foreach (var group in workspaceGroups)
            {
                if (group.DeletedAt.HasValue || group.Status != GroupStatus.Active || group.WorkspaceId != workspace.Id)
                {
                    continue;
                }

                if (await AnnouncementScopeAuthorization.CanCreateGroupAsync(groupAuthorization, userId, group.Id, IsTeacherAsync, cancellationToken))
                {
                    options.Add(await CreateOptionAsync(
                        $"group:{group.Id:D}",
                        "group",
                        workspace.Id,
                        group.Id,
                        null,
                        $"{workspace.Name} / {group.Name}",
                        cancellationToken));
                }

                var groupChannels = await channels.ListByGroupAsync(group.Id, cancellationToken);
                foreach (var channel in groupChannels)
                {
                    if (channel.DeletedAt.HasValue ||
                        channel.Status != ChannelStatus.Active ||
                        channel.GroupId != group.Id ||
                        channel.WorkspaceId != workspace.Id ||
                        !await channelAuthorization.CanManageChannel(userId, channel.Id, cancellationToken))
                    {
                        continue;
                    }

                    options.Add(await CreateOptionAsync(
                        $"channel:{channel.Id:D}",
                        "channel",
                        workspace.Id,
                        group.Id,
                        channel.Id,
                        $"{workspace.Name} / {group.Name} / #{channel.Name}",
                        cancellationToken));
                }
            }
        }

        return Result<IReadOnlyList<AnnouncementAudienceOptionResponse>>.Success(options);
    }

    public async Task<Result<bool>> IsAuthorizedAsync(
        Guid? workspaceId,
        Guid? groupId,
        Guid? channelId,
        CancellationToken cancellationToken = default)
    {
        if (!TryCurrentActor(out var userId, out var contextError))
        {
            return Result<bool>.Failure(contextError!);
        }

        return await IsAuthorizedForActorAsync(
            userId,
            workspaceId,
            groupId,
            channelId,
            cancellationToken);
    }

    public async Task<Result<bool>> IsAuthorizedForActorAsync(
        Guid actorUserId,
        Guid? workspaceId,
        Guid? groupId,
        Guid? channelId,
        CancellationToken cancellationToken = default)
    {
        if (actorUserId == Guid.Empty)
        {
            return Result<bool>.Success(false);
        }

        if (!currentTenant.IsAvailable || currentTenant.IsPlatformScope)
        {
            return Result<bool>.Failure("A tenant context is required to resolve announcement audiences.");
        }

        var isSystemAdmin = await IsSystemAdminAsync(actorUserId, cancellationToken);
        if (!isSystemAdmin && !await HasActiveTenantMembershipAsync(actorUserId, cancellationToken))
        {
            return Result<bool>.Success(false);
        }

        if (channelId.HasValue)
        {
            if (!groupId.HasValue || !workspaceId.HasValue)
            {
                return Result<bool>.Success(false);
            }

            var channel = await channels.GetByIdAsync(channelId.Value, cancellationToken);
            if (channel is null ||
                channel.DeletedAt.HasValue ||
                channel.Status != ChannelStatus.Active ||
                channel.GroupId != groupId.Value ||
                channel.WorkspaceId != workspaceId.Value)
            {
                return Result<bool>.Success(false);
            }

            if (!await ParentScopeIsActiveAsync(workspaceId.Value, groupId.Value, cancellationToken))
            {
                return Result<bool>.Success(false);
            }

            return Result<bool>.Success(await channelAuthorization.CanManageChannel(actorUserId, channelId.Value, cancellationToken));
        }

        if (groupId.HasValue)
        {
            if (!workspaceId.HasValue)
            {
                return Result<bool>.Success(false);
            }

            var group = await groups.GetByIdAsync(groupId.Value, cancellationToken);
            if (group is null ||
                group.DeletedAt.HasValue ||
                group.Status != GroupStatus.Active ||
                group.WorkspaceId != workspaceId.Value)
            {
                return Result<bool>.Success(false);
            }

            var workspace = await workspaces.GetByIdAsync(workspaceId.Value, cancellationToken);
            if (workspace is null || workspace.DeletedAt.HasValue || workspace.Status != WorkspaceStatus.Active)
            {
                return Result<bool>.Success(false);
            }

            return Result<bool>.Success(await AnnouncementScopeAuthorization.CanCreateGroupAsync(
                groupAuthorization,
                actorUserId,
                groupId.Value,
                IsTeacherAsync,
                cancellationToken));
        }

        if (workspaceId.HasValue)
        {
            var workspace = await workspaces.GetByIdAsync(workspaceId.Value, cancellationToken);
            if (workspace is null || workspace.DeletedAt.HasValue || workspace.Status != WorkspaceStatus.Active)
            {
                return Result<bool>.Success(false);
            }

            return Result<bool>.Success(await AnnouncementScopeAuthorization.CanCreateWorkspaceAsync(
                workspaceAuthorization,
                actorUserId,
                workspaceId.Value,
                IsTeacherAsync,
                cancellationToken));
        }

        return Result<bool>.Success(isSystemAdmin);
    }

    private bool TryCurrentActor(out Guid userId, out string? error)
    {
        userId = Guid.Empty;
        error = null;

        if (!currentUser.IsAuthenticated || !currentUser.UserId.HasValue)
        {
            error = "Authentication is required.";
            return false;
        }

        if (!currentTenant.IsAvailable || currentTenant.IsPlatformScope)
        {
            error = "A tenant context is required to resolve announcement audiences.";
            return false;
        }

        userId = currentUser.UserId.Value;
        return true;
    }

    private async Task<bool> HasActiveTenantMembershipAsync(Guid userId, CancellationToken cancellationToken)
    {
        var tenantMembership = await tenants.GetTenantUserAsync(currentTenant.TenantId, userId, cancellationToken);
        return tenantMembership is { Status: TenantUserStatus.Active };
    }

    private async Task<bool> ParentScopeIsActiveAsync(Guid workspaceId, Guid groupId, CancellationToken cancellationToken)
    {
        var workspace = await workspaces.GetByIdAsync(workspaceId, cancellationToken);
        if (workspace is null || workspace.DeletedAt.HasValue || workspace.Status != WorkspaceStatus.Active)
        {
            return false;
        }

        var group = await groups.GetByIdAsync(groupId, cancellationToken);
        return group is
        {
            DeletedAt: null,
            Status: GroupStatus.Active
        } && group.WorkspaceId == workspaceId;
    }

    private async Task<AnnouncementAudienceOptionResponse> CreateOptionAsync(
        string key,
        string scopeType,
        Guid? workspaceId,
        Guid? groupId,
        Guid? channelId,
        string displayName,
        CancellationToken cancellationToken)
    {
        var prototype = new Announcement
        {
            TenantId = currentTenant.TenantId,
            WorkspaceId = workspaceId,
            GroupId = groupId,
            ChannelId = channelId
        };
        var recipients = await announcements.ListTargetUsersAsync(prototype, cancellationToken);
        return new AnnouncementAudienceOptionResponse(
            key,
            scopeType,
            workspaceId,
            groupId,
            channelId,
            displayName,
            recipients.Count,
            (await scheduleTimeZones.ResolveAsync(
                currentTenant.TenantId,
                workspaceId,
                cancellationToken)).Id);
    }

    private async Task<bool> IsSystemAdminAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await users.GetByIdAsync(userId, cancellationToken);
        return user is { Status: UserStatus.Active, DeletedAt: null, SystemRole: SystemRole.SystemAdmin };
    }

    private async Task<bool> IsTeacherAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await users.GetByIdAsync(userId, cancellationToken);
        return user is
        {
            Status: UserStatus.Active,
            DeletedAt: null,
            SystemRole: SystemRole.Teacher or SystemRole.Admin or SystemRole.SystemAdmin
        };
    }
}
