using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Planning;
using Coglatas.Application.Realtime;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;

namespace Coglatas.Application.Projects;

public sealed class ProjectService(
    IProjectRepository projects,
    IWorkspaceRepository workspaces,
    IGroupRepository groups,
    IUserRepository users,
    IProjectAuthorizationService projectAuthorization,
    ITaskAuthorizationService taskAuthorization,
    ICommentAuthorizationService commentAuthorization,
    ICurrentUser currentUser,
    IClock clock,
    IAuditLogger auditLogger,
    IBusinessInvalidationPublisher invalidations,
    IAuthorizationStateChangePublisher authorizationChanges,
    IUnitOfWork unitOfWork,
    ITaskCommandUnitOfWork taskUnitOfWork,
    IFeatureFlagService? featureFlags = null,
    ITaskWorkspaceTimeZoneResolver? timeZones = null,
    ITaskNotificationProducer? taskNotifications = null,
    ITaskRelationshipTargetPolicy? relationshipTargets = null) : IProjectService
{
    private const int MaximumGanttItems = 500;
    private const int MaximumGanttDependencies = 2_000;

    private Task<bool>? _taskDomainV1Enabled;
    public async Task<Result<PagedResponse<ProjectResponse>>> ListAsync(ProjectListQuery query, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId))
        {
            return Result<PagedResponse<ProjectResponse>>.Failure("Authentication is required.");
        }

        var items = query.WorkspaceId.HasValue
            ? await projects.ListVisibleInWorkspaceAsync(userId, query.WorkspaceId.Value, cancellationToken)
            : await projects.ListVisibleAsync(userId, cancellationToken);
        var filtered = items
            .Where(project => !project.DeletedAt.HasValue)
            .Where(project => !query.WorkspaceId.HasValue || project.WorkspaceId == query.WorkspaceId.Value)
            .Where(project => query.Archived ? project.Status == ProjectStatus.Archived : project.Status is not ProjectStatus.Archived and not ProjectStatus.Deleted)
            .Where(project => !query.Status.HasValue || project.Status == query.Status.Value)
            .Where(project => MatchesSearch(project.Name, project.Description, query.Search))
            .ToList();

        var pageItems = filtered
            .Skip((query.SafePage - 1) * query.SafePageSize)
            .Take(query.SafePageSize)
            .ToList();
        var activationCandidateIds = pageItems
            .Where(IsCanonicalActivationCandidate)
            .Select(project => project.Id)
            .ToArray();
        HashSet<Guid> activatableProjectIds = activationCandidateIds.Length == 0
            ? []
            : (await projects.ListActivatableProjectIdsAsync(
                userId,
                activationCandidateIds,
                cancellationToken)).ToHashSet();

        var responses = new List<ProjectResponse>(pageItems.Count);
        foreach (var project in pageItems)
        {
            responses.Add(await ToProjectAsync(
                project,
                userId,
                activatableProjectIds.Contains(project.Id),
                cancellationToken));
        }

        return Result<PagedResponse<ProjectResponse>>.Success(new PagedResponse<ProjectResponse>(
            responses,
            query.SafePage,
            query.SafePageSize,
            filtered.Count));
    }

    public async Task<Result<ProjectResponse>> CreateAsync(CreateProjectRequest request, CancellationToken cancellationToken = default)
    {
        // The deprecated body-scoped command cannot safely approximate the
        // canonical Workspace-root authority, Visibility, or idempotency
        // contract.  Leave it explicitly fail closed until those decisions
        // and dependencies are resolved.
        await Task.CompletedTask;
        return Result<ProjectResponse>.Failure(new ApplicationErrorDetail(
            "DependencyUnavailable",
            "Project creation is temporarily unavailable."));
    }

    public async Task<Result<ProjectResponse>> GetAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId) || !await projectAuthorization.CanViewProject(userId, projectId, cancellationToken))
        {
            return ProjectNotFound<ProjectResponse>();
        }

        var project = await projects.GetProjectAsync(projectId, cancellationToken);
        return project is null || project.DeletedAt.HasValue
            ? ProjectNotFound<ProjectResponse>()
            : Result<ProjectResponse>.Success(await ToProjectAsync(
                project,
                userId,
                await CanActivateProjectAsync(project, userId, cancellationToken),
                cancellationToken));
    }

    private async Task<bool> CanReceiveTerminalLifecycleErrorAsync(
        Guid userId,
        Project project,
        CancellationToken cancellationToken)
    {
        // This grants no operational mutation authority. It only preserves the
        // typed terminal-state error for a current explicit Project manager.
        var workspaceMember = await workspaces.GetMemberAsync(project.WorkspaceId, userId, cancellationToken);
        if (workspaceMember is not { Status: MembershipStatus.Active })
        {
            return false;
        }

        var projectMember = await projects.GetMemberAsync(project.Id, userId, cancellationToken);
        return projectMember?.Role is ProjectRole.Owner or ProjectRole.Manager;
    }

    private static Result<T> ProjectNotFound<T>() =>
        Result<T>.Failure(new ApplicationErrorDetail(
            "NotFound",
            "The requested resource was not found."));

    public async Task<Result<ProjectResponse>> UpdateAsync(Guid projectId, UpdateProjectRequest request, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId) || !await projectAuthorization.CanManageProject(userId, projectId, cancellationToken))
        {
            return Result<ProjectResponse>.Failure("You are not allowed to manage this project.");
        }

        var project = await projects.GetProjectAsync(projectId, cancellationToken);
        if (project is null || project.DeletedAt.HasValue)
        {
            return Result<ProjectResponse>.Failure("Project not found.");
        }

        if (project.Status is ProjectStatus.Archived or ProjectStatus.Deleted)
        {
            return Result<ProjectResponse>.Failure(new ApplicationErrorDetail(
                "InvalidStateTransition",
                "Archived or deleted Projects are read-only.",
                Target: "project"));
        }

        var previousStatus = project.Status;
        var nextStatus = request.Status ?? project.Status;
        var isSuspendedResume = previousStatus == ProjectStatus.Suspended &&
                                nextStatus is not ProjectStatus.Suspended and not ProjectStatus.Archived;
        if (isSuspendedResume)
        {
            if (!CanRecoverProjectTo(project, project.SuspendedFromStatus, nextStatus))
            {
                return Result<ProjectResponse>.Failure(new ApplicationErrorDetail(
                    "InvalidStateTransition",
                    "The requested Project lifecycle transition is not available.",
                    Target: "body.status"));
            }
        }
        else
        {
            if (previousStatus != nextStatus &&
                nextStatus == ProjectStatus.Active &&
                previousStatus != ProjectStatus.Review)
            {
                return Result<ProjectResponse>.Failure(new ApplicationErrorDetail(
                    "InvalidStateTransition",
                    "The requested Project lifecycle transition is not available.",
                    Target: "body.status"));
            }
            if (!IsValidProjectStatusTransition(previousStatus, nextStatus))
            {
                return Result<ProjectResponse>.Failure(new ApplicationErrorDetail(
                    "InvalidStateTransition",
                    "The requested Project lifecycle transition is not available.",
                    Target: "body.status"));
            }
        }

        var startDate = request.StartDate ?? project.StartDate;
        var endDate = request.EndDate ?? project.DueDate;
        if (HasInvalidDateRange(startDate, endDate))
        {
            return Result<ProjectResponse>.Failure("Project end date cannot be before the start date.");
        }

        string? normalizedTitle = null;
        if (request.Title is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Title))
            {
                return Result<ProjectResponse>.Failure("Project title is required.");
            }

            normalizedTitle = request.Title.Trim();
        }
        var affectedReaders = RemovesCurrentReadAccess(previousStatus, nextStatus)
            ? await projects.ListCurrentReaderUserIdsAsync(project.Id, cancellationToken)
            : [];

        if (normalizedTitle is not null)
        {
            project.Name = normalizedTitle;
            project.Slug = SlugGenerator.FromName(project.Name);
        }

        project.Description = request.Description?.Trim() ?? project.Description;
        project.Status = nextStatus;
        project.StartDate = request.StartDate ?? project.StartDate;
        project.DueDate = request.EndDate ?? project.DueDate;
        await AuditAsync(userId, project.Status == previousStatus ? "ProjectUpdated" : "ProjectStatusChanged", "Project", project.Id, cancellationToken);
        await invalidations.ProjectChangedAsync(project, userId, isSuspendedResume ? "resumed" : "updated", cancellationToken);
        await PublishProjectAccessInvalidationsAsync(
            project,
            affectedReaders,
            nextStatus == ProjectStatus.Archived ? "archived" : "suspended",
            cancellationToken);
        if (!await SaveProjectMutationAsync(cancellationToken))
            return ProjectConflict<ProjectResponse>();
        return Result<ProjectResponse>.Success(await ToProjectAsync(
            project,
            userId,
            await CanActivateProjectAsync(project, userId, cancellationToken),
            cancellationToken));
    }

    public async Task<Result> ArchiveAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId))
        {
            return Result.Failure("You are not allowed to manage this project.");
        }

        var canManage = await projectAuthorization.CanManageProject(userId, projectId, cancellationToken);
        var project = await projects.GetProjectAsync(projectId, cancellationToken);
        if (project is null)
        {
            return Result.Failure("Project not found.");
        }

        var isDeletedTerminalState = project.DeletedAt.HasValue || project.Status == ProjectStatus.Deleted;
        if (!canManage &&
            !(isDeletedTerminalState && await CanReceiveTerminalLifecycleErrorAsync(userId, project, cancellationToken)))
        {
            return Result.Failure("You are not allowed to manage this project.");
        }

        if (project.DeletedAt.HasValue || project.Status is ProjectStatus.Archived or ProjectStatus.Deleted)
        {
            return Result.Failure(new ApplicationErrorDetail(
                "InvalidStateTransition",
                "The requested Project lifecycle transition is not available.",
                Target: "project"));
        }

        var affectedReaders = await projects.ListCurrentReaderUserIdsAsync(project.Id, cancellationToken);
        project.Status = ProjectStatus.Archived;
        await AuditAsync(userId, "ProjectArchived", "Project", project.Id, cancellationToken);
        await invalidations.ProjectChangedAsync(project, userId, "archived", cancellationToken);
        await PublishProjectAccessInvalidationsAsync(project, affectedReaders, "archived", cancellationToken);
        if (!await SaveProjectMutationAsync(cancellationToken))
            return ProjectConflict();
        return Result.Success();
    }

    public async Task<Result> RestoreAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId))
        {
            return Result.Failure("You are not allowed to manage this project.");
        }

        var canManage = await projectAuthorization.CanManageProject(userId, projectId, cancellationToken);
        var project = await projects.GetProjectAsync(projectId, cancellationToken);
        if (project is null)
        {
            return Result.Failure("Project not found.");
        }

        var isDeletedTerminalState = project.DeletedAt.HasValue || project.Status == ProjectStatus.Deleted;
        if (!canManage &&
            !(isDeletedTerminalState && await CanReceiveTerminalLifecycleErrorAsync(userId, project, cancellationToken)))
        {
            return Result.Failure("You are not allowed to manage this project.");
        }

        if (project.DeletedAt.HasValue ||
            project.Status != ProjectStatus.Archived ||
            !project.ArchivedFromStatus.HasValue ||
            !CanRecoverProjectTo(project, project.ArchivedFromStatus, project.ArchivedFromStatus.Value))
        {
            var message = project.Status is ProjectStatus.Archived or ProjectStatus.Deleted
                ? "The Project cannot be restored because its prior lifecycle state is unavailable or inconsistent."
                : "The requested Project lifecycle transition is not available.";
            return Result.Failure(new ApplicationErrorDetail(
                "InvalidStateTransition",
                message,
                Target: "project"));
        }

        var restoreStatus = project.ArchivedFromStatus.Value;
        var affectedReaders = await projects.ListCurrentReaderUserIdsAsync(project.Id, cancellationToken);
        project.Status = restoreStatus;
        await AuditAsync(userId, "ProjectRestored", "Project", project.Id, cancellationToken);
        await invalidations.ProjectChangedAsync(project, userId, "restored", cancellationToken);
        await PublishProjectAccessInvalidationsAsync(project, affectedReaders, "restored", cancellationToken);
        if (!await SaveProjectMutationAsync(cancellationToken))
            return ProjectConflict();
        return Result.Success();
    }

    public async Task<Result<IReadOnlyList<ProjectMemberResponse>>> ListMembersAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId) || !await projectAuthorization.CanViewProject(userId, projectId, cancellationToken))
        {
            return Result<IReadOnlyList<ProjectMemberResponse>>.Failure("Project not found.");
        }

        var members = await projects.ListMembersAsync(projectId, cancellationToken);
        return Result<IReadOnlyList<ProjectMemberResponse>>.Success(members.Select(ToProjectMember).ToList());
    }

    public async Task<Result<ProjectMemberResponse>> AddMemberAsync(Guid projectId, AddProjectMemberRequest request, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var actorUserId) || !await projectAuthorization.CanManageProject(actorUserId, projectId, cancellationToken))
        {
            return Result<ProjectMemberResponse>.Failure("You are not allowed to manage project members.");
        }

        var project = await projects.GetProjectAsync(projectId, cancellationToken);
        var user = await users.GetByIdAsync(request.UserId, cancellationToken);
        if (project is null || user is null)
        {
            return Result<ProjectMemberResponse>.Failure("Project or user not found.");
        }

        var access = await ValidateParentAccessAsync(project, request.UserId, cancellationToken);
        if (!access.IsSuccess)
        {
            return Result<ProjectMemberResponse>.Failure(access.Error!);
        }

        if (await projects.GetMemberAsync(projectId, request.UserId, cancellationToken) is not null)
        {
            return Result<ProjectMemberResponse>.Failure("User is already a project member.");
        }

        var member = new ProjectMember
        {
            ProjectId = projectId,
            UserId = request.UserId,
            User = user,
            Role = request.Role,
            JoinedAt = clock.UtcNow
        };

        await projects.AddMemberAsync(member, cancellationToken);
        await AuditAsync(actorUserId, "ProjectMemberAdded", "Project", projectId, cancellationToken);
        await invalidations.ProjectChangedAsync(project, actorUserId, "memberChanged", cancellationToken);
        await authorizationChanges.PublishAsync(project.TenantId, request.UserId, "project", project.Id, "membershipChanged", cancellationToken);
        if (!await SaveProjectMutationAsync(cancellationToken))
            return ProjectConflict<ProjectMemberResponse>();
        return Result<ProjectMemberResponse>.Success(ToProjectMember(member));
    }

    public async Task<Result<ProjectMemberResponse>> UpdateMemberAsync(Guid projectId, Guid userId, UpdateProjectMemberRequest request, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var actorUserId) || !await projectAuthorization.CanManageProject(actorUserId, projectId, cancellationToken))
        {
            return Result<ProjectMemberResponse>.Failure("You are not allowed to manage project members.");
        }

        var member = await projects.GetMemberAsync(projectId, userId, cancellationToken);
        if (member is null)
        {
            return Result<ProjectMemberResponse>.Failure("Project member not found.");
        }

        member.Role = request.Role;
        await AuditAsync(actorUserId, "ProjectMemberUpdated", "Project", projectId, cancellationToken);
        var project = await projects.GetProjectAsync(projectId, cancellationToken);
        if (project is not null)
        {
            await invalidations.ProjectChangedAsync(project, actorUserId, "memberChanged", cancellationToken);
            await authorizationChanges.PublishAsync(project.TenantId, userId, "project", project.Id, "membershipChanged", cancellationToken);
        }
        if (!await SaveProjectMutationAsync(cancellationToken))
            return ProjectConflict<ProjectMemberResponse>();
        return Result<ProjectMemberResponse>.Success(ToProjectMember(member));
    }

    public async Task<Result> RemoveMemberAsync(Guid projectId, Guid userId, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var actorUserId) || !await projectAuthorization.CanManageProject(actorUserId, projectId, cancellationToken))
        {
            return Result.Failure("You are not allowed to manage project members.");
        }

        var member = await projects.GetMemberAsync(projectId, userId, cancellationToken);
        if (member is null)
        {
            return Result.Failure("Project member not found.");
        }

        projects.RemoveMember(member);
        await AuditAsync(actorUserId, "ProjectMemberRemoved", "Project", projectId, cancellationToken);
        var project = await projects.GetProjectAsync(projectId, cancellationToken);
        if (project is not null)
        {
            await invalidations.ProjectChangedAsync(project, actorUserId, "memberChanged", cancellationToken);
            await authorizationChanges.PublishAsync(project.TenantId, userId, "project", project.Id, "revoked", cancellationToken);
        }
        if (!await SaveProjectMutationAsync(cancellationToken))
            return ProjectConflict();
        return Result.Success();
    }

    public async Task<Result<PagedResponse<MilestoneResponse>>> ListMilestonesAsync(Guid projectId, ProjectChildListQuery query, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId) || !await projectAuthorization.CanViewProject(userId, projectId, cancellationToken))
        {
            return Result<PagedResponse<MilestoneResponse>>.Failure("Project not found.");
        }

        var milestones = await projects.ListMilestonesAsync(projectId, cancellationToken);
        var filtered = milestones
            .Where(m => !m.DeletedAt.HasValue)
            .Where(m => MatchesSearch(m.Name, m.Description, query.Search))
            .Select(ToMilestone)
            .ToList();
        return Result<PagedResponse<MilestoneResponse>>.Success(ToPagedResponse(filtered, query.SafePage, query.SafePageSize));
    }

    public async Task<Result<MilestoneResponse>> GetMilestoneAsync(Guid milestoneId, CancellationToken cancellationToken = default)
    {
        var milestone = await projects.GetMilestoneAsync(milestoneId, cancellationToken);
        if (milestone is null || milestone.DeletedAt.HasValue || !CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId) ||
            !await projectAuthorization.CanViewProject(userId, milestone.ProjectId, cancellationToken))
        {
            return Result<MilestoneResponse>.Failure("Milestone not found.");
        }

        return Result<MilestoneResponse>.Success(ToMilestone(milestone));
    }

    public async Task<Result<MilestoneResponse>> CreateMilestoneAsync(Guid projectId, CreateMilestoneRequest request, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId) || !await projectAuthorization.CanManageProject(userId, projectId, cancellationToken))
        {
            return Result<MilestoneResponse>.Failure("You are not allowed to manage this project.");
        }

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return Result<MilestoneResponse>.Failure("Milestone title is required.");
        }

        if (!request.DueDate.HasValue)
        {
            return Result<MilestoneResponse>.Failure("MILESTONE_DATE_REQUIRED|Milestone date is required.");
        }

        var project = await projects.GetProjectAsync(projectId, cancellationToken);
        if (project is null ||
            project.DeletedAt.HasValue ||
            project.Status is ProjectStatus.Archived or ProjectStatus.Deleted)
        {
            return Result<MilestoneResponse>.Failure("Project not found.");
        }

        var milestone = new Milestone
        {
            ProjectId = projectId,
            Name = request.Title.Trim(),
            Description = request.Description?.Trim(),
            DueDate = request.DueDate,
            SortOrder = request.DisplayOrder,
            VersionNo = 1
        };

        await projects.AddMilestoneAsync(milestone, cancellationToken);
        await AuditAsync(userId, "MilestoneCreated", "Milestone", milestone.Id, cancellationToken);
        await invalidations.ProjectChangedAsync(project, userId, "milestoneChanged", cancellationToken);
        if (!await SaveMilestoneMutationAsync(cancellationToken))
            return Result<MilestoneResponse>.Failure("MILESTONE_CONFLICT|Milestone has changed. Refetch and retry.");
        return Result<MilestoneResponse>.Success(ToMilestone(milestone));
    }

    public async Task<Result<MilestoneResponse>> UpdateMilestoneAsync(Guid milestoneId, UpdateMilestoneRequest request, CancellationToken cancellationToken = default)
    {
        var milestone = await projects.GetMilestoneAsync(milestoneId, cancellationToken);
        if (milestone is null || milestone.DeletedAt.HasValue)
        {
            return Result<MilestoneResponse>.Failure("Milestone not found.");
        }

        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId) || !await projectAuthorization.CanManageProject(userId, milestone.ProjectId, cancellationToken))
        {
            return Result<MilestoneResponse>.Failure("You are not allowed to manage this project.");
        }
        var project = await projects.GetProjectAsync(milestone.ProjectId, cancellationToken);
        if (project is null ||
            project.DeletedAt.HasValue ||
            project.Status is ProjectStatus.Archived or ProjectStatus.Deleted)
        {
            return Result<MilestoneResponse>.Failure("Milestone not found.");
        }
        if (request.ExpectedVersion <= 0)
        {
            return Result<MilestoneResponse>.Failure(new ApplicationErrorDetail(
                "MILESTONE_INVALID_EXPECTED_VERSION",
                "Expected version must be a positive integer."));
        }
        if (milestone.VersionNo != request.ExpectedVersion)
        {
            return Result<MilestoneResponse>.Failure(new ApplicationErrorDetail(
                "MILESTONE_STALE_VERSION",
                "Milestone has changed. Refetch and retry."));
        }

        if (request.Title is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Title))
            {
                return Result<MilestoneResponse>.Failure("Milestone title is required.");
            }

            milestone.Name = request.Title.Trim();
        }

        if (request.Status.HasValue && request.Status.Value is not (MilestoneStatus.NotStarted or MilestoneStatus.InProgress or MilestoneStatus.Completed))
        {
            return Result<MilestoneResponse>.Failure("Milestone status must be NotStarted, InProgress, or Completed.");
        }

        var finalDueDate = request.DueDate ?? milestone.DueDate;
        var finalStatus = request.Status ?? milestone.Status;
        if (!finalDueDate.HasValue &&
            finalStatus is MilestoneStatus.InProgress or MilestoneStatus.Completed)
        {
            return Result<MilestoneResponse>.Failure(new ApplicationErrorDetail(
                "MILESTONE_DATE_REQUIRED",
                "Milestone date is required before progress can become active or completed."));
        }

        milestone.Description = request.Description?.Trim() ?? milestone.Description;
        milestone.DueDate = finalDueDate;
        milestone.Status = finalStatus;
        milestone.SortOrder = request.DisplayOrder ?? milestone.SortOrder;
        milestone.VersionNo++;
        await AuditAsync(userId, "MilestoneUpdated", "Milestone", milestone.Id, cancellationToken);
        await invalidations.ProjectChangedAsync(project, userId, "milestoneChanged", cancellationToken);
        if (!await SaveMilestoneMutationAsync(cancellationToken))
        {
            return Result<MilestoneResponse>.Failure(new ApplicationErrorDetail(
                "MILESTONE_CONFLICT",
                "Milestone has changed. Refetch and retry."));
        }
        return Result<MilestoneResponse>.Success(ToMilestone(milestone));
    }

    public async Task<Result> DeleteMilestoneAsync(Guid milestoneId, CancellationToken cancellationToken = default)
    {
        var milestone = await projects.GetMilestoneAsync(milestoneId, cancellationToken);
        if (milestone is null)
        {
            return Result.Failure("Milestone not found.");
        }

        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId) || !await projectAuthorization.CanManageProject(userId, milestone.ProjectId, cancellationToken))
        {
            return Result.Failure("You are not allowed to manage this project.");
        }
        var project = await projects.GetProjectAsync(milestone.ProjectId, cancellationToken);
        if (project is null ||
            project.DeletedAt.HasValue ||
            project.Status is ProjectStatus.Archived or ProjectStatus.Deleted)
        {
            return Result.Failure("Milestone not found.");
        }

        milestone.MarkDeleted(clock.UtcNow);
        milestone.VersionNo++;
        await AuditAsync(userId, "MilestoneDeleted", "Milestone", milestone.Id, cancellationToken);
        await invalidations.ProjectChangedAsync(project, userId, "milestoneChanged", cancellationToken);
        if (!await SaveMilestoneMutationAsync(cancellationToken))
            return Result.Failure("MILESTONE_CONFLICT|Milestone has changed. Refetch and retry.");
        return Result.Success();
    }

    public async Task<Result<PagedResponse<TaskItemResponse>>> ListTasksAsync(Guid projectId, TaskListQuery query, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId) || !await projectAuthorization.CanViewProject(userId, projectId, cancellationToken))
        {
            return Result<PagedResponse<TaskItemResponse>>.Failure("Project not found.");
        }

        var tasks = await projects.ListTasksAsync(projectId, cancellationToken);
        // Do not run concurrent operations over the scoped DbContext.  This
        // compatibility filter is intentionally sequential until it is moved
        // to the repository's SQL projection.
        HashSet<Guid>? taskIdsForAssignee = null;
        if (query.AssignedUserId.HasValue)
        {
            taskIdsForAssignee = [];
            foreach (var task in tasks)
                if ((await projects.ListAssignmentsAsync(task.Id, cancellationToken)).Any(assignment => assignment.UserId == query.AssignedUserId.Value))
                    taskIdsForAssignee.Add(task.Id);
        }

        var filtered = tasks
            .Where(task => !task.DeletedAt.HasValue)
            .Where(task => MatchesSearch(task.Title, task.Description, query.Search))
            .Where(task => !query.Status.HasValue || task.Status == query.Status.Value)
            .Where(task => !query.Priority.HasValue || task.Priority == query.Priority.Value)
            .Where(task => !query.MilestoneId.HasValue || task.MilestoneId == query.MilestoneId.Value)
            .Where(task => taskIdsForAssignee is null || taskIdsForAssignee.Contains(task.Id))
            .ToList();
        // Build each direct-child collection once.  Passing the complete Project
        // set to every row makes parent derivation quadratic for large lists.
        var childrenByParent = tasks
            .Where(task => task.ParentTaskItemId.HasValue)
            .GroupBy(task => task.ParentTaskItemId!.Value)
            .ToDictionary(group => group.Key, group => (IEnumerable<TaskItem>)group.ToArray());
        var derivedValues = tasks.ToDictionary(
            task => task.Id,
            task => ParentTaskDerivedValuesCalculator.Calculate(task, childrenByParent.GetValueOrDefault(task.Id, []), CategoryOf));
        var workspaceId = tasks.FirstOrDefault()?.WorkspaceId;
        var timeZone = workspaceId.HasValue && timeZones is not null
            ? await timeZones.ResolveAsync(tasks[0].TenantId, workspaceId.Value, cancellationToken)
            : TimeZoneInfo.Utc;
        var taskIdsWithArtifacts = (await projects.ListTaskIdsWithArtifactsAsync(projectId, cancellationToken)).ToHashSet();
        var responses = new List<TaskItemResponse>(filtered.Count);
        foreach (var task in filtered)
            responses.Add(await ToTaskAsync(
                task,
                userId,
                cancellationToken,
                derivedValues[task.Id],
                timeZone,
                taskIdsWithArtifacts.Contains(task.Id)));
        return Result<PagedResponse<TaskItemResponse>>.Success(ToPagedResponse(responses, query.SafePage, query.SafePageSize));
    }

    public async Task<Result<TaskItemResponse>> CreateTaskAsync(Guid projectId, CreateTaskItemRequest request, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId) || !await taskAuthorization.CanCreateTask(userId, projectId, cancellationToken))
        {
            return Result<TaskItemResponse>.Failure("You are not allowed to create tasks.");
        }

        var validation = await ValidateTaskRequestAsync(projectId, request.MilestoneId, request.Title, request.StartDate, request.DueDate, null, cancellationToken);
        if (!validation.IsSuccess)
        {
            return Result<TaskItemResponse>.Failure(validation.Error!);
        }

        var briefValidation = TaskBriefText.Validate(request.Goal, request.Deliverable, request.Constraints);
        if (briefValidation is not null)
            return Result<TaskItemResponse>.Failure(briefValidation);

        var project = await projects.GetProjectAsync(projectId, cancellationToken);
        if (project is null ||
            project.DeletedAt.HasValue ||
            project.Status is ProjectStatus.Archived or ProjectStatus.Deleted)
        {
            return Result<TaskItemResponse>.Failure("Project not found.");
        }

        var task = new TaskItem
        {
            ProjectId = projectId,
            WorkspaceId = project.WorkspaceId,
            MilestoneId = request.MilestoneId,
            Title = request.Title.Trim(),
            Description = request.Description?.Trim(),
            BriefGoal = TaskBriefText.Normalize(request.Goal),
            BriefDeliverable = TaskBriefText.Normalize(request.Deliverable),
            BriefConstraints = TaskBriefText.Normalize(request.Constraints),
            Priority = request.Priority,
            StartDate = request.StartDate,
            DueDate = request.DueDate,
            PlannedStartDate = request.StartDate,
            PlannedEndDate = request.DueDate,
            CreatedByUserId = userId
        };

        var placement = await TaskInitialPlacement.ApplyAsync(projects, task, cancellationToken);
        if (!placement.IsSuccess)
        {
            return Result<TaskItemResponse>.Failure(placement.Error!);
        }
        await projects.AddTaskAsync(task, cancellationToken);
        await projects.AddWatchStateAsync(TaskWatchStateInitializer.ForCreator(task, userId, clock.UtcNow), cancellationToken);
        await AuditAsync(userId, "TaskCreated", "TaskItem", task.Id, cancellationToken);
        await invalidations.TaskChangedAsync(task, userId, "created", cancellationToken: cancellationToken);
        if (await taskUnitOfWork.SaveTaskCommandAsync(cancellationToken) != TaskCommandSaveResult.Saved)
            return TaskConflict<TaskItemResponse>();
        return Result<TaskItemResponse>.Success(await ToTaskAsync(task, userId, cancellationToken));
    }

    public async Task<Result<TaskItemResponse>> GetTaskAsync(Guid taskItemId, CancellationToken cancellationToken = default)
    {
        var task = await projects.GetTaskAsync(taskItemId, cancellationToken);
        if (task is null || task.DeletedAt.HasValue || !CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId) ||
            !await projectAuthorization.CanViewProject(userId, task.ProjectId, cancellationToken))
        {
            return Result<TaskItemResponse>.Failure("Task not found.");
        }

        return Result<TaskItemResponse>.Success(await ToTaskAsync(task, userId, cancellationToken));
    }

    public async Task<Result<TaskItemResponse>> UpdateTaskAsync(Guid taskItemId, UpdateTaskItemRequest request, CancellationToken cancellationToken = default)
    {
        var task = await projects.GetTaskAsync(taskItemId, cancellationToken);
        if (task is null || task.DeletedAt.HasValue)
        {
            return Result<TaskItemResponse>.Failure("Task not found.");
        }

        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId) || !await taskAuthorization.CanUpdateTask(userId, taskItemId, cancellationToken))
        {
            return Result<TaskItemResponse>.Failure("You are not allowed to update this task.");
        }

        var startDate = request.StartDate ?? task.StartDate;
        var dueDate = request.DueDate ?? task.DueDate;
        var progress = request.ProgressPercent ?? task.ProgressPercent;
        var validation = await ValidateTaskRequestAsync(task.ProjectId, request.MilestoneId ?? task.MilestoneId, request.Title ?? task.Title, startDate, dueDate, progress, cancellationToken);
        if (!validation.IsSuccess)
        {
            return Result<TaskItemResponse>.Failure(validation.Error!);
        }

        if (request.Title is not null)
        {
            task.Title = request.Title.Trim();
        }

        var previousStatus = task.Status;
        task.MilestoneId = request.MilestoneId ?? task.MilestoneId;
        task.Description = request.Description?.Trim() ?? task.Description;
        task.Status = request.Status ?? task.Status;
        task.Priority = request.Priority ?? task.Priority;
        task.StartDate = request.StartDate ?? task.StartDate;
        task.DueDate = request.DueDate ?? task.DueDate;
        task.ProgressPercent = task.Status == TaskItemStatus.Completed ? 100 : progress;

        task.VersionNo++;
        await AuditAsync(userId, "TaskUpdated", "TaskItem", task.Id, cancellationToken);
        var changedFields = ChangedTaskFields(request, previousStatus);
        var affectedUsers = (await projects.ListAssignmentsAsync(task.Id, cancellationToken)).Select(assignment => assignment.UserId);
        await invalidations.TaskChangedAsync(task, userId, previousStatus == task.Status ? "updated" : "statusChanged", changedFields, affectedUsers, cancellationToken);
        if (await taskUnitOfWork.SaveTaskCommandAsync(cancellationToken) != TaskCommandSaveResult.Saved)
            return TaskConflict<TaskItemResponse>();
        return Result<TaskItemResponse>.Success(await ToTaskAsync(task, userId, cancellationToken));
    }

    public async Task<Result> DeleteTaskAsync(Guid taskItemId, CancellationToken cancellationToken = default)
    {
        var task = await projects.GetTaskAsync(taskItemId, cancellationToken);
        if (task is null)
        {
            return Result.Failure("Task not found.");
        }

        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId) || !await taskAuthorization.CanUpdateTask(userId, taskItemId, cancellationToken))
        {
            return Result.Failure("You are not allowed to update this task.");
        }

        task.MarkDeleted(clock.UtcNow);
        task.VersionNo++;
        await AuditAsync(userId, "TaskArchived", "TaskItem", task.Id, cancellationToken);
        var deletedTaskUsers = (await projects.ListAssignmentsAsync(task.Id, cancellationToken)).Select(assignment => assignment.UserId);
        await invalidations.TaskChangedAsync(task, userId, "deleted", affectedUserIds: deletedTaskUsers, cancellationToken: cancellationToken);
        if (await taskUnitOfWork.SaveTaskCommandAsync(cancellationToken) != TaskCommandSaveResult.Saved)
            return TaskConflict();
        return Result.Success();
    }

    public async Task<Result<IReadOnlyList<TaskAssignmentResponse>>> ListAssignmentsAsync(Guid taskItemId, CancellationToken cancellationToken = default)
    {
        var task = await projects.GetTaskAsync(taskItemId, cancellationToken);
        if (task is null || !CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId) || !await projectAuthorization.CanViewProject(userId, task.ProjectId, cancellationToken))
        {
            return Result<IReadOnlyList<TaskAssignmentResponse>>.Failure("Task not found.");
        }

        var assignments = await projects.ListAssignmentsAsync(taskItemId, cancellationToken);
        return Result<IReadOnlyList<TaskAssignmentResponse>>.Success(assignments.Select(ToAssignment).ToList());
    }

    public async Task<Result<TaskAssignmentResponse>> AddAssignmentAsync(Guid taskItemId, AddTaskAssignmentRequest request, CancellationToken cancellationToken = default)
    {
        var task = await projects.GetTaskAsync(taskItemId, cancellationToken);
        if (task is null || task.DeletedAt.HasValue)
        {
            return Result<TaskAssignmentResponse>.Failure("Task not found.");
        }

        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var actorUserId) || !await taskAuthorization.CanAssignTask(actorUserId, taskItemId, cancellationToken))
        {
            return Result<TaskAssignmentResponse>.Failure("You are not allowed to assign this task.");
        }

        if (request.EstimatedHours is < 0)
        {
            return Result<TaskAssignmentResponse>.Failure("Estimated hours cannot be negative.");
        }

        if (!await IsCompatibilityTaskMutableAsync(task, cancellationToken))
        {
            return CompatibilityAssignmentFailure<TaskAssignmentResponse>(
                "TASK_TRANSITION_GUARD_FAILED",
                "Project is read-only.");
        }

        if (!Enum.IsDefined(request.Role) || request.Role == TaskAssignmentRole.Owner)
        {
            return CompatibilityAssignmentFailure<TaskAssignmentResponse>(
                "TASK_ASSIGNMENT_ROLE_UNSUPPORTED",
                "Legacy Owner assignments are historical and cannot be created.");
        }

        if (!await RelationshipTargets.IsEligibleAsync(task.ProjectId, request.UserId, cancellationToken))
        {
            return CompatibilityAssignmentFailure<TaskAssignmentResponse>(
                "TASK_FORBIDDEN",
                "The assignment user is not available for this Task.");
        }

        var projectMember = await projects.GetMemberAsync(task.ProjectId, request.UserId, cancellationToken);
        var user = await users.GetByIdAsync(request.UserId, cancellationToken);
        if (projectMember is null || user is null)
        {
            return Result<TaskAssignmentResponse>.Failure("User must be a project member before assignment.");
        }

        var existing = await projects.ListAssignmentsAsync(taskItemId, cancellationToken);
        if (existing.Any(assignment => assignment.UserId == request.UserId && assignment.Role == request.Role))
        {
            return Result<TaskAssignmentResponse>.Failure("User already has this assignment role.");
        }

        var collaborators = await projects.ListCollaboratorsAsync(task.Id, cancellationToken);
        var planResult = PlanCompatibilityRelationshipChange(
            task,
            existing,
            collaborators,
            request.UserId,
            previousRole: null,
            newRole: request.Role,
            assignmentId: null);
        if (planResult.Error is not null)
        {
            return Result<TaskAssignmentResponse>.Failure(planResult.Error);
        }

        var assignment = new TaskAssignment
        {
            TaskItemId = taskItemId,
            UserId = request.UserId,
            User = user,
            Role = request.Role,
            EstimatedHours = request.EstimatedHours,
            AssignedByUserId = actorUserId,
            AssignedAt = clock.UtcNow
        };

        await projects.AddAssignmentAsync(assignment, cancellationToken);
        await ApplyCompatibilityRelationshipPlanAsync(task, planResult.Plan!, actorUserId, cancellationToken);
        var assignmentUsers = existing.Select(item => item.UserId).Append(request.UserId).Distinct().ToArray();
        var save = await CommitCompatibilityAssignmentAsync(
            task,
            actorUserId,
            "TaskAssigned",
            planResult.Plan!,
            assignmentUsers,
            cancellationToken);
        if (save != TaskCommandSaveResult.Saved)
            return save.Result == TaskCommandSaveResult.UniqueConflict
                ? (IsassignmentIdentityConstraint(save.ConstraintName)
                    ? AssignmentConflict<TaskAssignmentResponse>()
                    : GeneralTaskConflict<TaskAssignmentResponse>())
                : TaskConflict<TaskAssignmentResponse>();
        return Result<TaskAssignmentResponse>.Success(ToAssignment(assignment));
    }

    public async Task<Result<TaskAssignmentResponse>> UpdateAssignmentAsync(Guid assignmentId, UpdateTaskAssignmentRequest request, CancellationToken cancellationToken = default)
    {
        var assignment = await projects.GetAssignmentAsync(assignmentId, cancellationToken);
        if (assignment?.TaskItem is null || assignment.TaskItem.DeletedAt.HasValue)
        {
            return Result<TaskAssignmentResponse>.Failure("Assignment not found.");
        }

        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId) || !await taskAuthorization.CanAssignTask(userId, assignment.TaskItemId, cancellationToken))
        {
            return Result<TaskAssignmentResponse>.Failure("You are not allowed to assign this task.");
        }

        if (request.EstimatedHours is < 0 || request.ActualHours is < 0)
        {
            return Result<TaskAssignmentResponse>.Failure("Hours cannot be negative.");
        }

        if (!await IsCompatibilityTaskMutableAsync(assignment.TaskItem, cancellationToken))
        {
            return CompatibilityAssignmentFailure<TaskAssignmentResponse>(
                "TASK_TRANSITION_GUARD_FAILED",
                "Project is read-only.");
        }

        var existing = await projects.ListAssignmentsAsync(assignment.TaskItemId, cancellationToken);
        if (existing.Any(item => item.Id != assignment.Id && item.UserId == assignment.UserId && item.Role == request.Role))
        {
            return Result<TaskAssignmentResponse>.Failure("User already has this assignment role.");
        }

        var previousRole = assignment.Role;
        if (!Enum.IsDefined(request.Role) ||
            request.Role == TaskAssignmentRole.Owner && previousRole != TaskAssignmentRole.Owner)
        {
            return CompatibilityAssignmentFailure<TaskAssignmentResponse>(
                "TASK_ASSIGNMENT_ROLE_UNSUPPORTED",
                "Legacy Owner assignments are historical and cannot be created.");
        }

        if (request.Role != TaskAssignmentRole.Owner &&
            !await RelationshipTargets.IsEligibleAsync(assignment.TaskItem.ProjectId, assignment.UserId, cancellationToken))
        {
            return CompatibilityAssignmentFailure<TaskAssignmentResponse>(
                "TASK_FORBIDDEN",
                "The assignment user is not available for this Task.");
        }

        if (previousRole == request.Role &&
            assignment.EstimatedHours == request.EstimatedHours &&
            assignment.ActualHours == request.ActualHours)
        {
            return Result<TaskAssignmentResponse>.Success(ToAssignment(assignment));
        }

        var collaborators = await projects.ListCollaboratorsAsync(assignment.TaskItemId, cancellationToken);
        var planResult = PlanCompatibilityRelationshipChange(
            assignment.TaskItem,
            existing,
            collaborators,
            assignment.UserId,
            previousRole,
            request.Role,
            assignment.Id);
        if (planResult.Error is not null)
        {
            return Result<TaskAssignmentResponse>.Failure(planResult.Error);
        }

        assignment.Role = request.Role;
        assignment.EstimatedHours = request.EstimatedHours;
        assignment.ActualHours = request.ActualHours;
        await ApplyCompatibilityRelationshipPlanAsync(assignment.TaskItem, planResult.Plan!, userId, cancellationToken);
        var save = await CommitCompatibilityAssignmentAsync(
            assignment.TaskItem,
            userId,
            "TaskAssignmentUpdated",
            planResult.Plan!,
            [assignment.UserId],
            cancellationToken);
        if (save != TaskCommandSaveResult.Saved)
            return save.Result == TaskCommandSaveResult.UniqueConflict
                ? (IsassignmentIdentityConstraint(save.ConstraintName)
                    ? AssignmentConflict<TaskAssignmentResponse>()
                    : GeneralTaskConflict<TaskAssignmentResponse>())
                : TaskConflict<TaskAssignmentResponse>();
        return Result<TaskAssignmentResponse>.Success(ToAssignment(assignment));
    }

    public async Task<Result> DeleteAssignmentAsync(Guid assignmentId, CancellationToken cancellationToken = default)
    {
        var assignment = await projects.GetAssignmentAsync(assignmentId, cancellationToken);
        if (assignment is null)
        {
            return Result.Failure("Assignment not found.");
        }

        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId) || !await taskAuthorization.CanAssignTask(userId, assignment.TaskItemId, cancellationToken))
        {
            return Result.Failure("You are not allowed to assign this task.");
        }

        var task = assignment.TaskItem;
        if (task is null || task.DeletedAt.HasValue)
        {
            return Result.Failure("Task not found.");
        }

        if (!await IsCompatibilityTaskMutableAsync(task, cancellationToken))
        {
            return CompatibilityAssignmentFailure(
                "TASK_TRANSITION_GUARD_FAILED",
                "Project is read-only.");
        }

        var existing = await projects.ListAssignmentsAsync(task.Id, cancellationToken);
        var collaborators = await projects.ListCollaboratorsAsync(task.Id, cancellationToken);
        var planResult = PlanCompatibilityRelationshipChange(
            task,
            existing,
            collaborators,
            assignment.UserId,
            assignment.Role,
            newRole: null,
            assignment.Id);
        if (planResult.Error is not null)
        {
            return Result.Failure(planResult.Error);
        }

        projects.RemoveAssignment(assignment);
        await ApplyCompatibilityRelationshipPlanAsync(task, planResult.Plan!, userId, cancellationToken);
        if (await CommitCompatibilityAssignmentAsync(
                task,
                userId,
                "TaskAssignmentRemoved",
                planResult.Plan!,
                [assignment.UserId],
                cancellationToken) != TaskCommandSaveResult.Saved)
            return TaskConflict();
        return Result.Success();
    }

    public async Task<Result<IReadOnlyList<TaskDependencyResponse>>> ListDependenciesAsync(Guid taskItemId, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId))
            return DependencyFailure<IReadOnlyList<TaskDependencyResponse>>(
                "TASK_DEPENDENCY_AUTHENTICATION_REQUIRED",
                "Authentication is required.");
        var task = await projects.GetTaskAsync(taskItemId, cancellationToken);
        var project = task is null ? null : await projects.GetProjectAsync(task.ProjectId, cancellationToken);
        if (task is null ||
            task.DeletedAt.HasValue ||
            task.Kind != WorkItemKind.Task ||
            project is null ||
            project.DeletedAt.HasValue ||
            project.Status is ProjectStatus.Archived or ProjectStatus.Deleted ||
            !await projectAuthorization.CanViewProject(userId, task.ProjectId, cancellationToken))
        {
            return DependencyFailure<IReadOnlyList<TaskDependencyResponse>>(
                "TASK_DEPENDENCY_NOT_FOUND",
                "Task or dependency not found.");
        }

        if (await projects.CountGanttItemsBoundedAsync(
                task.ProjectId,
                MaximumGanttItems + 1,
                cancellationToken) > MaximumGanttItems)
        {
            return DependencyFailure<IReadOnlyList<TaskDependencyResponse>>(
                "GANTT_ITEM_LIMIT_EXCEEDED",
                $"The Project schedule exceeds the supported limit of {MaximumGanttItems} work items.");
        }
        var dependencies = await projects.ListDependenciesBoundedAsync(
            taskItemId,
            MaximumGanttDependencies + 1,
            cancellationToken);
        if (dependencies.Count > MaximumGanttDependencies)
            return DependencyFailure<IReadOnlyList<TaskDependencyResponse>>(
                "TASK_DEPENDENCY_LIMIT_EXCEEDED",
                "The Project dependency graph exceeds the supported schedule limit.");
        var projectTasks = await projects.ListTasksBoundedAsync(
            task.ProjectId,
            MaximumGanttItems,
            cancellationToken);
        var tasksById = projectTasks
            .Where(item => !item.DeletedAt.HasValue)
            .ToDictionary(item => item.Id);
        var canManage = await CanManageGanttDependenciesAsync(userId, project, cancellationToken);
        var response = dependencies
            .Where(dependency =>
                dependency.ProjectId == project.Id &&
                tasksById.ContainsKey(dependency.PredecessorTaskItemId) &&
                tasksById.ContainsKey(dependency.SuccessorTaskItemId))
            .OrderBy(dependency => dependency.PredecessorTaskItemId)
            .ThenBy(dependency => dependency.SuccessorTaskItemId)
            .ThenBy(dependency => dependency.Id)
            .Select(dependency => ToDependency(
                dependency,
                tasksById[dependency.SuccessorTaskItemId].VersionNo,
                canManage && dependency.DependencyType == TaskDependencyType.FinishToStart,
                DependencyDateWarnings(dependency, projectTasks)))
            .ToList();
        return Result<IReadOnlyList<TaskDependencyResponse>>.Success(response);
    }

    public async Task<Result<TaskDependencyResponse>> AddDependencyAsync(Guid taskItemId, AddTaskDependencyRequest request, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId))
            return DependencyFailure<TaskDependencyResponse>(
                "TASK_DEPENDENCY_AUTHENTICATION_REQUIRED",
                "Authentication is required.");
        var successor = await projects.GetTaskAsync(taskItemId, cancellationToken);
        var project = successor is null ? null : await projects.GetProjectAsync(successor.ProjectId, cancellationToken);
        if (successor is null ||
            successor.DeletedAt.HasValue ||
            successor.Kind != WorkItemKind.Task ||
            project is null ||
            project.DeletedAt.HasValue ||
            project.Status is ProjectStatus.Archived or ProjectStatus.Deleted ||
            !await projectAuthorization.CanViewProject(userId, successor.ProjectId, cancellationToken))
        {
            return DependencyFailure<TaskDependencyResponse>(
                "TASK_DEPENDENCY_NOT_FOUND",
                "Task or dependency not found.");
        }

        if (!await CanManageGanttDependenciesAsync(userId, project, cancellationToken))
        {
            return await RejectVisibleDependencyAsync<TaskDependencyResponse>(
                userId,
                successor,
                "TASK_DEPENDENCY_FORBIDDEN",
                "Dependency management is not authorized.",
                cancellationToken);
        }

        if (request.ExpectedVersion <= 0)
            return await RejectVisibleDependencyAsync<TaskDependencyResponse>(
                userId,
                successor,
                "TASK_DEPENDENCY_INVALID_EXPECTED_VERSION",
                "Expected version must be a positive integer.",
                cancellationToken);
        if (successor.VersionNo != request.ExpectedVersion)
            return await RejectVisibleDependencyAsync<TaskDependencyResponse>(
                userId,
                successor,
                "TASK_STALE_VERSION",
                "Task has changed. Refetch and retry.",
                cancellationToken);
        if (await projects.CountGanttItemsBoundedAsync(
                successor.ProjectId,
                MaximumGanttItems + 1,
                cancellationToken) > MaximumGanttItems)
        {
            return await RejectVisibleDependencyAsync<TaskDependencyResponse>(
                userId,
                successor,
                "GANTT_ITEM_LIMIT_EXCEEDED",
                $"The Project schedule exceeds the supported limit of {MaximumGanttItems} work items.",
                cancellationToken);
        }
        if (request.DependencyType != TaskDependencyType.FinishToStart)
        {
            return await RejectVisibleDependencyAsync<TaskDependencyResponse>(
                userId,
                successor,
                "TASK_DEPENDENCY_TYPE_DEFERRED",
                "Only Finish-to-Start dependencies can be authored.",
                cancellationToken);
        }

        if (successor.Id == request.PredecessorTaskId)
        {
            return await RejectVisibleDependencyAsync<TaskDependencyResponse>(
                userId,
                successor,
                "TASK_DEPENDENCY_SELF",
                "A Task cannot depend on itself.",
                cancellationToken);
        }

        // Resolve the neighbor only after the visible successor and management
        // permission are established. Unknown, deleted, and cross-Project IDs
        // intentionally share one redacted outcome.
        var predecessor = await projects.GetTaskAsync(request.PredecessorTaskId, cancellationToken);
        if (predecessor is null ||
            predecessor.DeletedAt.HasValue ||
            predecessor.Kind != WorkItemKind.Task ||
            predecessor.ProjectId != successor.ProjectId)
        {
            return await RejectVisibleDependencyAsync<TaskDependencyResponse>(
                userId,
                successor,
                "TASK_DEPENDENCY_NOT_FOUND",
                "Task or dependency not found.",
                cancellationToken);
        }

        if (await projects.DependencyExistsAsync(predecessor.Id, successor.Id, cancellationToken))
        {
            return await RejectVisibleDependencyAsync<TaskDependencyResponse>(
                userId,
                successor,
                "TASK_DEPENDENCY_DUPLICATE",
                "Task dependency already exists.",
                cancellationToken);
        }

        var cycle = await WouldCreateCycleAsync(predecessor.Id, successor.Id, successor.ProjectId, cancellationToken);
        if (cycle == DependencyCycleCheck.LimitExceeded)
        {
            return await RejectVisibleDependencyAsync<TaskDependencyResponse>(
                userId,
                successor,
                "TASK_DEPENDENCY_LIMIT_EXCEEDED",
                "The Project dependency graph exceeds the supported schedule limit.",
                cancellationToken);
        }
        if (cycle == DependencyCycleCheck.Cycle)
        {
            return await RejectVisibleDependencyAsync<TaskDependencyResponse>(
                userId,
                successor,
                "TASK_DEPENDENCY_CYCLE",
                "Task dependency would create a cycle.",
                cancellationToken);
        }

        var projectTasks = await projects.ListTasksBoundedAsync(
            successor.ProjectId,
            MaximumGanttItems,
            cancellationToken);

        var dependency = new TaskDependency
        {
            ProjectId = successor.ProjectId,
            PredecessorTaskItemId = predecessor.Id,
            SuccessorTaskItemId = successor.Id,
            DependencyType = request.DependencyType
        };

        await projects.AddDependencyAsync(dependency, cancellationToken);
        successor.VersionNo++;
        await auditLogger.LogAsync(new AuditLogEntry(
            userId,
            "TaskDependencyAdded",
            "TaskDependency",
            dependency.Id,
            "Finish-to-Start dependency added.",
            WorkspaceId: successor.WorkspaceId,
            ProjectId: successor.ProjectId,
            Metadata: new Dictionary<string, object?>
            {
                ["predecessorTaskId"] = predecessor.Id,
                ["successorTaskId"] = successor.Id,
                ["versionBefore"] = successor.VersionNo - 1
            }), cancellationToken);
        await invalidations.TaskChangedAsync(
            successor,
            userId,
            "dependencyChanged",
            ["dependencies"],
            cancellationToken: cancellationToken);
        await invalidations.ProjectChangedAsync(project, userId, "dependencyChanged", cancellationToken);
        var save = await taskUnitOfWork.SaveTaskCommandAsync(cancellationToken);
        if (!save.IsSaved)
        {
            var code = save.Result == TaskCommandSaveResult.ConcurrencyConflict
                ? "TASK_STALE_VERSION"
                : "TASK_DEPENDENCY_DUPLICATE";
            var message = save.Result == TaskCommandSaveResult.ConcurrencyConflict
                ? "Task has changed. Refetch and retry."
                : "Task dependency already exists.";
            taskUnitOfWork.ClearTaskCommandTracking();
            return await RejectVisibleDependencyAsync<TaskDependencyResponse>(
                userId,
                successor,
                code,
                message,
                cancellationToken);
        }
        return Result<TaskDependencyResponse>.Success(ToDependency(
            dependency,
            successor.VersionNo,
            true,
            DependencyDateWarnings(dependency, projectTasks)));
    }

    public async Task<Result> DeleteDependencyAsync(
        Guid taskItemId,
        Guid dependencyId,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId))
            return DependencyFailure(
                "TASK_DEPENDENCY_AUTHENTICATION_REQUIRED",
                "Authentication is required.");
        var successor = await projects.GetTaskAsync(taskItemId, cancellationToken);
        var project = successor is null ? null : await projects.GetProjectAsync(successor.ProjectId, cancellationToken);
        if (successor is null ||
            successor.DeletedAt.HasValue ||
            successor.Kind != WorkItemKind.Task ||
            project is null ||
            project.DeletedAt.HasValue ||
            project.Status is ProjectStatus.Archived or ProjectStatus.Deleted ||
            !await projectAuthorization.CanViewProject(userId, successor.ProjectId, cancellationToken))
        {
            return DependencyFailure("TASK_DEPENDENCY_NOT_FOUND", "Task or dependency not found.");
        }

        if (!await CanManageGanttDependenciesAsync(userId, project, cancellationToken))
        {
            return await RejectVisibleDependencyAsync(
                userId,
                successor,
                "TASK_DEPENDENCY_FORBIDDEN",
                "Dependency management is not authorized.",
                cancellationToken);
        }

        if (expectedVersion <= 0)
            return await RejectVisibleDependencyAsync(
                userId,
                successor,
                "TASK_DEPENDENCY_INVALID_EXPECTED_VERSION",
                "Expected version must be a positive integer.",
                cancellationToken);
        if (successor.VersionNo != expectedVersion)
            return await RejectVisibleDependencyAsync(
                userId,
                successor,
                "TASK_STALE_VERSION",
                "Task has changed. Refetch and retry.",
                cancellationToken);
        if (await projects.CountGanttItemsBoundedAsync(
                successor.ProjectId,
                MaximumGanttItems + 1,
                cancellationToken) > MaximumGanttItems)
        {
            return await RejectVisibleDependencyAsync(
                userId,
                successor,
                "GANTT_ITEM_LIMIT_EXCEEDED",
                $"The Project schedule exceeds the supported limit of {MaximumGanttItems} work items.",
                cancellationToken);
        }

        var dependency = await projects.GetDependencyAsync(dependencyId, cancellationToken);
        if (dependency is null ||
            dependency.ProjectId != successor.ProjectId ||
            dependency.SuccessorTaskItemId != successor.Id)
        {
            return await RejectVisibleDependencyAsync(
                userId,
                successor,
                "TASK_DEPENDENCY_NOT_FOUND",
                "Task or dependency not found.",
                cancellationToken);
        }
        if (dependency.DependencyType != TaskDependencyType.FinishToStart)
        {
            return await RejectVisibleDependencyAsync(
                userId,
                successor,
                "TASK_DEPENDENCY_LEGACY_READ_ONLY",
                "Legacy non-Finish-to-Start dependencies are read-only.",
                cancellationToken);
        }

        projects.RemoveDependency(dependency);
        successor.VersionNo++;
        await auditLogger.LogAsync(new AuditLogEntry(
            userId,
            "TaskDependencyRemoved",
            "TaskDependency",
            dependency.Id,
            "Finish-to-Start dependency removed.",
            WorkspaceId: successor.WorkspaceId,
            ProjectId: successor.ProjectId,
            Metadata: new Dictionary<string, object?>
            {
                ["predecessorTaskId"] = dependency.PredecessorTaskItemId,
                ["successorTaskId"] = dependency.SuccessorTaskItemId,
                ["versionBefore"] = successor.VersionNo - 1
            }), cancellationToken);
        await invalidations.TaskChangedAsync(
            successor,
            userId,
            "dependencyChanged",
            ["dependencies"],
            cancellationToken: cancellationToken);
        await invalidations.ProjectChangedAsync(project, userId, "dependencyChanged", cancellationToken);
        var save = await taskUnitOfWork.SaveTaskCommandAsync(cancellationToken);
        if (!save.IsSaved)
        {
            var code = save.Result == TaskCommandSaveResult.ConcurrencyConflict
                ? "TASK_STALE_VERSION"
                : "TASK_DEPENDENCY_CONFLICT";
            taskUnitOfWork.ClearTaskCommandTracking();
            return await RejectVisibleDependencyAsync(
                userId,
                successor,
                code,
                "Task dependency has changed. Refetch and retry.",
                cancellationToken);
        }
        return Result.Success();
    }

    public async Task<Result<PagedResponse<CommentResponse>>> ListCommentsAsync(CommentTargetType targetType, Guid targetId, ProjectChildListQuery query, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId) || !await commentAuthorization.CanCommentOnTarget(userId, targetType, targetId, cancellationToken))
        {
            return Result<PagedResponse<CommentResponse>>.Failure("Comment target not found.");
        }

        var comments = await projects.ListCommentsAsync(targetType, targetId, cancellationToken);
        var filtered = comments
            .Where(comment => !comment.DeletedAt.HasValue)
            .Where(comment => MatchesSearch(comment.Body, null, query.Search))
            .Select(ToComment)
            .ToList();
        return Result<PagedResponse<CommentResponse>>.Success(ToPagedResponse(filtered, query.SafePage, query.SafePageSize));
    }

    public async Task<Result<CommentResponse>> AddCommentAsync(CreateCommentRequest request, CancellationToken cancellationToken = default)
    {
        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId) || !await commentAuthorization.CanCommentOnTarget(userId, request.TargetType, request.TargetId, cancellationToken))
        {
            return Result<CommentResponse>.Failure("Comment target not found.");
        }

        if (string.IsNullOrWhiteSpace(request.Body))
        {
            return Result<CommentResponse>.Failure("Comment body is required.");
        }

        var target = await ResolveCommentTargetAsync(request.TargetType, request.TargetId, cancellationToken);
        if (target is null)
        {
            return Result<CommentResponse>.Failure("Comment target not found.");
        }

        var comment = new Comment
        {
            WorkspaceId = target.Value.WorkspaceId,
            AuthorUserId = userId,
            TargetType = request.TargetType,
            TargetId = request.TargetId,
            Body = request.Body.Trim()
        };

        await projects.AddCommentAsync(comment, cancellationToken);
        await AuditAsync(userId, "CommentAdded", "Comment", comment.Id, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result<CommentResponse>.Success(ToComment(comment));
    }

    public async Task<Result<CommentResponse>> UpdateCommentAsync(Guid commentId, UpdateCommentRequest request, CancellationToken cancellationToken = default)
    {
        var comment = await projects.GetCommentAsync(commentId, cancellationToken);
        if (comment is null || comment.DeletedAt.HasValue)
        {
            return Result<CommentResponse>.Failure("Comment not found.");
        }

        if (string.IsNullOrWhiteSpace(request.Body))
        {
            return Result<CommentResponse>.Failure("Comment body is required.");
        }

        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId) || !await CanModifyCommentAsync(userId, comment, cancellationToken))
        {
            return Result<CommentResponse>.Failure("You are not allowed to edit this comment.");
        }

        comment.Body = request.Body.Trim();
        await AuditAsync(userId, "CommentEdited", "Comment", comment.Id, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result<CommentResponse>.Success(ToComment(comment));
    }

    public async Task<Result> DeleteCommentAsync(Guid commentId, CancellationToken cancellationToken = default)
    {
        var comment = await projects.GetCommentAsync(commentId, cancellationToken);
        if (comment is null)
        {
            return Result.Failure("Comment not found.");
        }

        if (!CurrentUserIdentity.TryGetAuthenticatedUserId(currentUser, out var userId) || !await CanModifyCommentAsync(userId, comment, cancellationToken))
        {
            return Result.Failure("You are not allowed to delete this comment.");
        }

        comment.MarkDeleted(clock.UtcNow);
        await AuditAsync(userId, "CommentDeleted", "Comment", comment.Id, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    private async Task<Result> ValidateParentAccessAsync(Project project, Guid userId, CancellationToken cancellationToken)
    {
        var workspaceMember = await workspaces.GetMemberAsync(project.WorkspaceId, userId, cancellationToken);
        if (workspaceMember is not { Status: MembershipStatus.Active })
        {
            return Result.Failure("User must belong to the workspace before joining the project.");
        }

        if (project.GroupId.HasValue && await groups.GetMemberAsync(project.GroupId.Value, userId, cancellationToken) is null)
        {
            return Result.Failure("User must belong to the group before joining the project.");
        }

        return Result.Success();
    }

    private async Task<Result> ValidateTaskRequestAsync(Guid projectId, Guid? milestoneId, string title, DateOnly? startDate, DateOnly? dueDate, int? progressPercent, CancellationToken cancellationToken)
    {
        if (await projects.GetProjectAsync(projectId, cancellationToken) is null)
        {
            return Result.Failure("Project not found.");
        }

        if (milestoneId.HasValue)
        {
            var milestone = await projects.GetMilestoneAsync(milestoneId.Value, cancellationToken);
            if (milestone is null || milestone.ProjectId != projectId)
            {
                return Result.Failure("Milestone must belong to the same project.");
            }
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return Result.Failure("Task title is required.");
        }

        if (HasInvalidDateRange(startDate, dueDate))
        {
            return Result.Failure("Task due date cannot be before the start date.");
        }

        if (progressPercent is < 0 or > 100)
        {
            return Result.Failure("Task progress must be between 0 and 100.");
        }

        return Result.Success();
    }

    private async Task<DependencyCycleCheck> WouldCreateCycleAsync(
        Guid predecessorTaskId,
        Guid successorTaskId,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var dependencies = await projects.ListProjectDependenciesBoundedAsync(
            projectId,
            MaximumGanttDependencies + 1,
            cancellationToken);
        if (dependencies.Count >= MaximumGanttDependencies)
            return DependencyCycleCheck.LimitExceeded;
        var edges = dependencies
            .GroupBy(dependency => dependency.PredecessorTaskItemId)
            .ToDictionary(group => group.Key, group => group.Select(dependency => dependency.SuccessorTaskItemId).ToList());

        if (!edges.TryGetValue(predecessorTaskId, out var successors))
        {
            edges[predecessorTaskId] = successors = [];
        }

        successors.Add(successorTaskId);
        var stack = new Stack<Guid>();
        var visited = new HashSet<Guid>();
        stack.Push(successorTaskId);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!visited.Add(current))
            {
                continue;
            }

            if (current == predecessorTaskId)
            {
                return DependencyCycleCheck.Cycle;
            }

            if (edges.TryGetValue(current, out var next))
            {
                foreach (var nextTaskId in next)
                {
                    stack.Push(nextTaskId);
                }
            }
        }

        return DependencyCycleCheck.None;
    }

    private async Task<bool> CanManageGanttDependenciesAsync(
        Guid actorUserId,
        Project project,
        CancellationToken cancellationToken)
    {
        var workspaceMember = await workspaces.GetMemberAsync(project.WorkspaceId, actorUserId, cancellationToken);
        if (workspaceMember is { Status: MembershipStatus.Active, Role: WorkspaceRole.ReadOnly })
            return false;
        return await projectAuthorization.CanManageProject(actorUserId, project.Id, cancellationToken);
    }

    private async Task<bool> CanModifyCommentAsync(Guid userId, Comment comment, CancellationToken cancellationToken)
    {
        if (comment.AuthorUserId == userId)
        {
            return true;
        }

        var target = await ResolveCommentTargetAsync(comment.TargetType, comment.TargetId, cancellationToken);
        return target is not null && await projectAuthorization.CanManageProject(userId, target.Value.ProjectId, cancellationToken);
    }

    private async Task<(Guid ProjectId, Guid WorkspaceId)?> ResolveCommentTargetAsync(CommentTargetType targetType, Guid targetId, CancellationToken cancellationToken)
    {
        if (targetType == CommentTargetType.Project)
        {
            var project = await projects.GetProjectAsync(targetId, cancellationToken);
            return project is null ? null : (project.Id, project.WorkspaceId);
        }

        if (targetType == CommentTargetType.TaskItem)
        {
            var task = await projects.GetTaskAsync(targetId, cancellationToken);
            if (task is null) return null;
            var project = await projects.GetProjectAsync(task.ProjectId, cancellationToken);
            return project is null ? null : (project.Id, project.WorkspaceId);
        }

        if (targetType == CommentTargetType.Milestone)
        {
            var milestone = await projects.GetMilestoneAsync(targetId, cancellationToken);
            if (milestone is null) return null;
            var project = await projects.GetProjectAsync(milestone.ProjectId, cancellationToken);
            return project is null ? null : (project.Id, project.WorkspaceId);
        }

        return null;
    }

    private async Task<bool> SaveMilestoneMutationAsync(CancellationToken cancellationToken)
    {
        var save = await taskUnitOfWork.SaveTaskCommandAsync(cancellationToken);
        if (save.IsSaved)
            return true;
        taskUnitOfWork.ClearTaskCommandTracking();
        return false;
    }

    private async Task<bool> SaveProjectMutationAsync(CancellationToken cancellationToken)
    {
        var save = await taskUnitOfWork.SaveTaskCommandAsync(cancellationToken);
        if (save.IsSaved)
            return true;
        taskUnitOfWork.ClearTaskCommandTracking();
        return false;
    }

    private Task AuditAsync(Guid actorUserId, string action, string targetType, Guid targetId, CancellationToken cancellationToken)
    {
        return auditLogger.LogAsync(new AuditLogEntry(actorUserId, action, targetType, targetId, SummaryFor(action)), cancellationToken);
    }

    private async Task PublishProjectAccessInvalidationsAsync(
        Project project,
        IReadOnlyList<Guid> affectedUserIds,
        string change,
        CancellationToken cancellationToken)
    {
        foreach (var affectedUserId in affectedUserIds.Distinct())
        {
            await authorizationChanges.PublishAsync(
                project.TenantId,
                affectedUserId,
                "project",
                project.Id,
                change,
                cancellationToken);
        }
    }

    private static bool RemovesCurrentReadAccess(ProjectStatus current, ProjectStatus next) =>
        next == ProjectStatus.Archived ||
        (next == ProjectStatus.Suspended &&
         current is ProjectStatus.Active or ProjectStatus.Review or ProjectStatus.Completed);

    private static bool CanRecoverProjectTo(
        Project project,
        ProjectStatus? storedStatus,
        ProjectStatus requestedStatus)
    {
        if (project.ActivationState == ProjectActivationState.LegacyUnknown ||
            !storedStatus.HasValue ||
            storedStatus.Value != requestedStatus)
        {
            return false;
        }

        if (requestedStatus == ProjectStatus.Suspended)
        {
            return project.SuspendedFromStatus.HasValue &&
                   IsRecoveryStateConsistent(project, project.SuspendedFromStatus.Value);
        }

        return IsRecoveryStateConsistent(project, requestedStatus);
    }

    private static bool IsRecoveryStateConsistent(Project project, ProjectStatus status) => status switch
    {
        ProjectStatus.Planning => project is
        {
            ActivationState: ProjectActivationState.NeverActivated,
            ActivatedAtUtc: null,
            ActivationVersion: null
        },
        ProjectStatus.Active or ProjectStatus.Review or ProjectStatus.Completed => project is
        {
            ActivationState: ProjectActivationState.Activated,
            ActivatedAtUtc: not null,
            ActivationVersion: > 0
        },
        _ => false
    };

    private static bool IsValidProjectStatusTransition(ProjectStatus current, ProjectStatus next)
    {
        if (current == next) return true;
        if (next == ProjectStatus.Deleted) return false;

        return current switch
        {
            ProjectStatus.Planning => next is ProjectStatus.Suspended or ProjectStatus.Archived,
            ProjectStatus.Active => next is ProjectStatus.Review or ProjectStatus.Completed or ProjectStatus.Suspended or ProjectStatus.Archived,
            ProjectStatus.Review => next is ProjectStatus.Active or ProjectStatus.Completed or ProjectStatus.Suspended or ProjectStatus.Archived,
            ProjectStatus.Completed => next is ProjectStatus.Archived,
            ProjectStatus.Suspended => next is ProjectStatus.Archived,
            _ => false
        };
    }

    private static IReadOnlyList<string> ChangedTaskFields(UpdateTaskItemRequest request, TaskItemStatus previousStatus)
    {
        var fields = new List<string>();
        if (request.Title is not null) fields.Add("title");
        if (request.Description is not null) fields.Add("description");
        if (request.MilestoneId.HasValue) fields.Add("milestoneId");
        if (request.Priority.HasValue) fields.Add("priority");
        if (request.StartDate.HasValue) fields.Add("startDate");
        if (request.DueDate.HasValue) fields.Add("dueDate");
        if (request.ProgressPercent.HasValue) fields.Add("progressPercent");
        if (request.Status.HasValue && request.Status != previousStatus) fields.Add("status");
        return fields;
    }

    private static string SummaryFor(string action) => action switch
    {
        "ProjectStatusChanged" => "Project status changed.",
        "ProjectRestored" => "Project restored to its recorded lifecycle state.",
        "TaskAssigned" => "Task assignment added.",
        "TaskAssignmentUpdated" => "Task assignment changed.",
        "TaskAssignmentRemoved" => "Task assignment removed.",
        _ => $"{action} completed."
    };

    private static bool HasInvalidDateRange(DateOnly? startDate, DateOnly? endDate)
    {
        return startDate.HasValue && endDate.HasValue && endDate.Value < startDate.Value;
    }

    private static bool MatchesSearch(string value, string? secondaryValue, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        return value.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase) ||
            (secondaryValue?.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static PagedResponse<T> ToPagedResponse<T>(IReadOnlyList<T> items, int page, int pageSize)
    {
        return new PagedResponse<T>(items.Skip((page - 1) * pageSize).Take(pageSize).ToList(), page, pageSize, items.Count);
    }

    private async Task<ProjectResponse> ToProjectAsync(
        Project project,
        Guid userId,
        bool canActivate,
        CancellationToken cancellationToken)
    {
        var canCreateTask = await taskAuthorization.CanCreateTask(userId, project.Id, cancellationToken);
        return new ProjectResponse(
            project.Id,
            project.WorkspaceId,
            project.GroupId,
            project.OwnerUserId,
            project.Name,
            project.Description,
            project.Status,
            project.StartDate,
            project.DueDate,
            project.VersionNo,
            project.CreatedAt,
            project.UpdatedAt,
            new ProjectUiPermissionResponse(canCreateTask, canActivate),
            project.Visibility,
            project.ActivationState,
            project.ActivatedAtUtc,
            project.ActivationVersion,
            project.SuspendedFromStatus,
            project.ArchivedFromStatus);
    }

    private async Task<bool> CanActivateProjectAsync(
        Project project,
        Guid userId,
        CancellationToken cancellationToken)
    {
        if (!IsCanonicalActivationCandidate(project))
        {
            return false;
        }

        return (await projects.ListActivatableProjectIdsAsync(
                userId,
                [project.Id],
                cancellationToken))
            .Contains(project.Id);
    }

    private static bool IsCanonicalActivationCandidate(Project project) =>
        project is
        {
            VersionNo: > 0,
            Visibility: not null,
            ActivationState: ProjectActivationState.NeverActivated,
            Status: ProjectStatus.Planning,
            ActivatedAtUtc: null,
            ActivationVersion: null
        };

    private static ProjectMemberResponse ToProjectMember(ProjectMember member)
    {
        return new ProjectMemberResponse(member.UserId, member.User?.DisplayName ?? string.Empty, member.User?.Email ?? string.Empty, member.Role, member.JoinedAt);
    }

    private static MilestoneResponse ToMilestone(Milestone milestone)
    {
        return new MilestoneResponse(milestone.Id, milestone.ProjectId, milestone.Name, milestone.Description, milestone.DueDate, milestone.Status, milestone.SortOrder, milestone.CreatedAt, milestone.UpdatedAt, milestone.VersionNo);
    }

    private async Task<TaskItemResponse> ToTaskAsync(
        TaskItem task,
        Guid userId,
        CancellationToken cancellationToken,
        ParentTaskDerivedValues? derivedOverride = null,
        TimeZoneInfo? timeZoneOverride = null,
        bool? hasArtifactOverride = null)
    {
        var canEdit = await taskAuthorization.CanUpdateTask(userId, task.Id, cancellationToken);
        var canAssign = await taskAuthorization.CanAssignTask(userId, task.Id, cancellationToken);
        var derived = derivedOverride ??
            ParentTaskDerivedValuesCalculator.Calculate(
                task,
                await projects.ListTasksAsync(task.ProjectId, cancellationToken),
                CategoryOf);
        var timeZone = timeZoneOverride ?? (timeZones is null
            ? TimeZoneInfo.Utc
            : await timeZones.ResolveAsync(task.TenantId, task.WorkspaceId, cancellationToken));
        var hasArtifact = hasArtifactOverride ??
            (await projects.ListTaskIdsWithArtifactsAsync(task.ProjectId, cancellationToken)).Contains(task.Id);
        var stageCategory = CategoryOf(task);
        return new TaskItemResponse(
            task.Id,
            task.ProjectId,
            task.MilestoneId,
            task.Title,
            task.Description,
            task.Status,
            task.Priority,
            derived.PlannedStartDate,
            derived.PlannedEndDate,
            derived.ProgressPercent,
            task.CreatedByUserId,
            task.CreatedAt,
            task.UpdatedAt,
            new TaskUiPermissionResponse(
                canEdit,
                canAssign,
                false,
                canEdit,
                Array.Empty<TaskItemStatus>(),
                await GetTaskDomainV1RowVersionAsync(task, cancellationToken)),
            derived.PlannedStartDate,
            derived.PlannedEndDate,
            derived.IsDerived,
            TaskDeadlineCalculator.IsOverdue(task, stageCategory, timeZone, clock.UtcNow, derived.PlannedEndDate),
            task.VersionNo,
            task.WorkflowStageId,
            task.WorkflowStage?.Name ?? StageCategoryDisplayName(stageCategory),
            stageCategory,
            task.IsBlocked,
            hasArtifact);
    }

    private static string StageCategoryDisplayName(TaskStageCategory category) => category switch
    {
        TaskStageCategory.InProgress => "In progress",
        _ => category.ToString()
    };

    private static TaskStageCategory CategoryOf(TaskItem task) => task.WorkflowStage?.InternalCategory ?? task.Status switch
    {
        TaskItemStatus.InProgress => TaskStageCategory.InProgress,
        TaskItemStatus.WaitingReview => TaskStageCategory.Review,
        TaskItemStatus.Completed => TaskStageCategory.Done,
        TaskItemStatus.Cancelled => TaskStageCategory.Cancelled,
        _ => TaskStageCategory.Todo
    };

    private async Task<string?> GetTaskDomainV1RowVersionAsync(TaskItem task, CancellationToken cancellationToken)
    {
        if (featureFlags is null)
        {
            return null;
        }

        _taskDomainV1Enabled ??= featureFlags.IsEnabledAsync(FeatureKeys.TasksDomainV1, cancellationToken);
        return await _taskDomainV1Enabled
            ? task.VersionNo.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;
    }

    private static TaskAssignmentResponse ToAssignment(TaskAssignment assignment)
    {
        return new TaskAssignmentResponse(assignment.Id, assignment.TaskItemId, assignment.UserId, assignment.User?.DisplayName ?? string.Empty, assignment.Role, assignment.EstimatedHours, assignment.ActualHours, assignment.AssignedAt, assignment.AssignedByUserId);
    }

    private CompatibilityRelationshipPlanResult PlanCompatibilityRelationshipChange(
        TaskItem task,
        IReadOnlyList<TaskAssignment> assignments,
        IReadOnlyList<WorkItemCollaborator> collaborators,
        Guid relationshipUserId,
        TaskAssignmentRole? previousRole,
        TaskAssignmentRole? newRole,
        Guid? assignmentId)
    {
        if (newRole == TaskAssignmentRole.Owner && previousRole != TaskAssignmentRole.Owner)
        {
            return CompatibilityRelationshipPlanResult.Failure(
                "TASK_ASSIGNMENT_ROLE_UNSUPPORTED",
                "Legacy Owner assignments are historical and cannot be created.");
        }

        var originalPrimaryAssigneeUserId = task.PrimaryAssigneeUserId;
        var originalReviewerUserId = task.ReviewerUserId;
        var finalPrimaryAssigneeUserId = originalPrimaryAssigneeUserId;
        var finalReviewerUserId = originalReviewerUserId;
        var finalCollaboratorUserIds = collaborators.Select(item => item.UserId).ToHashSet();
        WorkItemCollaborator? collaboratorToRemove = null;
        var addCollaborator = false;

        bool HasOtherRole(TaskAssignmentRole role) => assignments.Any(item =>
            item.Id != assignmentId && item.Role == role);

        if (previousRole == newRole)
        {
            switch (previousRole)
            {
                case TaskAssignmentRole.Assignee:
                    if (HasOtherRole(TaskAssignmentRole.Assignee) ||
                        originalPrimaryAssigneeUserId != relationshipUserId)
                    {
                        return CompatibilityRelationshipPlanResult.Failure(
                            "TASK_ASSIGNMENT_AMBIGUOUS",
                            "The legacy assignee row does not map unambiguously to the canonical primary assignee.");
                    }
                    break;

                case TaskAssignmentRole.Reviewer:
                    if (HasOtherRole(TaskAssignmentRole.Reviewer) ||
                        originalReviewerUserId != relationshipUserId)
                    {
                        return CompatibilityRelationshipPlanResult.Failure(
                            "TASK_ASSIGNMENT_AMBIGUOUS",
                            "The legacy reviewer row does not map unambiguously to the canonical reviewer.");
                    }
                    break;

                case TaskAssignmentRole.Support:
                    if (!finalCollaboratorUserIds.Contains(relationshipUserId))
                    {
                        return CompatibilityRelationshipPlanResult.Failure(
                            "TASK_ASSIGNMENT_AMBIGUOUS",
                            "The legacy Support row does not map unambiguously to a canonical collaborator.");
                    }
                    break;
            }
        }
        else
        {
            switch (previousRole)
            {
                case TaskAssignmentRole.Assignee:
                    if (originalPrimaryAssigneeUserId == relationshipUserId)
                    {
                        if (HasOtherRole(TaskAssignmentRole.Assignee))
                        {
                            return CompatibilityRelationshipPlanResult.Failure(
                                "TASK_ASSIGNMENT_AMBIGUOUS",
                                "Multiple legacy assignee rows prevent a canonical relationship change.");
                        }
                        finalPrimaryAssigneeUserId = null;
                    }
                    else if (newRole.HasValue)
                    {
                        return CompatibilityRelationshipPlanResult.Failure(
                            "TASK_ASSIGNMENT_AMBIGUOUS",
                            "The legacy assignee row does not map to the canonical primary assignee.");
                    }
                    break;

                case TaskAssignmentRole.Reviewer:
                    if (originalReviewerUserId == relationshipUserId)
                    {
                        if (HasOtherRole(TaskAssignmentRole.Reviewer))
                        {
                            return CompatibilityRelationshipPlanResult.Failure(
                                "TASK_ASSIGNMENT_AMBIGUOUS",
                                "Multiple legacy reviewer rows prevent a canonical relationship change.");
                        }
                        finalReviewerUserId = null;
                    }
                    else if (newRole.HasValue)
                    {
                        return CompatibilityRelationshipPlanResult.Failure(
                            "TASK_ASSIGNMENT_AMBIGUOUS",
                            "The legacy reviewer row does not map to the canonical reviewer.");
                    }
                    break;

                case TaskAssignmentRole.Support:
                    collaboratorToRemove = collaborators.FirstOrDefault(item => item.UserId == relationshipUserId);
                    if (collaboratorToRemove is null && newRole.HasValue)
                    {
                        return CompatibilityRelationshipPlanResult.Failure(
                            "TASK_ASSIGNMENT_AMBIGUOUS",
                            "The legacy Support row does not map to a canonical collaborator.");
                    }
                    if (collaboratorToRemove is not null)
                    {
                        finalCollaboratorUserIds.Remove(relationshipUserId);
                    }
                    break;
            }

            switch (newRole)
            {
                case TaskAssignmentRole.Assignee:
                    if (HasOtherRole(TaskAssignmentRole.Assignee))
                    {
                        return CompatibilityRelationshipPlanResult.Failure(
                            "TASK_ASSIGNMENT_AMBIGUOUS",
                            "A canonical Task can have only one primary assignee.");
                    }
                    if (finalPrimaryAssigneeUserId.HasValue &&
                        finalPrimaryAssigneeUserId != relationshipUserId)
                    {
                        return CompatibilityRelationshipPlanResult.Failure(
                            "TASK_ALREADY_ASSIGNED",
                            "The Task already has a different primary assignee.");
                    }
                    finalPrimaryAssigneeUserId = relationshipUserId;
                    break;

                case TaskAssignmentRole.Reviewer:
                    if (HasOtherRole(TaskAssignmentRole.Reviewer))
                    {
                        return CompatibilityRelationshipPlanResult.Failure(
                            "TASK_ASSIGNMENT_AMBIGUOUS",
                            "A canonical Task can have only one reviewer.");
                    }
                    if (finalReviewerUserId.HasValue &&
                        finalReviewerUserId != relationshipUserId)
                    {
                        return CompatibilityRelationshipPlanResult.Failure(
                            "TASK_ALREADY_ASSIGNED",
                            "The Task already has a different reviewer.");
                    }
                    finalReviewerUserId = relationshipUserId;
                    break;

                case TaskAssignmentRole.Support:
                    if (finalCollaboratorUserIds.Add(relationshipUserId))
                    {
                        addCollaborator = true;
                    }
                    break;
            }
        }

        if (finalPrimaryAssigneeUserId.HasValue &&
            finalPrimaryAssigneeUserId == finalReviewerUserId)
        {
            return CompatibilityRelationshipPlanResult.Failure(
                "TASK_REVIEWER_MUST_DIFFER",
                "Reviewer and primary assignee must differ.");
        }

        if (originalPrimaryAssigneeUserId.HasValue &&
            !finalPrimaryAssigneeUserId.HasValue &&
            CategoryOf(task) is TaskStageCategory.InProgress or TaskStageCategory.Review)
        {
            return CompatibilityRelationshipPlanResult.Failure(
                "TASK_ASSIGNEE_REQUIRED",
                "Active work cannot be unassigned.");
        }

        var primaryChanged = originalPrimaryAssigneeUserId != finalPrimaryAssigneeUserId;
        var reviewerChanged = originalReviewerUserId != finalReviewerUserId;
        var collaboratorChanged = collaboratorToRemove is not null || addCollaborator;
        var canonicalChanged = primaryChanged || reviewerChanged || collaboratorChanged;
        var semanticChange = canonicalChanged
            ? CompatibilityAssignmentSemanticChange(newRole ?? previousRole)
            : null;
        var changedFields = new List<string>();
        if (primaryChanged) changedFields.Add("primaryAssigneeUserId");
        if (reviewerChanged) changedFields.Add("reviewerUserId");
        if (collaboratorChanged) changedFields.Add("collaborators");
        var affectedUserIds = new[]
            {
                originalPrimaryAssigneeUserId,
                finalPrimaryAssigneeUserId,
                originalReviewerUserId,
                finalReviewerUserId,
                relationshipUserId
            }
            .Where(userId => userId.HasValue && userId.Value != Guid.Empty)
            .Select(userId => userId!.Value)
            .Distinct()
            .ToArray();

        return CompatibilityRelationshipPlanResult.Success(new CompatibilityRelationshipPlan(
            relationshipUserId,
            originalPrimaryAssigneeUserId,
            finalPrimaryAssigneeUserId,
            originalReviewerUserId,
            finalReviewerUserId,
            collaboratorToRemove,
            addCollaborator,
            finalCollaboratorUserIds.ToArray(),
            canonicalChanged,
            semanticChange,
            changedFields,
            affectedUserIds));
    }

    private async Task ApplyCompatibilityRelationshipPlanAsync(
        TaskItem task,
        CompatibilityRelationshipPlan plan,
        Guid actorUserId,
        CancellationToken cancellationToken)
    {
        task.PrimaryAssigneeUserId = plan.FinalPrimaryAssigneeUserId;
        task.ReviewerUserId = plan.FinalReviewerUserId;
        if (plan.OriginalReviewerUserId != plan.FinalReviewerUserId &&
            !plan.FinalReviewerUserId.HasValue)
        {
            task.ReviewStatus = TaskReviewStatus.None;
        }

        if (plan.CollaboratorToRemove is not null)
        {
            projects.RemoveCollaborator(plan.CollaboratorToRemove);
        }
        if (plan.AddCollaborator)
        {
            await projects.AddCollaboratorAsync(new WorkItemCollaborator
            {
                TaskItemId = task.Id,
                UserId = plan.RelationshipUserId,
                AddedByUserId = actorUserId,
                AddedAt = clock.UtcNow
            }, cancellationToken);
        }
    }

    private async Task<TaskCommandSaveOutcome> CommitCompatibilityAssignmentAsync(
        TaskItem task,
        Guid actorUserId,
        string auditAction,
        CompatibilityRelationshipPlan plan,
        IEnumerable<Guid> compatibilityAffectedUserIds,
        CancellationToken cancellationToken)
    {
        if (plan.CanonicalChanged)
        {
            await ReconcileCompatibilityAutomaticWatchAsync(
                task,
                plan.FinalCollaboratorUserIds,
                cancellationToken);
        }

        task.VersionNo++;
        await AuditAsync(actorUserId, auditAction, "TaskItem", task.Id, cancellationToken);
        var affectedUserIds = compatibilityAffectedUserIds
            .Concat(plan.AffectedUserIds)
            .Where(userId => userId != Guid.Empty)
            .Distinct()
            .ToArray();
        var changedFields = new[] { "assignments" }
            .Concat(plan.ChangedFields)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        await invalidations.TaskChangedAsync(
            task,
            actorUserId,
            "assignmentChanged",
            changedFields,
            affectedUserIds,
            cancellationToken);

        if (plan is { CanonicalChanged: true, SemanticChange: { } semanticChange })
        {
            await invalidations.TaskAssignmentChangedAsync(
                task,
                actorUserId,
                semanticChange,
                plan.AffectedUserIds,
                cancellationToken);
        }

        if (taskNotifications is not null)
        {
            if (plan.OriginalPrimaryAssigneeUserId != plan.FinalPrimaryAssigneeUserId)
            {
                await taskNotifications.ProduceAsync(new TaskNotificationRecipientRequest(
                    task,
                    TaskNotificationEventKind.PrimaryAssigneeChanged,
                    ActorUserId: actorUserId,
                    PreviousPrimaryAssigneeUserId: plan.OriginalPrimaryAssigneeUserId,
                    NewPrimaryAssigneeUserId: plan.FinalPrimaryAssigneeUserId), cancellationToken);
            }
            if (plan.OriginalReviewerUserId != plan.FinalReviewerUserId &&
                plan.FinalReviewerUserId.HasValue)
            {
                await taskNotifications.ProduceAsync(new TaskNotificationRecipientRequest(
                    task,
                    TaskNotificationEventKind.ReviewerAssigned,
                    ActorUserId: actorUserId,
                    NewReviewerUserId: plan.FinalReviewerUserId), cancellationToken);
            }
        }

        await AdvanceCompatibilityParentForAssignmentAsync(
            task,
            actorUserId,
            auditAction,
            cancellationToken);

        return await taskUnitOfWork.SaveTaskCommandAsync(cancellationToken);
    }

    private async Task AdvanceCompatibilityParentForAssignmentAsync(
        TaskItem child,
        Guid actorUserId,
        string childAction,
        CancellationToken cancellationToken)
    {
        if (!child.ParentTaskItemId.HasValue)
            return;

        var parent = await projects.GetTaskAsync(child.ParentTaskItemId.Value, cancellationToken);
        if (parent is null || parent.DeletedAt.HasValue)
            return;

        parent.VersionNo++;
        await auditLogger.LogAsync(new AuditLogEntry(
            actorUserId,
            "TaskSubtasksChanged",
            "TaskItem",
            parent.Id,
            WorkspaceId: parent.WorkspaceId,
            ProjectId: parent.ProjectId,
            Metadata: new Dictionary<string, object?>
            {
                ["childTaskId"] = child.Id,
                ["childAction"] = childAction,
                ["versionBefore"] = parent.VersionNo - 1
            }), cancellationToken);
        await invalidations.TaskChangedAsync(
            parent,
            actorUserId,
            "subtasksChanged",
            cancellationToken: cancellationToken);
    }

    private async Task ReconcileCompatibilityAutomaticWatchAsync(
        TaskItem task,
        IReadOnlyCollection<Guid> collaboratorUserIds,
        CancellationToken cancellationToken)
    {
        var sources = new Dictionary<Guid, WorkItemWatchAutomaticSource>();
        void Add(Guid? userId, WorkItemWatchAutomaticSource source)
        {
            if (!userId.HasValue || userId.Value == Guid.Empty)
                return;
            sources[userId.Value] = sources.GetValueOrDefault(userId.Value) | source;
        }

        Add(task.CreatedByUserId, WorkItemWatchAutomaticSource.Creator);
        Add(task.PrimaryAssigneeUserId, WorkItemWatchAutomaticSource.PrimaryAssignee);
        Add(task.ReviewerUserId, WorkItemWatchAutomaticSource.Reviewer);
        foreach (var collaboratorUserId in collaboratorUserIds)
        {
            Add(collaboratorUserId, WorkItemWatchAutomaticSource.Collaborator);
        }

        var states = (await projects.ListWatchStatesAsync(task.Id, cancellationToken))
            .ToDictionary(state => state.UserId);
        foreach (var userId in states.Keys.Union(sources.Keys).ToArray())
        {
            if (!states.TryGetValue(userId, out var state))
            {
                var initialSources = sources.GetValueOrDefault(userId);
                await projects.AddWatchStateAsync(new WorkItemWatchState
                {
                    TaskItemId = task.Id,
                    UserId = userId,
                    AutomaticSources = initialSources,
                    IsWatching = TaskWatchStateRules.IsWatching(false, false, initialSources),
                    UpdatedAt = clock.UtcNow,
                    VersionNo = 1
                }, cancellationToken);
                continue;
            }

            var automaticSources = sources.GetValueOrDefault(userId);
            if (state.AutomaticSources == automaticSources &&
                state.IsWatching == TaskWatchStateRules.IsWatching(
                    state.IsManualWatch,
                    state.IsExplicitOptOut,
                    automaticSources))
            {
                continue;
            }

            state.AutomaticSources = automaticSources;
            TaskWatchStateRules.Normalize(state);
            state.UpdatedAt = clock.UtcNow;
            state.VersionNo++;
        }
    }

    private async Task<bool> IsCompatibilityTaskMutableAsync(
        TaskItem task,
        CancellationToken cancellationToken)
    {
        var project = await projects.GetProjectAsync(task.ProjectId, cancellationToken);
        return project is not null &&
               !project.DeletedAt.HasValue &&
               project.Status is not ProjectStatus.Archived and not ProjectStatus.Deleted;
    }

    private static string CompatibilityAssignmentSemanticChange(TaskAssignmentRole? role) => role switch
    {
        TaskAssignmentRole.Assignee => "assigneeChanged",
        TaskAssignmentRole.Reviewer => "reviewerChanged",
        TaskAssignmentRole.Support => "collaboratorChanged",
        _ => throw new InvalidOperationException("A canonical compatibility relationship change requires a supported role.")
    };

    private ITaskRelationshipTargetPolicy RelationshipTargets =>
        relationshipTargets ?? new TaskRelationshipTargetPolicy(projects, users, projectAuthorization);

    private sealed record CompatibilityRelationshipPlan(
        Guid RelationshipUserId,
        Guid? OriginalPrimaryAssigneeUserId,
        Guid? FinalPrimaryAssigneeUserId,
        Guid? OriginalReviewerUserId,
        Guid? FinalReviewerUserId,
        WorkItemCollaborator? CollaboratorToRemove,
        bool AddCollaborator,
        IReadOnlyList<Guid> FinalCollaboratorUserIds,
        bool CanonicalChanged,
        string? SemanticChange,
        IReadOnlyList<string> ChangedFields,
        IReadOnlyList<Guid> AffectedUserIds);

    private sealed record CompatibilityRelationshipPlanResult(
        CompatibilityRelationshipPlan? Plan,
        string? Error)
    {
        public static CompatibilityRelationshipPlanResult Success(CompatibilityRelationshipPlan plan) =>
            new(plan, null);

        public static CompatibilityRelationshipPlanResult Failure(string code, string message) =>
            new(null, $"{code}|{message}");
    }

    private static Result<T> CompatibilityAssignmentFailure<T>(string code, string message) =>
        Result<T>.Failure($"{code}|{message}");

    private static Result CompatibilityAssignmentFailure(string code, string message) =>
        Result.Failure($"{code}|{message}");

    private static Result TaskConflict() =>
        Result.Failure(new ApplicationErrorDetail("TASK_STALE_VERSION", "Task has changed. Refetch and retry."));

    private static Result<T> TaskConflict<T>() =>
        Result<T>.Failure(new ApplicationErrorDetail("TASK_STALE_VERSION", "Task has changed. Refetch and retry."));

    private static Result<T> AssignmentConflict<T>() =>
        Result<T>.Failure(new ApplicationErrorDetail("TASK_ALREADY_ASSIGNED", "User already has this assignment role."));

    private static Result<T> GeneralTaskConflict<T>() =>
        Result<T>.Failure(new ApplicationErrorDetail("TASK_CONFLICT", "The task could not be updated. Refetch and retry."));

    private static Result ProjectConflict() =>
        Result.Failure(new ApplicationErrorDetail(
            "PROJECT_CONFLICT",
            "Project state has changed. Refetch and retry."));

    private static Result<T> ProjectConflict<T>() =>
        Result<T>.Failure(new ApplicationErrorDetail(
            "PROJECT_CONFLICT",
            "Project state has changed. Refetch and retry."));

    // This is the generated PostgreSQL index name for the unique TaskAssignment
    // identity configured in TaskAssignmentConfiguration.  Do not map other
    // database unique constraints to the assignment-specific error.
    private static bool IsassignmentIdentityConstraint(string? constraintName) =>
        string.Equals(constraintName, "IX_task_assignments_TenantId_TaskItemId_UserId_Role", StringComparison.Ordinal);

    private static TaskDependencyResponse ToDependency(
        TaskDependency dependency,
        long successorVersion,
        bool editable,
        IReadOnlyList<GanttWarningResponse>? additionalWarnings = null)
    {
        var warnings = additionalWarnings?.ToList() ?? [];
        if (dependency.DependencyType != TaskDependencyType.FinishToStart)
        {
            warnings.Add(new GanttWarningResponse(
                    "LEGACY_DEPENDENCY_TYPE",
                    "This legacy dependency type is read-only; new authoring supports Finish-to-Start only.",
                    GanttWarningSeverity.Warning,
                    "Dependency",
                    dependency.Id,
                    "type"));
        }
        return new TaskDependencyResponse(
            dependency.Id,
            dependency.PredecessorTaskItemId,
            dependency.SuccessorTaskItemId,
            dependency.DependencyType,
            dependency.CreatedAt,
            successorVersion,
            editable,
            warnings
                .Distinct()
                .OrderBy(warning => warning.Code, StringComparer.Ordinal)
                .ToList());
    }

    private static IReadOnlyList<GanttWarningResponse> DependencyDateWarnings(
        TaskDependency dependency,
        IReadOnlyList<TaskItem> projectTasks)
    {
        if (dependency.DependencyType != TaskDependencyType.FinishToStart)
            return [];
        var tasksById = projectTasks
            .Where(task => !task.DeletedAt.HasValue)
            .ToDictionary(task => task.Id);
        if (!tasksById.TryGetValue(dependency.PredecessorTaskItemId, out var predecessor) ||
            !tasksById.TryGetValue(dependency.SuccessorTaskItemId, out var successor))
        {
            return [];
        }

        var predecessorDates = ParentTaskDerivedValuesCalculator.Calculate(predecessor, projectTasks, CategoryOf);
        var successorDates = ParentTaskDerivedValuesCalculator.Calculate(successor, projectTasks, CategoryOf);
        if (!predecessorDates.PlannedEndDate.HasValue ||
            !successorDates.PlannedStartDate.HasValue ||
            predecessorDates.PlannedEndDate.Value <= successorDates.PlannedStartDate.Value)
        {
            return [];
        }

        return
        [
            new GanttWarningResponse(
                "DEPENDENCY_VIOLATION",
                "The predecessor is planned to finish after the successor starts. No dates were changed automatically.",
                GanttWarningSeverity.Warning,
                "Dependency",
                dependency.Id,
                "plannedStartDate")
        ];
    }

    private async Task<Result<T>> RejectVisibleDependencyAsync<T>(
        Guid actorUserId,
        TaskItem visibleSuccessor,
        string reasonCode,
        string message,
        CancellationToken cancellationToken)
    {
        await LogDependencyRejectionAsync(
            actorUserId,
            visibleSuccessor,
            reasonCode,
            cancellationToken);
        return DependencyFailure<T>(reasonCode, message);
    }

    private async Task<Result> RejectVisibleDependencyAsync(
        Guid actorUserId,
        TaskItem visibleSuccessor,
        string reasonCode,
        string message,
        CancellationToken cancellationToken)
    {
        await LogDependencyRejectionAsync(
            actorUserId,
            visibleSuccessor,
            reasonCode,
            cancellationToken);
        return DependencyFailure(reasonCode, message);
    }

    private async Task LogDependencyRejectionAsync(
        Guid actorUserId,
        TaskItem visibleSuccessor,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        // Rejection audit deliberately omits the supplied predecessor/dependency
        // identifier and every title. Unknown and cross-Project neighbors remain
        // indistinguishable outside the authorized Project boundary.
        await auditLogger.LogAsync(new AuditLogEntry(
            actorUserId,
            "TaskDependencyMutationRejected",
            "TaskDependency",
            null,
            "Dependency mutation rejected.",
            WorkspaceId: visibleSuccessor.WorkspaceId,
            ProjectId: visibleSuccessor.ProjectId,
            Metadata: new Dictionary<string, object?>
            {
                ["reasonCode"] = reasonCode
            }), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private static Result<T> DependencyFailure<T>(string code, string message) =>
        Result<T>.Failure($"{code}|{message}");

    private static Result DependencyFailure(string code, string message) =>
        Result.Failure($"{code}|{message}");

    private enum DependencyCycleCheck
    {
        None,
        Cycle,
        LimitExceeded
    }

    private static CommentResponse ToComment(Comment comment)
    {
        return new CommentResponse(comment.Id, comment.TargetType, comment.TargetId, comment.AuthorUserId, comment.Body, comment.CreatedAt, comment.UpdatedAt);
    }
}
