using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Groups;
using Coglatas.Application.Projects;
using Coglatas.Application.Workspaces;
using Coglatas.Domain.Enums;

namespace Coglatas.Application.Common;

internal sealed class ScopeAuthorizationEvaluator(
    IUserRepository users,
    IWorkspaceAuthorizationService workspaces,
    IGroupAuthorizationService groups,
    IProjectAuthorizationService projects)
{
    internal async Task<bool> CanViewScopeAsync(
        Guid userId,
        Guid? workspaceId,
        Guid? groupId,
        Guid? projectId,
        CancellationToken cancellationToken)
    {
        if (workspaceId.HasValue)
        {
            return await workspaces.CanViewWorkspace(userId, workspaceId.Value, cancellationToken);
        }

        if (groupId.HasValue)
        {
            return await groups.CanViewGroup(userId, groupId.Value, cancellationToken);
        }

        if (projectId.HasValue)
        {
            return await projects.CanViewProject(userId, projectId.Value, cancellationToken);
        }

        return false;
    }

    internal async Task<bool> CanManageScopeAsync(
        Guid userId,
        Guid? workspaceId,
        Guid? groupId,
        Guid? projectId,
        CancellationToken cancellationToken)
    {
        if (workspaceId.HasValue)
        {
            return await workspaces.CanManageWorkspace(userId, workspaceId.Value, cancellationToken) ||
                await CanElevatedUserManageVisibleScopeAsync(
                    userId,
                    () => workspaces.CanViewWorkspace(userId, workspaceId.Value, cancellationToken),
                    cancellationToken);
        }

        if (groupId.HasValue)
        {
            return await groups.CanManageGroup(userId, groupId.Value, cancellationToken) ||
                await CanElevatedUserManageVisibleScopeAsync(
                    userId,
                    () => groups.CanViewGroup(userId, groupId.Value, cancellationToken),
                    cancellationToken);
        }

        if (projectId.HasValue)
        {
            return await projects.CanManageProject(userId, projectId.Value, cancellationToken) ||
                await CanElevatedUserManageVisibleScopeAsync(
                    userId,
                    () => projects.CanViewProject(userId, projectId.Value, cancellationToken),
                    cancellationToken);
        }

        return false;
    }

    private async Task<bool> CanElevatedUserManageVisibleScopeAsync(
        Guid userId,
        Func<Task<bool>> canViewScope,
        CancellationToken cancellationToken)
    {
        var user = await users.GetByIdAsync(userId, cancellationToken);
        return user is
        {
            Status: UserStatus.Active,
            SystemRole: SystemRole.Teacher or SystemRole.Admin or SystemRole.SystemAdmin
        } && await canViewScope();
    }
}
