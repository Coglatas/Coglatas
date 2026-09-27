using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Groups;
using Coglatas.Application.Projects;
using Coglatas.Application.Workspaces;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;

namespace Coglatas.Application.Events;

public sealed class EventAuthorizationService(
    IUserRepository users,
    IWorkspaceAuthorizationService workspaces,
    IGroupAuthorizationService groups,
    IProjectAuthorizationService projects) : IEventAuthorizationService
{
    private readonly ScopeAuthorizationEvaluator _scopeAuthorization =
        new(users, workspaces, groups, projects);

    public Task<bool> CanCreateEvent(Guid userId, Guid? workspaceId, Guid? groupId, Guid? projectId, CancellationToken cancellationToken = default)
    {
        return _scopeAuthorization.CanManageScopeAsync(userId, workspaceId, groupId, projectId, cancellationToken);
    }

    public async Task<bool> CanViewEvent(Guid userId, ActivityEvent activityEvent, CancellationToken cancellationToken = default)
    {
        if (activityEvent.Status == EventStatus.Draft)
        {
            return activityEvent.CreatedByUserId == userId ||
                await _scopeAuthorization.CanManageScopeAsync(
                    userId,
                    activityEvent.WorkspaceId,
                    activityEvent.GroupId,
                    activityEvent.ProjectId,
                    cancellationToken);
        }

        return await _scopeAuthorization.CanViewScopeAsync(
            userId,
            activityEvent.WorkspaceId,
            activityEvent.GroupId,
            activityEvent.ProjectId,
            cancellationToken);
    }

    public async Task<bool> CanManageEvent(Guid userId, ActivityEvent activityEvent, CancellationToken cancellationToken = default)
    {
        return activityEvent.CreatedByUserId == userId ||
            await _scopeAuthorization.CanManageScopeAsync(
                userId,
                activityEvent.WorkspaceId,
                activityEvent.GroupId,
                activityEvent.ProjectId,
                cancellationToken);
    }

    public Task<bool> CanManageAttendance(Guid userId, ActivityEvent activityEvent, CancellationToken cancellationToken = default)
    {
        return CanManageEvent(userId, activityEvent, cancellationToken);
    }

    public Task<bool> CanAccessScope(Guid userId, ActivityEvent activityEvent, CancellationToken cancellationToken = default)
    {
        return _scopeAuthorization.CanViewScopeAsync(
            userId,
            activityEvent.WorkspaceId,
            activityEvent.GroupId,
            activityEvent.ProjectId,
            cancellationToken);
    }
}
