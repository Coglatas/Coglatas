using Coglatas.Application.Groups;
using Coglatas.Application.Workspaces;

namespace Coglatas.Application.Announcements;

internal static class AnnouncementScopeAuthorization
{
    internal static async Task<bool> CanCreateWorkspaceAsync(
        IWorkspaceAuthorizationService workspaceAuthorization,
        Guid userId,
        Guid workspaceId,
        Func<Guid, CancellationToken, Task<bool>> isElevatedUser,
        CancellationToken cancellationToken)
    {
        if (await workspaceAuthorization.CanManageWorkspace(userId, workspaceId, cancellationToken))
        {
            return true;
        }

        return await isElevatedUser(userId, cancellationToken) &&
            await workspaceAuthorization.CanViewWorkspace(userId, workspaceId, cancellationToken);
    }

    internal static async Task<bool> CanCreateGroupAsync(
        IGroupAuthorizationService groupAuthorization,
        Guid userId,
        Guid groupId,
        Func<Guid, CancellationToken, Task<bool>> isElevatedUser,
        CancellationToken cancellationToken)
    {
        if (await groupAuthorization.CanManageGroup(userId, groupId, cancellationToken))
        {
            return true;
        }

        return await isElevatedUser(userId, cancellationToken) &&
            await groupAuthorization.CanViewGroup(userId, groupId, cancellationToken);
    }
}
