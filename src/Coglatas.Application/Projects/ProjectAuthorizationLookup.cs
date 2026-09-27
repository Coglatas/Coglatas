using Coglatas.Application.Common.Interfaces;
using Coglatas.Domain.Entities;

namespace Coglatas.Application.Projects;

internal sealed class ProjectAuthorizationLookup(
    IProjectRepository projects,
    IProjectAuthorizationService projectAuthorization,
    ICurrentUser currentUser)
{
    internal async Task<Project?> VisibleProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        if (!TryActor(out var actor) ||
            projectId == Guid.Empty ||
            !await projectAuthorization.CanViewProject(actor, projectId, cancellationToken))
        {
            return null;
        }

        var project = await projects.GetProjectAsync(projectId, cancellationToken);
        return project is { DeletedAt: null } ? project : null;
    }

    internal async Task<Project?> ManagedProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        if (!TryActor(out var actor) ||
            projectId == Guid.Empty ||
            !await projectAuthorization.CanManageProject(actor, projectId, cancellationToken))
        {
            return null;
        }

        var project = await projects.GetProjectAsync(projectId, cancellationToken);
        return project is { DeletedAt: null } ? project : null;
    }

    internal async Task<TaskItem?> VisibleTaskAsync(
        Guid taskItemId,
        CancellationToken cancellationToken)
    {
        if (!TryActor(out var actor) || taskItemId == Guid.Empty)
        {
            return null;
        }

        var task = await projects.GetTaskAsync(taskItemId, cancellationToken);
        return task is { DeletedAt: null } &&
            await projectAuthorization.CanViewProject(actor, task.ProjectId, cancellationToken)
                ? task
                : null;
    }

    internal async Task<TaskItem?> ManagedTaskAsync(
        Guid taskItemId,
        CancellationToken cancellationToken)
    {
        if (!TryActor(out var actor) || taskItemId == Guid.Empty)
        {
            return null;
        }

        var task = await projects.GetTaskAsync(taskItemId, cancellationToken);
        return task is { DeletedAt: null } &&
            await projectAuthorization.CanManageProject(actor, task.ProjectId, cancellationToken)
                ? task
                : null;
    }

    internal Task<bool> CanManageAsync(Guid projectId, CancellationToken cancellationToken) =>
        TryActor(out var actor)
            ? projectAuthorization.CanManageProject(actor, projectId, cancellationToken)
            : Task.FromResult(false);

    internal bool TryActor(out Guid actor)
    {
        actor = currentUser.UserId ?? Guid.Empty;
        return currentUser.IsAuthenticated && actor != Guid.Empty;
    }

    internal Guid Actor() => currentUser.UserId ?? Guid.Empty;
}
