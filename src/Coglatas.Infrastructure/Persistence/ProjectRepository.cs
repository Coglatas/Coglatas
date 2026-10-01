using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Infrastructure.Persistence;

public sealed class ProjectRepository(AppDbContext dbContext) : IProjectRepository
{
    public async Task<IReadOnlyList<Project>> ListVisibleAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return await dbContext.ListableProjectsFor(userId)
            .OrderBy(project => project.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Project>> ListVisibleInWorkspaceAsync(
        Guid userId,
        Guid workspaceId,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.ListableProjectsFor(userId)
            .Where(project => project.WorkspaceId == workspaceId)
            .OrderBy(project => project.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> ListActivatableProjectIdsAsync(
        Guid userId,
        IReadOnlyCollection<Guid> projectIds,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty || projectIds.Count == 0)
        {
            return [];
        }

        var candidateIds = projectIds.Distinct().ToArray();
        var activeSystemAdminIds = dbContext.Users
            .AsNoTracking()
            .Where(user =>
                user.Id == userId &&
                user.SystemRole == SystemRole.SystemAdmin &&
                user.Status == UserStatus.Active &&
                user.DeletedAt == null)
            .Select(user => user.Id);

        return await dbContext.Projects
            .AsNoTracking()
            .Where(project =>
                Enumerable.Contains(candidateIds, project.Id) &&
                project.DeletedAt == null &&
                project.VersionNo > 0 &&
                project.Visibility.HasValue &&
                project.ActivationState == ProjectActivationState.NeverActivated &&
                project.Status == ProjectStatus.Planning &&
                project.ActivatedAtUtc == null &&
                project.ActivationVersion == null &&
                dbContext.Tenants.Any(tenant =>
                    tenant.Id == project.TenantId &&
                    tenant.Status == TenantStatus.Active &&
                    tenant.DeletedAt == null) &&
                dbContext.Users.Any(user =>
                    user.Id == userId &&
                    user.Status == UserStatus.Active &&
                    user.DeletedAt == null) &&
                dbContext.TenantUsers.Any(member =>
                    member.TenantId == project.TenantId &&
                    member.UserId == userId &&
                    member.Status == TenantUserStatus.Active) &&
                dbContext.Workspaces.Any(workspace =>
                    workspace.Id == project.WorkspaceId &&
                    workspace.TenantId == project.TenantId &&
                    workspace.Status == WorkspaceStatus.Active &&
                    workspace.DeletedAt == null) &&
                dbContext.WorkspaceMembers.Any(member =>
                    member.TenantId == project.TenantId &&
                    member.WorkspaceId == project.WorkspaceId &&
                    member.UserId == userId &&
                    member.Status == MembershipStatus.Active) &&
                (activeSystemAdminIds.Contains(userId) ||
                 dbContext.WorkspaceMembers.Any(member =>
                     member.TenantId == project.TenantId &&
                     member.WorkspaceId == project.WorkspaceId &&
                     member.UserId == userId &&
                     member.Status == MembershipStatus.Active &&
                     (member.Role == WorkspaceRole.Owner ||
                      member.Role == WorkspaceRole.Admin ||
                      member.Role == WorkspaceRole.Adviser ||
                      member.Role == WorkspaceRole.Member))) &&
                (dbContext.ProjectMembers.Any(member =>
                     member.TenantId == project.TenantId &&
                     member.ProjectId == project.Id &&
                     member.UserId == userId &&
                     (member.Role == ProjectRole.Owner || member.Role == ProjectRole.Manager)) ||
                 (project.Visibility == ProjectVisibility.WorkspaceVisible &&
                  (activeSystemAdminIds.Contains(userId) ||
                   dbContext.WorkspaceMembers.Any(member =>
                       member.TenantId == project.TenantId &&
                       member.WorkspaceId == project.WorkspaceId &&
                       member.UserId == userId &&
                       member.Status == MembershipStatus.Active &&
                       (member.Role == WorkspaceRole.Owner || member.Role == WorkspaceRole.Admin)) ||
                   (project.GroupId.HasValue &&
                    dbContext.Groups.Any(group =>
                        group.Id == project.GroupId.Value &&
                        group.TenantId == project.TenantId &&
                        group.WorkspaceId == project.WorkspaceId &&
                        group.DeletedAt == null &&
                        dbContext.GroupMembers.Any(member =>
                            member.TenantId == project.TenantId &&
                            member.GroupId == group.Id &&
                            member.UserId == userId &&
                            (member.Role == GroupRole.Owner || member.Role == GroupRole.Admin))))))))
            .Select(project => project.Id)
            .ToListAsync(cancellationToken);
    }

    public Task<Project?> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        return dbContext.Projects.FirstOrDefaultAsync(project => project.Id == projectId, cancellationToken);
    }

    public Task<ProjectMember?> GetMemberAsync(Guid projectId, Guid userId, CancellationToken cancellationToken = default)
    {
        return dbContext.ProjectMembers
            .Include(member => member.User)
            .FirstOrDefaultAsync(member => member.ProjectId == projectId && member.UserId == userId, cancellationToken);
    }

    public async Task<IReadOnlyList<ProjectMember>> ListMembersAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        return await dbContext.ProjectMembers
            .AsNoTracking()
            .Include(member => member.User)
            .Where(member => member.ProjectId == projectId)
            .OrderBy(member => member.User!.DisplayName)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Milestone>> ListMilestonesAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        return await dbContext.Milestones
            .AsNoTracking()
            .Where(milestone => milestone.ProjectId == projectId)
            .OrderBy(milestone => milestone.SortOrder)
            .ThenBy(milestone => milestone.DueDate)
            .ToListAsync(cancellationToken);
    }

    public Task<Milestone?> GetMilestoneAsync(Guid milestoneId, CancellationToken cancellationToken = default)
    {
        return dbContext.Milestones.FirstOrDefaultAsync(milestone => milestone.Id == milestoneId, cancellationToken);
    }

    public async Task<IReadOnlyList<TaskItem>> ListTasksAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        return await dbContext.TaskItems
            .AsNoTracking()
            .Include(task => task.WorkflowStage)
            .Include(task => task.Collaborators)
            .Where(task => task.ProjectId == projectId)
            .OrderBy(task => task.SortKey)
            .ThenBy(task => task.SortOrder)
            .ThenBy(task => task.DueDate)
            .ThenBy(task => task.Title)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> ListTaskIdsWithArtifactsAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.Artifacts
            .AsNoTracking()
            .Where(artifact =>
                artifact.ProjectId == projectId &&
                artifact.TaskItemId.HasValue &&
                !artifact.DeletedAt.HasValue)
            .Select(artifact => artifact.TaskItemId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> ListCurrentReaderUserIdsAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.CurrentReaderUserIdsForProject(projectId)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TaskItem>> ListTasksBoundedAsync(Guid projectId, int take, CancellationToken cancellationToken = default)
    {
        return await dbContext.TaskItems
            .AsNoTracking()
            .Include(task => task.WorkflowStage)
            .Where(task =>
                task.ProjectId == projectId &&
                task.Kind == WorkItemKind.Task &&
                !task.DeletedAt.HasValue)
            .OrderBy(task => task.SortKey)
            .ThenBy(task => task.Id)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    public Task<int> CountGanttItemsBoundedAsync(
        Guid projectId,
        int take,
        CancellationToken cancellationToken = default)
    {
        if (take <= 0)
            return Task.FromResult(0);

        var taskIds = dbContext.TaskItems
            .Where(task =>
                task.ProjectId == projectId &&
                task.Kind == WorkItemKind.Task &&
                !task.DeletedAt.HasValue)
            .Select(task => task.Id);
        var milestoneIds = dbContext.Milestones
            .Where(milestone => milestone.ProjectId == projectId && !milestone.DeletedAt.HasValue)
            .Select(milestone => milestone.Id);
        return taskIds
            .Concat(milestoneIds)
            .Take(take)
            .CountAsync(cancellationToken);
    }

    public async Task<PagedResponse<TaskItem>> ListDirectSubtasksPageAsync(Guid projectId, Guid parentTaskItemId, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var query = dbContext.TaskItems
            .Include(task => task.WorkflowStage)
            .Include(task => task.PrimaryAssigneeUser)
            .Where(task => task.ProjectId == projectId && task.ParentTaskItemId == parentTaskItemId && !task.DeletedAt.HasValue);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderBy(task => task.SortKey).ThenBy(task => task.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new PagedResponse<TaskItem>(items, page, pageSize, total);
    }

    public Task<TaskItem?> GetTaskAsync(Guid taskItemId, CancellationToken cancellationToken = default)
    {
        return dbContext.TaskItems
            .Include(task => task.WorkflowStage)
            .Include(task => task.Collaborators)
            .FirstOrDefaultAsync(task => task.Id == taskItemId, cancellationToken);
    }

    public async Task<PagedResponse<TaskActivityLogReadModel>> ListTaskActivityLogsPageAsync(
        Guid projectId,
        Guid taskItemId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.ActivityLogs
            .AsNoTracking()
            .Where(log => log.ProjectId == projectId && log.TaskItemId == taskItemId);
        var totalCount = await query.CountAsync(cancellationToken);
        var skip = (int)Math.Min((long)Math.Max(0, page - 1) * pageSize, int.MaxValue);
        var items = await query
            .OrderByDescending(log => log.OccurredAt)
            .ThenByDescending(log => log.Id)
            .Skip(skip)
            .Take(pageSize)
            .Select(log => new TaskActivityLogReadModel(
                log.Id,
                log.ActivityType,
                log.Body,
                log.OccurredAt,
                log.AuthorUserId,
                log.AuthorUser!.DisplayName))
            .ToListAsync(cancellationToken);

        return new PagedResponse<TaskActivityLogReadModel>(items, page, pageSize, totalCount);
    }

    public Task<TaskWorkflowDefinition?> GetWorkflowDefinitionAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        dbContext.TaskWorkflowDefinitions.FirstOrDefaultAsync(definition => definition.ProjectId == projectId, cancellationToken);

    public Task<TaskWorkflowStage?> GetWorkflowStageAsync(Guid workflowStageId, CancellationToken cancellationToken = default) =>
        dbContext.TaskWorkflowStages.FirstOrDefaultAsync(stage => stage.Id == workflowStageId, cancellationToken);

    public async Task<IReadOnlyList<TaskWorkflowStage>> ListWorkflowStagesAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await dbContext.TaskWorkflowStages.AsNoTracking().Where(stage => stage.ProjectId == projectId).OrderBy(stage => stage.SortKey).ToListAsync(cancellationToken);
    public Task<TaskWorkflowStage?> GetInitialWorkflowStageAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        dbContext.TaskWorkflowStages
            .Where(stage =>
                stage.ProjectId == projectId &&
                (stage.IsInitialStage ||
                 stage.InternalCategory == TaskStageCategory.Backlog ||
                 stage.InternalCategory == TaskStageCategory.Todo))
            .OrderByDescending(stage => stage.IsInitialStage)
            .ThenBy(stage => stage.SortKey)
            .ThenBy(stage => stage.Id)
            .FirstOrDefaultAsync(cancellationToken);
    public Task<long?> GetMaximumTaskSortKeyAsync(Guid projectId, Guid workflowStageId, CancellationToken cancellationToken = default) =>
        dbContext.TaskItems
            .Where(task => task.ProjectId == projectId && task.WorkflowStageId == workflowStageId && !task.DeletedAt.HasValue)
            .MaxAsync(task => (long?)task.SortKey, cancellationToken);

    public async Task<IReadOnlyList<WorkItemCollaborator>> ListCollaboratorsAsync(Guid taskItemId, CancellationToken cancellationToken = default) =>
        await dbContext.WorkItemCollaborators.AsNoTracking().Include(item => item.User).Where(item => item.TaskItemId == taskItemId).OrderBy(item => item.User!.DisplayName).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<TaskAssignment>> ListAssignmentsAsync(Guid taskItemId, CancellationToken cancellationToken = default)
    {
        return await dbContext.TaskAssignments
            .AsNoTracking()
            .Include(assignment => assignment.User)
            .Where(assignment => assignment.TaskItemId == taskItemId)
            .OrderBy(assignment => assignment.Role)
            .ThenBy(assignment => assignment.User!.DisplayName)
            .ToListAsync(cancellationToken);
    }

    public Task<TaskAssignment?> GetAssignmentAsync(Guid assignmentId, CancellationToken cancellationToken = default)
    {
        return dbContext.TaskAssignments
            .Include(assignment => assignment.User)
            .Include(assignment => assignment.TaskItem)
            .FirstOrDefaultAsync(assignment => assignment.Id == assignmentId, cancellationToken);
    }

    public async Task<IReadOnlyList<TaskDependency>> ListDependenciesAsync(Guid taskItemId, CancellationToken cancellationToken = default)
    {
        return await dbContext.TaskDependencies
            .AsNoTracking()
            .Where(dependency => dependency.SuccessorTaskItemId == taskItemId || dependency.PredecessorTaskItemId == taskItemId)
            .OrderBy(dependency => dependency.CreatedAt)
            .ThenBy(dependency => dependency.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TaskDependency>> ListDependenciesBoundedAsync(
        Guid taskItemId,
        int take,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.TaskDependencies
            .AsNoTracking()
            .Where(dependency =>
                (dependency.SuccessorTaskItemId == taskItemId ||
                 dependency.PredecessorTaskItemId == taskItemId) &&
                dependency.PredecessorTaskItem != null &&
                dependency.PredecessorTaskItem.ProjectId == dependency.ProjectId &&
                dependency.PredecessorTaskItem.Kind == WorkItemKind.Task &&
                !dependency.PredecessorTaskItem.DeletedAt.HasValue &&
                dependency.SuccessorTaskItem != null &&
                dependency.SuccessorTaskItem.ProjectId == dependency.ProjectId &&
                dependency.SuccessorTaskItem.Kind == WorkItemKind.Task &&
                !dependency.SuccessorTaskItem.DeletedAt.HasValue)
            .OrderBy(dependency => dependency.PredecessorTaskItemId)
            .ThenBy(dependency => dependency.SuccessorTaskItemId)
            .ThenBy(dependency => dependency.Id)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TaskDependency>> ListProjectDependenciesAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        return await dbContext.TaskDependencies
            .AsNoTracking()
            .Where(dependency => dependency.ProjectId == projectId)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TaskDependency>> ListProjectDependenciesBoundedAsync(Guid projectId, int take, CancellationToken cancellationToken = default)
    {
        return await dbContext.TaskDependencies
            .AsNoTracking()
            .Where(dependency =>
                dependency.ProjectId == projectId &&
                dependency.PredecessorTaskItem != null &&
                dependency.PredecessorTaskItem.ProjectId == projectId &&
                dependency.PredecessorTaskItem.Kind == WorkItemKind.Task &&
                !dependency.PredecessorTaskItem.DeletedAt.HasValue &&
                dependency.SuccessorTaskItem != null &&
                dependency.SuccessorTaskItem.ProjectId == projectId &&
                dependency.SuccessorTaskItem.Kind == WorkItemKind.Task &&
                !dependency.SuccessorTaskItem.DeletedAt.HasValue)
            .OrderBy(dependency => dependency.PredecessorTaskItemId)
            .ThenBy(dependency => dependency.SuccessorTaskItemId)
            .ThenBy(dependency => dependency.Id)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    public Task<TaskDependency?> GetDependencyAsync(Guid dependencyId, CancellationToken cancellationToken = default)
    {
        return dbContext.TaskDependencies.FirstOrDefaultAsync(dependency => dependency.Id == dependencyId, cancellationToken);
    }

    public Task<bool> DependencyExistsAsync(Guid predecessorTaskId, Guid successorTaskId, CancellationToken cancellationToken = default)
    {
        return dbContext.TaskDependencies.AnyAsync(dependency =>
            dependency.PredecessorTaskItemId == predecessorTaskId &&
            dependency.SuccessorTaskItemId == successorTaskId,
            cancellationToken);
    }

    public async Task<IReadOnlyList<Comment>> ListCommentsAsync(CommentTargetType targetType, Guid targetId, CancellationToken cancellationToken = default)
    {
        return await dbContext.Comments
            .AsNoTracking()
            .Where(comment => comment.TargetType == targetType && comment.TargetId == targetId)
            .OrderBy(comment => comment.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public Task<Comment?> GetCommentAsync(Guid commentId, CancellationToken cancellationToken = default)
    {
        return dbContext.Comments.FirstOrDefaultAsync(comment => comment.Id == commentId, cancellationToken);
    }

    public async Task AddProjectAsync(Project project, CancellationToken cancellationToken = default)
    {
        await dbContext.Projects.AddAsync(project, cancellationToken);
    }

    public async Task AddMemberAsync(ProjectMember member, CancellationToken cancellationToken = default)
    {
        await dbContext.ProjectMembers.AddAsync(member, cancellationToken);
    }

    public async Task AddMilestoneAsync(Milestone milestone, CancellationToken cancellationToken = default)
    {
        await dbContext.Milestones.AddAsync(milestone, cancellationToken);
    }

    public async Task AddTaskAsync(TaskItem task, CancellationToken cancellationToken = default)
    {
        await dbContext.TaskItems.AddAsync(task, cancellationToken);
    }

    public Task<WorkItemWatchState?> GetWatchStateAsync(Guid taskItemId, Guid userId, CancellationToken cancellationToken = default) =>
        dbContext.WorkItemWatchStates.FirstOrDefaultAsync(x => x.TaskItemId == taskItemId && x.UserId == userId, cancellationToken);
    public async Task<IReadOnlyList<WorkItemWatchState>> ListWatchStatesAsync(Guid taskItemId, CancellationToken cancellationToken = default) =>
        await dbContext.WorkItemWatchStates.Where(x => x.TaskItemId == taskItemId).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<TaskChecklistItem>> ListChecklistAsync(Guid taskItemId, CancellationToken cancellationToken = default) =>
        await dbContext.TaskChecklistItems.Where(x => x.TaskItemId == taskItemId).OrderBy(x => x.SortKey).ThenBy(x => x.Id).ToListAsync(cancellationToken);
    public Task<TaskChecklistItem?> GetChecklistItemAsync(Guid itemId, CancellationToken cancellationToken = default) => dbContext.TaskChecklistItems.FirstOrDefaultAsync(x => x.Id == itemId, cancellationToken);
    public async Task<IReadOnlyList<TaskComment>> ListTaskCommentsAsync(Guid taskItemId, int skip, int take, CancellationToken cancellationToken = default) =>
        await dbContext.TaskComments.Include(x => x.AuthorUser).Where(x => x.TaskItemId == taskItemId).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Skip(skip).Take(take).ToListAsync(cancellationToken);
    public Task<int> CountTaskCommentsAsync(Guid taskItemId, CancellationToken cancellationToken = default) => dbContext.TaskComments.CountAsync(x => x.TaskItemId == taskItemId, cancellationToken);
    public Task<TaskComment?> GetTaskCommentAsync(Guid commentId, CancellationToken cancellationToken = default) => dbContext.TaskComments.Include(x => x.TaskItem).FirstOrDefaultAsync(x => x.Id == commentId, cancellationToken);
    public async Task<IReadOnlyList<ProjectTaskLabel>> ListTaskLabelsAsync(Guid projectId, bool includeArchived, CancellationToken cancellationToken = default) =>
        await dbContext.ProjectTaskLabels.Where(x => x.ProjectId == projectId && (includeArchived || !x.IsArchived)).OrderBy(x => x.SortKey).ThenBy(x => x.Name).ToListAsync(cancellationToken);
    public Task<ProjectTaskLabel?> GetTaskLabelAsync(Guid labelId, CancellationToken cancellationToken = default) => dbContext.ProjectTaskLabels.FirstOrDefaultAsync(x => x.Id == labelId, cancellationToken);
    public async Task<IReadOnlyList<WorkItemLabel>> ListWorkItemLabelsAsync(Guid taskItemId, CancellationToken cancellationToken = default) =>
        await dbContext.WorkItemLabels.Include(x => x.Label).Where(x => x.TaskItemId == taskItemId).ToListAsync(cancellationToken);
    public async Task<IReadOnlyList<User>> SearchMentionCandidatesAsync(Guid projectId, string query, int take, CancellationToken cancellationToken = default)
    {
        var project = await dbContext.Projects.AsNoTracking().FirstOrDefaultAsync(item => item.Id == projectId && !item.DeletedAt.HasValue && item.Status != ProjectStatus.Archived, cancellationToken);
        if (project is null) return [];
        var normalized = query.Trim().ToUpperInvariant();
        var candidates = dbContext.Users.AsNoTracking().Where(user => user.DeletedAt == null && user.Status == UserStatus.Active && user.DisplayName.ToUpper().Contains(normalized));
        candidates = project.GroupId.HasValue
            ? candidates.Where(user => dbContext.ProjectMembers.Any(member => member.ProjectId == projectId && member.UserId == user.Id) ||
                                       dbContext.GroupMembers.Any(member => member.GroupId == project.GroupId.Value && member.UserId == user.Id) ||
                                       dbContext.WorkspaceMembers.Any(member => member.WorkspaceId == project.WorkspaceId && member.UserId == user.Id && member.Status == MembershipStatus.Active && (member.Role == WorkspaceRole.Owner || member.Role == WorkspaceRole.Admin)))
            : candidates.Where(user => dbContext.ProjectMembers.Any(member => member.ProjectId == projectId && member.UserId == user.Id) ||
                                       dbContext.WorkspaceMembers.Any(member => member.WorkspaceId == project.WorkspaceId && member.UserId == user.Id && member.Status == MembershipStatus.Active));
        return await candidates.OrderBy(user => user.DisplayName.ToUpper()).ThenBy(user => user.Id).Take(take).ToListAsync(cancellationToken);
    }
    public async Task<IReadOnlyList<User>> GetEligibleMentionUsersAsync(Guid projectId, IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default)
    {
        if (userIds.Count == 0) return [];
        var project = await dbContext.Projects.AsNoTracking().FirstOrDefaultAsync(item => item.Id == projectId && !item.DeletedAt.HasValue && item.Status != ProjectStatus.Archived, cancellationToken);
        if (project is null) return [];
        var candidates = dbContext.Users.AsNoTracking().Where(user => userIds.Contains(user.Id) && user.DeletedAt == null && user.Status == UserStatus.Active);
        candidates = project.GroupId.HasValue
            ? candidates.Where(user => dbContext.ProjectMembers.Any(member => member.ProjectId == projectId && member.UserId == user.Id) || dbContext.GroupMembers.Any(member => member.GroupId == project.GroupId.Value && member.UserId == user.Id) || dbContext.WorkspaceMembers.Any(member => member.WorkspaceId == project.WorkspaceId && member.UserId == user.Id && member.Status == MembershipStatus.Active && (member.Role == WorkspaceRole.Owner || member.Role == WorkspaceRole.Admin)))
            : candidates.Where(user => dbContext.ProjectMembers.Any(member => member.ProjectId == projectId && member.UserId == user.Id) || dbContext.WorkspaceMembers.Any(member => member.WorkspaceId == project.WorkspaceId && member.UserId == user.Id && member.Status == MembershipStatus.Active));
        return await candidates.ToListAsync(cancellationToken);
    }
    public Task<WorkItemLabel?> GetWorkItemLabelAsync(Guid associationId, CancellationToken cancellationToken = default) => dbContext.WorkItemLabels.FirstOrDefaultAsync(x => x.Id == associationId, cancellationToken);

    public async Task AddCollaboratorAsync(WorkItemCollaborator collaborator, CancellationToken cancellationToken = default) =>
        await dbContext.WorkItemCollaborators.AddAsync(collaborator, cancellationToken);

    public async Task AddAssignmentAsync(TaskAssignment assignment, CancellationToken cancellationToken = default)
    {
        await dbContext.TaskAssignments.AddAsync(assignment, cancellationToken);
    }

    public async Task AddDependencyAsync(TaskDependency dependency, CancellationToken cancellationToken = default)
    {
        await dbContext.TaskDependencies.AddAsync(dependency, cancellationToken);
    }

    public async Task AddCommentAsync(Comment comment, CancellationToken cancellationToken = default)
    {
        await dbContext.Comments.AddAsync(comment, cancellationToken);
    }

    public void RemoveMember(ProjectMember member)
    {
        dbContext.ProjectMembers.Remove(member);
    }

    public void RemoveAssignment(TaskAssignment assignment)
    {
        dbContext.TaskAssignments.Remove(assignment);
    }

    public void RemoveDependency(TaskDependency dependency)
    {
        dbContext.TaskDependencies.Remove(dependency);
    }

    public async Task AddWatchStateAsync(WorkItemWatchState watchState, CancellationToken cancellationToken = default) =>
        await dbContext.WorkItemWatchStates.AddAsync(watchState, cancellationToken);
    public async Task AddChecklistItemAsync(TaskChecklistItem item, CancellationToken cancellationToken = default) => await dbContext.TaskChecklistItems.AddAsync(item, cancellationToken);
    public async Task AddTaskCommentAsync(TaskComment comment, CancellationToken cancellationToken = default) => await dbContext.TaskComments.AddAsync(comment, cancellationToken);
    public async Task AddTaskLabelAsync(ProjectTaskLabel label, CancellationToken cancellationToken = default) => await dbContext.ProjectTaskLabels.AddAsync(label, cancellationToken);
    public async Task AddWorkItemLabelAsync(WorkItemLabel association, CancellationToken cancellationToken = default) => await dbContext.WorkItemLabels.AddAsync(association, cancellationToken);

    public void RemoveCollaborator(WorkItemCollaborator collaborator)
    {
        // Command services may have loaded the same association while composing a
        // task projection. Prefer the request context's tracked instance over the
        // no-tracking query result used for relationship reads.
        var tracked = dbContext.WorkItemCollaborators.Local.FirstOrDefault(item => item.Id == collaborator.Id);
        dbContext.WorkItemCollaborators.Remove(tracked ?? collaborator);
    }
    public void RemoveChecklistItem(TaskChecklistItem item) => dbContext.TaskChecklistItems.Remove(item);
    public void RemoveWorkItemLabel(WorkItemLabel association) => dbContext.WorkItemLabels.Remove(association);
}
