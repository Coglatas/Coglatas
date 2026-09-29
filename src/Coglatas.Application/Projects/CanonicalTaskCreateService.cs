using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Realtime;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;

namespace Coglatas.Application.Projects;

/// <summary>
/// Retry-safe, Project-scoped Task creation for the Task-create UI.  This is
/// intentionally independent of the legacy ProjectService create command: all
/// records that make the created Task observable are staged under one
/// idempotency-owned transaction.
/// </summary>
public sealed class CanonicalTaskCreateService(
    IProjectRepository projects,
    IUserRepository users,
    ITaskExecutionScopeRepository executionScopes,
    IProjectAuthorizationService projectAuthorization,
    ITaskAuthorizationService taskAuthorization,
    ITaskRelationshipTargetPolicy relationshipTargets,
    ICurrentUser currentUser,
    ICurrentTenant currentTenant,
    IClock clock,
    IAuditLogger audit,
    IBusinessInvalidationPublisher invalidations,
    ITaskNotificationProducer taskNotifications,
    ICreateIdempotencyCoordinator idempotency) : ICanonicalTaskCreateService
{
    private const string TaskCreateOperation = "Task.Create.v1";
    private const int MaximumTitleLength = 240;
    private const int MaximumDescriptionLength = 8_000;

    public async Task<Result<TaskCreateOptionsResponse>> GetCreateOptionsAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var scope = await ResolveProjectScopeAsync(projectId, requireCreate: false, cancellationToken);
        if (scope.Error is not null)
            return Result<TaskCreateOptionsResponse>.Failure(scope.Error);

        var value = scope.Value!;
        var milestones = (await projects.ListMilestonesAsync(value.Project.Id, cancellationToken))
            .Where(milestone => !milestone.DeletedAt.HasValue)
            .OrderBy(milestone => milestone.SortOrder)
            .ThenBy(milestone => milestone.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(milestone => milestone.Id)
            .Select(milestone => new TaskCreateMilestoneOptionResponse(milestone.Id, milestone.Name))
            .ToArray();
        var assignees = value.CanManageProject
            ? await GetEligibleAssigneesAsync(value.Project.Id, cancellationToken)
            : [];
        var projectScope = await executionScopes.GetProjectScopeAsync(value.Project.Id, cancellationToken);

        return Result<TaskCreateOptionsResponse>.Success(new TaskCreateOptionsResponse(
            value.Project.Id,
            value.Project.WorkspaceId,
            value.Project.Name,
            value.CanCreateTask,
            value.CanManageProject,
            milestones,
            assignees,
            new TaskCreateProjectScopeResponse(
                new TaskExecutionSourcePolicyResponse(
                    projectScope?.WebEnabled ?? false,
                    projectScope?.ProjectFilesEnabled ?? false),
                projectScope?.VersionNo ?? 0,
                value.CanManageProject)));
    }

    public async Task<Result<CanonicalTaskCreateResponse>> CreateAsync(
        Guid projectId,
        CanonicalCreateTaskRequest request,
        string? clientRequestIdentity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var scope = await ResolveProjectScopeAsync(projectId, requireCreate: true, cancellationToken);
        if (scope.Error is not null)
            return Result<CanonicalTaskCreateResponse>.Failure(scope.Error);

        var normalized = ValidateAndNormalize(request);
        if (normalized.Error is not null)
            return Result<CanonicalTaskCreateResponse>.Failure(normalized.Error);

        if (clientRequestIdentity is null)
        {
            return Failure(
                "MissingIdempotencyKey",
                "An Idempotency-Key header is required.",
                "header.Idempotency-Key");
        }
        if (!IsValidClientRequestIdentity(clientRequestIdentity))
        {
            return Failure(
                "InvalidIdempotencyKey",
                "The Idempotency-Key header is invalid.",
                "header.Idempotency-Key");
        }

        var actor = scope.Value!.ActorUserId;
        var project = scope.Value.Project;
        var task = NewTask(project, actor, normalized.Value!);

        IdempotentCreateResult<TaskItem> result;
        try
        {
            result = await idempotency.ExecuteAsync(
                new CreateIdempotencyContext(
                    project.TenantId,
                    actor,
                    TaskCreateOperation,
                    clientRequestIdentity,
                    CreateRequestFingerprint(project.Id, normalized.Value!),
                    "TaskItem",
                    task.Id),
                async token =>
                {
                    // Re-authorize and revalidate mutable selection targets at
                    // the transaction's creation boundary.  The options
                    // response is advisory only and cannot authorize a write.
                    var stagedScope = await ResolveProjectScopeAsync(projectId, requireCreate: true, token);
                    if (stagedScope.Error is not null)
                        throw new CanonicalTaskCreateValidationException(stagedScope.Error);

                    var stagedValidation = await ValidateReferencesAndAuthorityAsync(
                        stagedScope.Value!,
                        normalized.Value!,
                        token);
                    if (stagedValidation is not null)
                        throw new CanonicalTaskCreateValidationException(stagedValidation);

                    var placement = await TaskInitialPlacement.ApplyAsync(projects, task, token);
                    if (!placement.IsSuccess || !task.WorkflowStageId.HasValue)
                    {
                        throw new CanonicalTaskCreateValidationException(new ApplicationErrorDetail(
                            "InvalidStateTransition",
                            "The Project workflow cannot accept a new Task."));
                    }

                    await projects.AddTaskAsync(task, token);
                    await AddAutomaticCreateWatchesAsync(task, actor, token);

                    TaskExecutionScopeOverride? scopeOverride = null;
                    if (normalized.Value!.SourceScopeMode == TaskCreateSourceScopeMode.TaskOverride)
                    {
                        var policy = normalized.Value.TaskOverridePolicy!;
                        scopeOverride = new TaskExecutionScopeOverride
                        {
                            TenantId = task.TenantId,
                            WorkspaceId = task.WorkspaceId,
                            ProjectId = task.ProjectId,
                            TaskItemId = task.Id,
                            TaskItem = task,
                            WebEnabled = policy.WebEnabled,
                            ProjectFilesEnabled = policy.ProjectFilesEnabled,
                            VersionNo = 1,
                            UpdatedByUserId = actor
                        };
                        task.ExecutionScopeOverride = scopeOverride;
                        await executionScopes.AddTaskOverrideAsync(scopeOverride, token);
                    }

                    await audit.LogAsync(new AuditLogEntry(
                        actor,
                        "TaskCreated",
                        "TaskItem",
                        task.Id,
                        WorkspaceId: task.WorkspaceId,
                        ProjectId: task.ProjectId,
                        TenantId: task.TenantId,
                        Metadata: new Dictionary<string, object?>
                        {
                            ["milestoneSelected"] = task.MilestoneId.HasValue,
                            ["primaryAssigneeSelected"] = task.PrimaryAssigneeUserId.HasValue,
                            ["sourceScopeMode"] = normalized.Value.SourceScopeMode.ToString()
                        }), token);

                    if (scopeOverride is not null)
                    {
                        await audit.LogAsync(new AuditLogEntry(
                            actor,
                            "TaskExecutionScopeOverrideSet",
                            "TaskExecutionScopeOverride",
                            scopeOverride.Id,
                            WorkspaceId: task.WorkspaceId,
                            ProjectId: task.ProjectId,
                            TenantId: task.TenantId,
                            Metadata: new Dictionary<string, object?>
                            {
                                ["webEnabled"] = scopeOverride.WebEnabled,
                                ["projectFilesEnabled"] = scopeOverride.ProjectFilesEnabled,
                                ["scopeVersion"] = scopeOverride.VersionNo
                            }), token);
                    }

                    var affectedUserIds = new[] { actor, task.PrimaryAssigneeUserId ?? Guid.Empty }
                        .Where(userId => userId != Guid.Empty)
                        .Distinct()
                        .ToArray();
                    await invalidations.TaskChangedAsync(
                        task,
                        actor,
                        "created",
                        ["created"],
                        affectedUserIds,
                        token);

                    if (task.PrimaryAssigneeUserId.HasValue)
                    {
                        await invalidations.TaskAssignmentChangedAsync(
                            task,
                            actor,
                            "assigneeInitialized",
                            affectedUserIds,
                            token);
                        await taskNotifications.ProduceAsync(new TaskNotificationRecipientRequest(
                            task,
                            TaskNotificationEventKind.PrimaryAssigneeChanged,
                            ActorUserId: actor,
                            PreviousPrimaryAssigneeUserId: null,
                            NewPrimaryAssigneeUserId: task.PrimaryAssigneeUserId), token);
                    }

                    return task;
                },
                async (resourceId, token) =>
                {
                    // An idempotent replay must never become a way to recover a
                    // Task after the caller has lost the create authority.
                    var replayScope = await ResolveProjectScopeAsync(projectId, requireCreate: true, token);
                    if (replayScope.Error is not null)
                        return null;

                    var committed = await projects.GetTaskAsync(resourceId, token);
                    return committed is { DeletedAt: null } &&
                           committed.TenantId == replayScope.Value!.TenantId &&
                           committed.ProjectId == replayScope.Value.Project.Id
                        ? committed
                        : null;
                },
                cancellationToken);
        }
        catch (CanonicalTaskCreateValidationException exception)
        {
            return Result<CanonicalTaskCreateResponse>.Failure(exception.Detail);
        }
        catch (RequiredOutboxStagingException)
        {
            return Failure("DependencyUnavailable", "Task creation is temporarily unavailable.");
        }

        return result.Disposition switch
        {
            IdempotentCreateDisposition.Created or IdempotentCreateDisposition.Replayed
                when result.Value is not null => Result<CanonicalTaskCreateResponse>.Success(
                    ToResponse(
                        result.Value,
                        await executionScopes.GetTaskOverrideAsync(result.Value.Id, cancellationToken))),
            IdempotentCreateDisposition.RequestMismatch => Failure(
                "IdempotencyConflict",
                "The Idempotency-Key was already used with a different Task request.",
                "header.Idempotency-Key"),
            _ => NotFound()
        };
    }

    private async Task<ProjectCreateScopeResolution> ResolveProjectScopeAsync(
        Guid projectId,
        bool requireCreate,
        CancellationToken cancellationToken)
    {
        if (!TryActor(out var actor))
        {
            return ProjectCreateScopeResolution.Failed(new ApplicationErrorDetail(
                "AuthenticationRequired",
                "Authentication is required."));
        }
        if (currentTenant is not { IsAvailable: true, IsPlatformScope: false } || currentTenant.TenantId == Guid.Empty)
        {
            return ProjectCreateScopeResolution.Failed(new ApplicationErrorDetail(
                "TenantMembershipRequired",
                "An active Tenant membership is required."));
        }
        if (projectId == Guid.Empty)
            return ProjectCreateScopeResolution.Failed(NotFoundDetail());

        var project = await projects.GetProjectAsync(projectId, cancellationToken);
        if (project is null ||
            project.TenantId != currentTenant.TenantId ||
            project.DeletedAt.HasValue ||
            project.Status is ProjectStatus.Archived or ProjectStatus.Deleted ||
            !await projectAuthorization.CanViewProject(actor, projectId, cancellationToken))
        {
            return ProjectCreateScopeResolution.Failed(NotFoundDetail());
        }

        var canCreate = await taskAuthorization.CanCreateTask(actor, projectId, cancellationToken);
        if (requireCreate && !canCreate)
        {
            return ProjectCreateScopeResolution.Failed(new ApplicationErrorDetail(
                "CapabilityDenied",
                "You are not allowed to create Tasks in this Project.",
                Target: "project"));
        }

        var canManage = await projectAuthorization.CanManageProject(actor, projectId, cancellationToken);
        return ProjectCreateScopeResolution.Succeeded(new ProjectCreateScope(
            actor,
            currentTenant.TenantId,
            project,
            canCreate,
            canManage));
    }

    private async Task<IReadOnlyList<TaskCreateAssigneeOptionResponse>> GetEligibleAssigneesAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var members = await projects.ListMembersAsync(projectId, cancellationToken);
        var activeUsers = (await users.GetActiveByIdsAsync(
                members.Select(member => member.UserId).Distinct().ToArray(),
                cancellationToken))
            .ToDictionary(user => user.Id);
        var candidates = new List<TaskCreateAssigneeOptionResponse>();
        foreach (var member in members
                     .OrderBy(member => activeUsers.GetValueOrDefault(member.UserId)?.DisplayName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(member => member.UserId))
        {
            if (!activeUsers.TryGetValue(member.UserId, out var user) ||
                !await relationshipTargets.IsEligibleAsync(projectId, user.Id, cancellationToken))
            {
                continue;
            }

            candidates.Add(new TaskCreateAssigneeOptionResponse(user.Id, user.DisplayName));
        }

        return candidates;
    }

    private async Task<ApplicationErrorDetail?> ValidateReferencesAndAuthorityAsync(
        ProjectCreateScope scope,
        NormalizedTaskCreateRequest request,
        CancellationToken cancellationToken)
    {
        if (request.MilestoneId.HasValue)
        {
            var milestone = await projects.GetMilestoneAsync(request.MilestoneId.Value, cancellationToken);
            if (milestone is null ||
                milestone.ProjectId != scope.Project.Id ||
                milestone.DeletedAt.HasValue)
            {
                return new ApplicationErrorDetail(
                    "NotFound",
                    "The requested resource was not found.",
                    Target: "body.milestoneId");
            }
        }

        if (request.PrimaryAssigneeUserId.HasValue)
        {
            if (!scope.CanManageProject)
            {
                return new ApplicationErrorDetail(
                    "CapabilityDenied",
                    "You are not allowed to select an initial primary assignee.",
                    Target: "body.primaryAssigneeUserId");
            }
            if (!await relationshipTargets.IsEligibleAsync(
                    scope.Project.Id,
                    request.PrimaryAssigneeUserId.Value,
                    cancellationToken))
            {
                return new ApplicationErrorDetail(
                    "ValidationFailed",
                    "The selected primary assignee is not available for this Task.",
                    Target: "body.primaryAssigneeUserId");
            }
        }

        if (request.SourceScopeMode == TaskCreateSourceScopeMode.TaskOverride && !scope.CanManageProject)
        {
            return new ApplicationErrorDetail(
                "CapabilityDenied",
                "You are not allowed to set a Task source override.",
                Target: "body.sourceScopeMode");
        }

        return null;
    }

    private static NormalizedTaskCreateRequestResult ValidateAndNormalize(CanonicalCreateTaskRequest request)
    {
        var title = request.Title?.Trim();
        if (string.IsNullOrWhiteSpace(title) || title.Length > MaximumTitleLength)
        {
            return NormalizedTaskCreateRequestResult.Failed(new ApplicationErrorDetail(
                "ValidationFailed",
                $"Task title must be between 1 and {MaximumTitleLength} characters.",
                Target: "body.title"));
        }

        var description = NormalizeOptional(request.Description);
        if (description?.Length > MaximumDescriptionLength)
        {
            return NormalizedTaskCreateRequestResult.Failed(new ApplicationErrorDetail(
                "ValidationFailed",
                $"Task description must not exceed {MaximumDescriptionLength} characters.",
                Target: "body.description"));
        }
        if (!Enum.IsDefined(typeof(TaskPriority), request.Priority))
        {
            return NormalizedTaskCreateRequestResult.Failed(new ApplicationErrorDetail(
                "ValidationFailed",
                "Task priority is invalid.",
                Target: "body.priority"));
        }
        if (request.MilestoneId == Guid.Empty)
        {
            return NormalizedTaskCreateRequestResult.Failed(new ApplicationErrorDetail(
                "ValidationFailed",
                "MilestoneId must be a non-empty identifier when supplied.",
                Target: "body.milestoneId"));
        }
        if (request.PrimaryAssigneeUserId == Guid.Empty)
        {
            return NormalizedTaskCreateRequestResult.Failed(new ApplicationErrorDetail(
                "ValidationFailed",
                "PrimaryAssigneeUserId must be a non-empty identifier when supplied.",
                Target: "body.primaryAssigneeUserId"));
        }
        if (request.StartDate.HasValue && request.DueDate.HasValue && request.DueDate < request.StartDate)
        {
            return NormalizedTaskCreateRequestResult.Failed(new ApplicationErrorDetail(
                "ValidationFailed",
                "Task due date cannot be before the start date.",
                Target: "body.dueDate"));
        }

        var briefValidation = TaskBriefText.Validate(request.Goal, request.Deliverable, request.Constraints);
        if (briefValidation is not null)
        {
            return NormalizedTaskCreateRequestResult.Failed(briefValidation with
            {
                Target = $"body.{briefValidation.Target}"
            });
        }
        if (!Enum.IsDefined(typeof(TaskCreateSourceScopeMode), request.SourceScopeMode))
        {
            return NormalizedTaskCreateRequestResult.Failed(new ApplicationErrorDetail(
                "ValidationFailed",
                "Task source scope mode is invalid.",
                Target: "body.sourceScopeMode"));
        }
        if (request.SourceScopeMode == TaskCreateSourceScopeMode.Inherit && request.TaskOverridePolicy is not null)
        {
            return NormalizedTaskCreateRequestResult.Failed(new ApplicationErrorDetail(
                "ValidationFailed",
                "A Task source override policy is only allowed when TaskOverride is selected.",
                Target: "body.taskOverridePolicy"));
        }
        if (request.SourceScopeMode == TaskCreateSourceScopeMode.TaskOverride && request.TaskOverridePolicy is null)
        {
            return NormalizedTaskCreateRequestResult.Failed(new ApplicationErrorDetail(
                "ValidationFailed",
                "A complete Task source override policy is required.",
                Target: "body.taskOverridePolicy"));
        }

        return NormalizedTaskCreateRequestResult.Succeeded(new NormalizedTaskCreateRequest(
            title,
            description,
            request.Priority,
            request.MilestoneId,
            request.StartDate,
            request.DueDate,
            TaskBriefText.Normalize(request.Goal),
            TaskBriefText.Normalize(request.Deliverable),
            TaskBriefText.Normalize(request.Constraints),
            request.PrimaryAssigneeUserId,
            request.SourceScopeMode,
            request.TaskOverridePolicy));
    }

    private static TaskItem NewTask(
        Project project,
        Guid actor,
        NormalizedTaskCreateRequest request) => new()
    {
        TenantId = project.TenantId,
        WorkspaceId = project.WorkspaceId,
        ProjectId = project.Id,
        MilestoneId = request.MilestoneId,
        Title = request.Title,
        Description = request.Description,
        BriefGoal = request.Goal,
        BriefDeliverable = request.Deliverable,
        BriefConstraints = request.Constraints,
        Priority = request.Priority,
        StartDate = request.StartDate,
        DueDate = request.DueDate,
        PlannedStartDate = request.StartDate,
        PlannedEndDate = request.DueDate,
        PrimaryAssigneeUserId = request.PrimaryAssigneeUserId,
        CreatedByUserId = actor,
        VersionNo = 1
    };

    private async Task AddAutomaticCreateWatchesAsync(
        TaskItem task,
        Guid actor,
        CancellationToken cancellationToken)
    {
        var sources = new Dictionary<Guid, WorkItemWatchAutomaticSource>
        {
            [actor] = WorkItemWatchAutomaticSource.Creator
        };
        if (task.PrimaryAssigneeUserId is { } assignee && assignee != Guid.Empty)
        {
            sources[assignee] = sources.GetValueOrDefault(assignee) |
                                WorkItemWatchAutomaticSource.PrimaryAssignee;
        }

        foreach (var (userId, automaticSources) in sources)
        {
            await projects.AddWatchStateAsync(new WorkItemWatchState
            {
                TenantId = task.TenantId,
                TaskItemId = task.Id,
                UserId = userId,
                AutomaticSources = automaticSources,
                IsWatching = TaskWatchStateRules.IsWatching(false, false, automaticSources),
                UpdatedAt = clock.UtcNow,
                VersionNo = 1
            }, cancellationToken);
        }
    }

    private static CanonicalTaskCreateResponse ToResponse(
        TaskItem task,
        TaskExecutionScopeOverride? scopeOverride) => new(
        task.Id,
        task.ProjectId,
        task.WorkspaceId,
        task.MilestoneId,
        task.PrimaryAssigneeUserId,
        task.Title,
        task.Priority,
        task.Status,
        task.WorkflowStageId ?? Guid.Empty,
        task.VersionNo,
        scopeOverride is null
            ? TaskCreateSourceScopeMode.Inherit
            : TaskCreateSourceScopeMode.TaskOverride,
        scopeOverride is null
            ? null
            : new TaskExecutionSourcePolicyResponse(
                scopeOverride.WebEnabled,
                scopeOverride.ProjectFilesEnabled));

    private static string CreateRequestFingerprint(
        Guid projectId,
        NormalizedTaskCreateRequest request)
    {
        var policy = request.TaskOverridePolicy;
        var canonical = string.Join("|",
            projectId.ToString("N"),
            EncodeFingerprintPart(request.Title),
            EncodeFingerprintPart(request.Description),
            ((int)request.Priority).ToString(CultureInfo.InvariantCulture),
            request.MilestoneId?.ToString("N") ?? string.Empty,
            request.StartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
            request.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
            EncodeFingerprintPart(request.Goal),
            EncodeFingerprintPart(request.Deliverable),
            EncodeFingerprintPart(request.Constraints),
            request.PrimaryAssigneeUserId?.ToString("N") ?? string.Empty,
            ((int)request.SourceScopeMode).ToString(CultureInfo.InvariantCulture),
            policy is null ? string.Empty : policy.WebEnabled ? "1" : "0",
            policy is null ? string.Empty : policy.ProjectFilesEnabled ? "1" : "0"
        );
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string EncodeFingerprintPart(string? value) =>
        value is null ? "-1:" : $"{Encoding.UTF8.GetByteCount(value)}:{value}";

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static bool IsValidClientRequestIdentity(string? value) =>
        value is { Length: >= 8 and <= 128 } &&
        !string.IsNullOrWhiteSpace(value) &&
        value.All(character => character is >= ' ' and <= '~');

    private bool TryActor(out Guid actor)
    {
        actor = currentUser.UserId ?? Guid.Empty;
        return currentUser.IsAuthenticated && actor != Guid.Empty;
    }

    private static Result<CanonicalTaskCreateResponse> NotFound() =>
        Result<CanonicalTaskCreateResponse>.Failure(NotFoundDetail());

    private static ApplicationErrorDetail NotFoundDetail() =>
        new("NotFound", "The requested resource was not found.");

    private static Result<CanonicalTaskCreateResponse> Failure(
        string code,
        string message,
        string? target = null) =>
        Result<CanonicalTaskCreateResponse>.Failure(new ApplicationErrorDetail(code, message, Target: target));

    private sealed record ProjectCreateScope(
        Guid ActorUserId,
        Guid TenantId,
        Project Project,
        bool CanCreateTask,
        bool CanManageProject);

    private sealed record ProjectCreateScopeResolution(
        ProjectCreateScope? Value,
        ApplicationErrorDetail? Error)
    {
        public static ProjectCreateScopeResolution Succeeded(ProjectCreateScope value) => new(value, null);
        public static ProjectCreateScopeResolution Failed(ApplicationErrorDetail error) => new(null, error);
    }

    private sealed record NormalizedTaskCreateRequest(
        string Title,
        string? Description,
        TaskPriority Priority,
        Guid? MilestoneId,
        DateOnly? StartDate,
        DateOnly? DueDate,
        string? Goal,
        string? Deliverable,
        string? Constraints,
        Guid? PrimaryAssigneeUserId,
        TaskCreateSourceScopeMode SourceScopeMode,
        TaskCreateSourceScopePolicyRequest? TaskOverridePolicy);

    private sealed record NormalizedTaskCreateRequestResult(
        NormalizedTaskCreateRequest? Value,
        ApplicationErrorDetail? Error)
    {
        public static NormalizedTaskCreateRequestResult Succeeded(NormalizedTaskCreateRequest value) => new(value, null);
        public static NormalizedTaskCreateRequestResult Failed(ApplicationErrorDetail error) => new(null, error);
    }

    private sealed class CanonicalTaskCreateValidationException(ApplicationErrorDetail detail) : Exception
    {
        public ApplicationErrorDetail Detail { get; } = detail;
    }
}
