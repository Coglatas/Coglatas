using Coglatas.Application.Common.Interfaces;
using Coglatas.Domain.Enums;

namespace Coglatas.Application.Common;

internal static class ScopedResourceValidation
{
    internal static bool HasExactlyOneScope(Guid? workspaceId, Guid? groupId, Guid? projectId)
    {
        var count = 0;
        if (workspaceId.HasValue) count++;
        if (groupId.HasValue) count++;
        if (projectId.HasValue) count++;
        return count == 1;
    }

    internal static async Task<Result> ValidateExistingScopeAsync(
        IWorkspaceRepository workspaces,
        IGroupRepository groups,
        IProjectRepository projects,
        Guid? workspaceId,
        Guid? groupId,
        Guid? projectId,
        CancellationToken cancellationToken)
    {
        if (workspaceId.HasValue)
        {
            var workspace = await workspaces.GetByIdAsync(workspaceId.Value, cancellationToken);
            if (workspace is null || workspace.DeletedAt.HasValue || workspace.Status != WorkspaceStatus.Active)
            {
                return Result.Failure("Workspace not found.");
            }
        }

        if (groupId.HasValue)
        {
            var group = await groups.GetByIdAsync(groupId.Value, cancellationToken);
            if (group is null || group.DeletedAt.HasValue || group.Status != GroupStatus.Active)
            {
                return Result.Failure("Group not found.");
            }
        }

        if (projectId.HasValue)
        {
            var project = await projects.GetProjectAsync(projectId.Value, cancellationToken);
            if (project is null || project.DeletedAt.HasValue || project.Status == ProjectStatus.Archived)
            {
                return Result.Failure("Project not found.");
            }
        }

        return Result.Success();
    }
}
