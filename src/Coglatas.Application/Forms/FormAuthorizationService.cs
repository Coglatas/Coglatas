using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Groups;
using Coglatas.Application.Projects;
using Coglatas.Application.Workspaces;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;

namespace Coglatas.Application.Forms;

public sealed class FormAuthorizationService(
    IUserRepository users,
    IWorkspaceAuthorizationService workspaces,
    IGroupAuthorizationService groups,
    IProjectAuthorizationService projects) : IFormAuthorizationService
{
    private readonly ScopeAuthorizationEvaluator _scopeAuthorization =
        new(users, workspaces, groups, projects);

    public Task<bool> CanCreateForm(Guid userId, Guid? workspaceId, Guid? groupId, Guid? projectId, CancellationToken cancellationToken = default)
    {
        return _scopeAuthorization.CanManageScopeAsync(userId, workspaceId, groupId, projectId, cancellationToken);
    }

    public async Task<bool> CanViewForm(Guid userId, InternalForm form, CancellationToken cancellationToken = default)
    {
        if (form.DeletedAt.HasValue || form.Status == FormStatus.Archived)
        {
            return await CanManageForm(userId, form, cancellationToken);
        }

        if (form.Status == FormStatus.Draft)
        {
            return form.CreatedByUserId == userId ||
                await _scopeAuthorization.CanManageScopeAsync(
                    userId,
                    form.WorkspaceId,
                    form.GroupId,
                    form.ProjectId,
                    cancellationToken);
        }

        return await _scopeAuthorization.CanViewScopeAsync(
            userId,
            form.WorkspaceId,
            form.GroupId,
            form.ProjectId,
            cancellationToken);
    }

    public Task<bool> CanManageForm(Guid userId, InternalForm form, CancellationToken cancellationToken = default)
    {
        return form.CreatedByUserId == userId
            ? Task.FromResult(true)
            : _scopeAuthorization.CanManageScopeAsync(
                userId,
                form.WorkspaceId,
                form.GroupId,
                form.ProjectId,
                cancellationToken);
    }

    public Task<bool> CanAccessScope(Guid userId, InternalForm form, CancellationToken cancellationToken = default)
    {
        return _scopeAuthorization.CanViewScopeAsync(
            userId,
            form.WorkspaceId,
            form.GroupId,
            form.ProjectId,
            cancellationToken);
    }
}
