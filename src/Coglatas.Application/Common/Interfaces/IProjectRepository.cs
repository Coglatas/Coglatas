using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;

namespace Coglatas.Application.Common.Interfaces;

public sealed record TaskActivityLogReadModel(
    Guid Id,
    ActivityLogType ActivityType,
    string Body,
    DateTimeOffset OccurredAt,
    Guid AuthorUserId,
    string AuthorDisplayName);

public interface IProjectRepository
{
    Task<IReadOnlyList<Project>> ListVisibleAsync(Guid userId, CancellationToken cancellationToken = default);
    async Task<IReadOnlyList<Project>> ListVisibleInWorkspaceAsync(
        Guid userId,
        Guid workspaceId,
        CancellationToken cancellationToken = default) =>
        (await ListVisibleAsync(userId, cancellationToken))
            .Where(project => project.WorkspaceId == workspaceId)
            .ToArray();
    Task<IReadOnlyList<Guid>> ListActivatableProjectIdsAsync(
        Guid userId,
        IReadOnlyCollection<Guid> projectIds,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Guid>>([]);
    Task<Project?> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<ProjectMember?> GetMemberAsync(Guid projectId, Guid userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectMember>> ListMembersAsync(Guid projectId, CancellationToken cancellationToken = default);
    async Task<IReadOnlyList<Guid>> ListCurrentReaderUserIdsAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        (await ListMembersAsync(projectId, cancellationToken))
            .Select(member => member.UserId)
            .Distinct()
            .ToArray();
    Task<IReadOnlyList<Milestone>> ListMilestonesAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<Milestone?> GetMilestoneAsync(Guid milestoneId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TaskItem>> ListTasksAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Guid>> ListTaskIdsWithArtifactsAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Guid>>([]);
    async Task<IReadOnlyList<TaskItem>> ListTasksBoundedAsync(Guid projectId, int take, CancellationToken cancellationToken = default) =>
        (await ListTasksAsync(projectId, cancellationToken))
            .Where(task => task.Kind == WorkItemKind.Task && !task.DeletedAt.HasValue)
            .Take(take)
            .ToList();
    async Task<int> CountGanttItemsBoundedAsync(Guid projectId, int take, CancellationToken cancellationToken = default)
    {
        if (take <= 0)
            return 0;
        var tasks = await ListTasksBoundedAsync(projectId, take, cancellationToken);
        if (tasks.Count >= take)
            return take;
        var milestones = await ListMilestonesAsync(projectId, cancellationToken);
        return Math.Min(
            take,
            tasks.Count + milestones.Count(milestone => !milestone.DeletedAt.HasValue));
    }
    Task<PagedResponse<TaskItem>> ListDirectSubtasksPageAsync(Guid projectId, Guid parentTaskItemId, int page, int pageSize, CancellationToken cancellationToken = default) => Task.FromResult(new PagedResponse<TaskItem>([], page, pageSize, 0));
    Task<TaskItem?> GetTaskAsync(Guid taskItemId, CancellationToken cancellationToken = default);
    Task<PagedResponse<TaskActivityLogReadModel>> ListTaskActivityLogsPageAsync(
        Guid projectId,
        Guid taskItemId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new PagedResponse<TaskActivityLogReadModel>([], page, pageSize, 0));
    Task<TaskWorkflowDefinition?> GetWorkflowDefinitionAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<TaskWorkflowDefinition?>(null);
    Task<TaskWorkflowStage?> GetWorkflowStageAsync(Guid workflowStageId, CancellationToken cancellationToken = default) => Task.FromResult<TaskWorkflowStage?>(null);
    Task<IReadOnlyList<TaskWorkflowStage>> ListWorkflowStagesAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskWorkflowStage>>([]);
    async Task<TaskWorkflowStage?> GetInitialWorkflowStageAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        (await ListWorkflowStagesAsync(projectId, cancellationToken))
            .Where(stage => stage.IsInitialStage || stage.InternalCategory is TaskStageCategory.Backlog or TaskStageCategory.Todo)
            .OrderByDescending(stage => stage.IsInitialStage)
            .ThenBy(stage => stage.SortKey)
            .ThenBy(stage => stage.Id)
            .FirstOrDefault();
    async Task<long?> GetMaximumTaskSortKeyAsync(Guid projectId, Guid workflowStageId, CancellationToken cancellationToken = default) =>
        (await ListTasksAsync(projectId, cancellationToken))
            .Where(task => task.WorkflowStageId == workflowStageId && !task.DeletedAt.HasValue)
            .Select(task => (long?)task.SortKey)
            .Max();
    Task<IReadOnlyList<WorkItemCollaborator>> ListCollaboratorsAsync(Guid taskItemId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<WorkItemCollaborator>>([]);
    Task<IReadOnlyList<TaskAssignment>> ListAssignmentsAsync(Guid taskItemId, CancellationToken cancellationToken = default);
    Task<TaskAssignment?> GetAssignmentAsync(Guid assignmentId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TaskDependency>> ListDependenciesAsync(Guid taskItemId, CancellationToken cancellationToken = default);
    async Task<IReadOnlyList<TaskDependency>> ListDependenciesBoundedAsync(Guid taskItemId, int take, CancellationToken cancellationToken = default) =>
        (await ListDependenciesAsync(taskItemId, cancellationToken)).Take(take).ToList();
    Task<IReadOnlyList<TaskDependency>> ListProjectDependenciesAsync(Guid projectId, CancellationToken cancellationToken = default);
    async Task<IReadOnlyList<TaskDependency>> ListProjectDependenciesBoundedAsync(Guid projectId, int take, CancellationToken cancellationToken = default) =>
        (await ListProjectDependenciesAsync(projectId, cancellationToken)).Take(take).ToList();
    Task<TaskDependency?> GetDependencyAsync(Guid dependencyId, CancellationToken cancellationToken = default);
    Task<bool> DependencyExistsAsync(Guid predecessorTaskId, Guid successorTaskId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Comment>> ListCommentsAsync(CommentTargetType targetType, Guid targetId, CancellationToken cancellationToken = default);
    Task<Comment?> GetCommentAsync(Guid commentId, CancellationToken cancellationToken = default);
    Task<WorkItemWatchState?> GetWatchStateAsync(Guid taskItemId, Guid userId, CancellationToken cancellationToken = default) => Task.FromResult<WorkItemWatchState?>(null);
    Task<IReadOnlyList<WorkItemWatchState>> ListWatchStatesAsync(Guid taskItemId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<WorkItemWatchState>>([]);
    Task<IReadOnlyList<TaskChecklistItem>> ListChecklistAsync(Guid taskItemId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskChecklistItem>>([]);
    Task<TaskChecklistItem?> GetChecklistItemAsync(Guid itemId, CancellationToken cancellationToken = default) => Task.FromResult<TaskChecklistItem?>(null);
    Task<IReadOnlyList<TaskComment>> ListTaskCommentsAsync(Guid taskItemId, int skip, int take, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskComment>>([]);
    Task<int> CountTaskCommentsAsync(Guid taskItemId, CancellationToken cancellationToken = default) => Task.FromResult(0);
    Task<TaskComment?> GetTaskCommentAsync(Guid commentId, CancellationToken cancellationToken = default) => Task.FromResult<TaskComment?>(null);
    Task<IReadOnlyList<ProjectTaskLabel>> ListTaskLabelsAsync(Guid projectId, bool includeArchived, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ProjectTaskLabel>>([]);
    Task<ProjectTaskLabel?> GetTaskLabelAsync(Guid labelId, CancellationToken cancellationToken = default) => Task.FromResult<ProjectTaskLabel?>(null);
    Task<IReadOnlyList<WorkItemLabel>> ListWorkItemLabelsAsync(Guid taskItemId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<WorkItemLabel>>([]);
    Task<IReadOnlyList<User>> SearchMentionCandidatesAsync(Guid projectId, string query, int take, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<User>>([]);
    Task<IReadOnlyList<User>> GetEligibleMentionUsersAsync(Guid projectId, IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<User>>([]);
    Task<WorkItemLabel?> GetWorkItemLabelAsync(Guid associationId, CancellationToken cancellationToken = default) => Task.FromResult<WorkItemLabel?>(null);
    Task AddProjectAsync(Project project, CancellationToken cancellationToken = default);
    Task AddMemberAsync(ProjectMember member, CancellationToken cancellationToken = default);
    Task AddMilestoneAsync(Milestone milestone, CancellationToken cancellationToken = default);
    Task AddTaskAsync(TaskItem task, CancellationToken cancellationToken = default);
    Task AddCollaboratorAsync(WorkItemCollaborator collaborator, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task AddAssignmentAsync(TaskAssignment assignment, CancellationToken cancellationToken = default);
    Task AddDependencyAsync(TaskDependency dependency, CancellationToken cancellationToken = default);
    Task AddCommentAsync(Comment comment, CancellationToken cancellationToken = default);
    Task AddWatchStateAsync(WorkItemWatchState watchState, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task AddChecklistItemAsync(TaskChecklistItem item, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task AddTaskCommentAsync(TaskComment comment, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task AddTaskLabelAsync(ProjectTaskLabel label, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task AddWorkItemLabelAsync(WorkItemLabel association, CancellationToken cancellationToken = default) => Task.CompletedTask;
    void RemoveMember(ProjectMember member);
    void RemoveAssignment(TaskAssignment assignment);
    void RemoveDependency(TaskDependency dependency);
    void RemoveCollaborator(WorkItemCollaborator collaborator) { }
    void RemoveChecklistItem(TaskChecklistItem item) { }
    void RemoveWorkItemLabel(WorkItemLabel association) { }
}
