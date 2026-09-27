using System.Security.Cryptography;
using System.Text;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Files;
using Coglatas.Application.Realtime;
using Coglatas.Application.Tenancy;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;

namespace Coglatas.Application.Projects;

/// <summary>
/// Owns the server-authoritative source-policy and durable acceptance boundary
/// for Task execution. Policy V2 adds Allow/Prioritize/Exclude item rules while
/// retaining the V1 booleans as a compatibility projection.
/// </summary>
public sealed class TaskExecutionScopeService(
    IProjectRepository projects,
    IProjectAuthorizationService projectAuthorization,
    ITaskExecutionScopeRepository executionScopes,
    IResearchPlanRepository researchPlans,
    ICurrentUser currentUser,
    IClock clock,
    IAuditLogger audit,
    IBusinessInvalidationPublisher invalidations,
    ITaskCommandUnitOfWork unitOfWork,
    ICreateIdempotencyCoordinator idempotency,
    IFileAuthorizationService? fileAuthorization = null,
    ITenantAuthorizationService? tenantAuthorization = null) : ITaskExecutionScopeService
{
    private readonly ProjectAuthorizationLookup _authorizationLookup = new(projects, projectAuthorization, currentUser);

    private const string RunOperation = "TaskExecution.Run.v1";

    public async Task<Result<ProjectExecutionScopeResponse>> GetProjectScopeAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var project = await _authorizationLookup.VisibleProjectAsync(projectId, cancellationToken);
        if (project is null)
            return NotFound<ProjectExecutionScopeResponse>();

        var scope = await executionScopes.GetProjectScopeAsync(project.Id, cancellationToken);
        var canManage = await _authorizationLookup.CanManageAsync(project.Id, cancellationToken);
        var canManageConnectedApps = canManage && await CanManageTenantAsync(project.TenantId, cancellationToken);
        var policy = await ProjectPolicyAsync(scope, cancellationToken);
        return Result<ProjectExecutionScopeResponse>.Success(
            ToProjectResponse(scope, RedactProjectPolicy(policy, canManage, canManageConnectedApps), canManage));
    }

    public async Task<Result<ProjectExecutionScopeResponse>> UpdateProjectScopeAsync(
        Guid projectId,
        UpdateProjectExecutionScopeRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.ExpectedVersion < 0)
            return Invalid<ProjectExecutionScopeResponse>("expectedVersion", "Expected version must be zero or a positive integer.");

        if (!TryRequestedPolicy(request.PolicyV2, request.WebEnabled, request.ProjectFilesEnabled, out var requestedPolicy, out var policyError))
            return Result<ProjectExecutionScopeResponse>.Failure(policyError!);

        var project = await _authorizationLookup.ManagedProjectAsync(projectId, cancellationToken);
        if (project is null)
            return NotFound<ProjectExecutionScopeResponse>();

        if (!await CanUseProjectItemRulesAsync(project, requestedPolicy, cancellationToken))
            return Invalid<ProjectExecutionScopeResponse>("policyV2.items", "One or more source rules are not available in the current authorization scope.");

        var actor = _authorizationLookup.Actor();
        var scope = await executionScopes.GetProjectScopeForUpdateAsync(project.Id, cancellationToken);
        if (scope is null)
        {
            if (request.ExpectedVersion != 0)
                return Stale<ProjectExecutionScopeResponse>();

            scope = NewProjectScope(project, actor, requestedPolicy.WebEnabled, requestedPolicy.ProjectFilesEnabled);
            await executionScopes.AddProjectScopeAsync(scope, cancellationToken);
        }
        else
        {
            if (scope.VersionNo != request.ExpectedVersion)
                return Stale<ProjectExecutionScopeResponse>();

            scope.WebEnabled = requestedPolicy.WebEnabled;
            scope.ProjectFilesEnabled = requestedPolicy.ProjectFilesEnabled;
            scope.VersionNo = NextVersion(scope.VersionNo);
            scope.UpdatedByUserId = actor;
        }

        executionScopes.StageSourcePolicyDocument(new TaskExecutionSourcePolicyDocument(
            TaskExecutionSourcePolicyOwnerType.Project,
            project.Id,
            project.TenantId,
            project.WorkspaceId,
            project.Id,
            null,
            scope.VersionNo,
            null,
            requestedPolicy));

        await audit.LogAsync(new AuditLogEntry(
            actor,
            "ProjectExecutionScopeChanged",
            "ProjectExecutionScope",
            scope.Id,
            WorkspaceId: project.WorkspaceId,
            ProjectId: project.Id,
            Metadata: ScopeMetadata(requestedPolicy, scope.VersionNo)), cancellationToken);
        await invalidations.ProjectChangedAsync(project, actor, "executionScopeChanged", cancellationToken);

        var save = await unitOfWork.SaveTaskCommandAsync(cancellationToken);
        if (!save.IsSaved)
            return Stale<ProjectExecutionScopeResponse>();

        var canManageConnectedApps = await CanManageTenantAsync(project.TenantId, cancellationToken);
        return Result<ProjectExecutionScopeResponse>.Success(
            ToProjectResponse(scope, RedactProjectPolicy(requestedPolicy, true, canManageConnectedApps), true));
    }

    public async Task<Result<TaskExecutionScopeResponse>> GetTaskScopeAsync(
        Guid taskItemId,
        CancellationToken cancellationToken = default)
    {
        var task = await _authorizationLookup.VisibleTaskAsync(taskItemId, cancellationToken);
        if (task is null)
            return NotFound<TaskExecutionScopeResponse>();

        return Result<TaskExecutionScopeResponse>.Success(
            await BuildTaskScopeResponseAsync(task, await _authorizationLookup.CanManageAsync(task.ProjectId, cancellationToken), cancellationToken));
    }

    public async Task<Result<TaskExecutionScopeResponse>> UpdateTaskOverrideAsync(
        Guid taskItemId,
        UpdateTaskExecutionScopeOverrideRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.ExpectedVersion < 0)
            return Invalid<TaskExecutionScopeResponse>("expectedVersion", "Expected version must be zero or a positive integer.");

        if (!TryRequestedPolicy(request.PolicyV2, request.WebEnabled, request.ProjectFilesEnabled, out var requestedPolicy, out var policyError))
            return Result<TaskExecutionScopeResponse>.Failure(policyError!);

        var task = await _authorizationLookup.ManagedTaskAsync(taskItemId, cancellationToken);
        if (task is null)
            return NotFound<TaskExecutionScopeResponse>();

        if (!await CanUseTaskItemRulesAsync(task, requestedPolicy, cancellationToken))
            return Invalid<TaskExecutionScopeResponse>("policyV2.items", "One or more source rules are not available in the current authorization scope.");

        var actor = _authorizationLookup.Actor();
        var overrideScope = await executionScopes.GetTaskOverrideForUpdateAsync(task.Id, cancellationToken);
        if (overrideScope is null)
        {
            if (request.ExpectedVersion != 0)
                return Stale<TaskExecutionScopeResponse>();

            overrideScope = NewTaskOverride(task, actor, requestedPolicy.WebEnabled, requestedPolicy.ProjectFilesEnabled);
            await executionScopes.AddTaskOverrideAsync(overrideScope, cancellationToken);
        }
        else
        {
            if (overrideScope.VersionNo != request.ExpectedVersion)
                return Stale<TaskExecutionScopeResponse>();

            overrideScope.WebEnabled = requestedPolicy.WebEnabled;
            overrideScope.ProjectFilesEnabled = requestedPolicy.ProjectFilesEnabled;
            overrideScope.VersionNo = NextVersion(overrideScope.VersionNo);
            overrideScope.UpdatedByUserId = actor;
        }

        var projectScope = await executionScopes.GetProjectScopeAsync(task.ProjectId, cancellationToken);
        executionScopes.StageSourcePolicyDocument(new TaskExecutionSourcePolicyDocument(
            TaskExecutionSourcePolicyOwnerType.Task,
            task.Id,
            task.TenantId,
            task.WorkspaceId,
            task.ProjectId,
            task.Id,
            projectScope?.VersionNo ?? 0,
            overrideScope.VersionNo,
            requestedPolicy));

        AdvanceTaskVersion(task);
        await audit.LogAsync(new AuditLogEntry(
            actor,
            "TaskExecutionScopeOverrideSet",
            "TaskExecutionScopeOverride",
            overrideScope.Id,
            WorkspaceId: task.WorkspaceId,
            ProjectId: task.ProjectId,
            Metadata: ScopeMetadata(requestedPolicy, overrideScope.VersionNo)), cancellationToken);
        await invalidations.TaskChangedAsync(task, actor, "executionScopeChanged", cancellationToken: cancellationToken);

        var save = await unitOfWork.SaveTaskCommandAsync(cancellationToken);
        if (!save.IsSaved)
            return Stale<TaskExecutionScopeResponse>();

        return Result<TaskExecutionScopeResponse>.Success(
            await BuildTaskScopeResponseAsync(task, true, cancellationToken));
    }

    public async Task<Result<TaskExecutionScopeResponse>> ClearTaskOverrideAsync(
        Guid taskItemId,
        ClearTaskExecutionScopeOverrideRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.ExpectedVersion < 0)
            return Invalid<TaskExecutionScopeResponse>("expectedVersion", "Expected version must be zero or a positive integer.");

        var task = await _authorizationLookup.ManagedTaskAsync(taskItemId, cancellationToken);
        if (task is null)
            return NotFound<TaskExecutionScopeResponse>();

        var overrideScope = await executionScopes.GetTaskOverrideForUpdateAsync(task.Id, cancellationToken);
        if (overrideScope is null)
        {
            if (request.ExpectedVersion != 0)
                return Stale<TaskExecutionScopeResponse>();

            return Result<TaskExecutionScopeResponse>.Success(
                await BuildTaskScopeResponseAsync(task, true, cancellationToken));
        }

        if (overrideScope.VersionNo != request.ExpectedVersion)
            return Stale<TaskExecutionScopeResponse>();

        var actor = _authorizationLookup.Actor();
        var removedVersion = overrideScope.VersionNo;
        executionScopes.RemoveTaskOverride(overrideScope);
        executionScopes.StageSourcePolicyDocumentDelete(TaskExecutionSourcePolicyOwnerType.Task, task.Id);
        AdvanceTaskVersion(task);
        await audit.LogAsync(new AuditLogEntry(
            actor,
            "TaskExecutionScopeOverrideCleared",
            "TaskExecutionScopeOverride",
            overrideScope.Id,
            WorkspaceId: task.WorkspaceId,
            ProjectId: task.ProjectId,
            Metadata: new Dictionary<string, object?> { ["overrideVersion"] = removedVersion }), cancellationToken);
        await invalidations.TaskChangedAsync(task, actor, "executionScopeChanged", cancellationToken: cancellationToken);

        var save = await unitOfWork.SaveTaskCommandAsync(cancellationToken);
        if (!save.IsSaved)
            return Stale<TaskExecutionScopeResponse>();

        return Result<TaskExecutionScopeResponse>.Success(
            await BuildTaskScopeResponseAsync(task, true, cancellationToken));
    }

    public async Task<Result<TaskExecutionRunResponse>> RequestRunAsync(
        Guid taskItemId,
        string? idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidIdempotencyKey(idempotencyKey))
            return Result<TaskExecutionRunResponse>.Failure(new ApplicationErrorDetail(
                string.IsNullOrWhiteSpace(idempotencyKey) ? "TASK_EXECUTION_MISSING_IDEMPOTENCY_KEY" : "TASK_EXECUTION_INVALID_IDEMPOTENCY_KEY",
                string.IsNullOrWhiteSpace(idempotencyKey)
                    ? "An Idempotency-Key header is required."
                    : "The Idempotency-Key header is invalid.",
                Target: "header.Idempotency-Key"));

        var task = await _authorizationLookup.ManagedTaskAsync(taskItemId, cancellationToken);
        if (task is null)
            return NotFound<TaskExecutionRunResponse>();

        var actor = _authorizationLookup.Actor();
        var run = new TaskExecutionRun();

        IdempotentCreateResult<TaskExecutionRunResponse> result;
        try
        {
            result = await idempotency.ExecuteAsync(
                new CreateIdempotencyContext(
                    task.TenantId,
                    actor,
                    RunOperation,
                    idempotencyKey!,
                    CreateRunFingerprint(task),
                    "TaskExecutionRun",
                    run.Id),
                async token =>
                {
                    var projectScope = await executionScopes.GetProjectScopeAsync(task.ProjectId, token);
                    var overrideScope = await executionScopes.GetTaskOverrideAsync(task.Id, token);
                    var researchPlanSnapshot = await researchPlans.GetCurrentExecutionSnapshotForTaskAsync(task.Id, token);
                    var snapshot = await EffectivePolicyAsync(projectScope, overrideScope, token);

                    run.TenantId = task.TenantId;
                    run.WorkspaceId = task.WorkspaceId;
                    run.ProjectId = task.ProjectId;
                    run.TaskItemId = task.Id;
                    run.RequestedByUserId = actor;
                    run.RequestedAtUtc = clock.UtcNow;
                    run.SnapshotSchemaVersion = TaskExecutionRun.CurrentSnapshotSchemaVersion;
                    run.SnapshotScopeOrigin = snapshot.Origin;
                    run.SnapshotProjectScopeVersion = snapshot.ProjectVersion;
                    run.SnapshotTaskOverrideVersion = snapshot.OverrideVersion;
                    run.SnapshotWebEnabled = snapshot.Policy.WebEnabled;
                    run.SnapshotProjectFilesEnabled = snapshot.Policy.ProjectFilesEnabled;
                    run.SnapshotResearchPlanRevisionId = researchPlanSnapshot?.RevisionId;
                    run.SnapshotResearchPlanRevisionNo = researchPlanSnapshot?.RevisionNo;
                    run.RuntimeProvider = FirstPartyProjectFilesRuntimeV1.Provider;
                    run.RuntimeContractVersion = FirstPartyProjectFilesRuntimeV1.ContractVersion;
                    run.Status = TaskExecutionRunStatus.Accepted;
                    run.FailureCode = null;
                    run.QueuedAtUtc = null;
                    run.StartedAtUtc = null;
                    run.FinishedAtUtc = null;

                    await executionScopes.AddRunAsync(run, token);
                    executionScopes.StageSourcePolicyDocument(new TaskExecutionSourcePolicyDocument(
                        TaskExecutionSourcePolicyOwnerType.Run,
                        run.Id,
                        task.TenantId,
                        task.WorkspaceId,
                        task.ProjectId,
                        task.Id,
                        snapshot.ProjectVersion,
                        snapshot.OverrideVersion,
                        snapshot.Policy));

                    AdvanceTaskVersion(task);
                    await audit.LogAsync(new AuditLogEntry(
                        actor,
                        "TaskExecutionRunRequested",
                        "TaskExecutionRun",
                        run.Id,
                        WorkspaceId: task.WorkspaceId,
                        ProjectId: task.ProjectId,
                        Metadata: RunMetadata(run, snapshot.Policy)), token);
                    await invalidations.TaskChangedAsync(task, actor, "executionRunChanged", cancellationToken: token);
                    return ToRunResponse(run, snapshot.Policy);
                },
                async (runId, token) =>
                {
                    var existing = await executionScopes.GetRunAsync(runId, token);
                    return existing is null ? null : await ToRunResponseAsync(existing, token);
                },
                cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return Result<TaskExecutionRunResponse>.Failure(new ApplicationErrorDetail(
                "TASK_EXECUTION_PERSISTENCE_UNAVAILABLE",
                "The execution request could not be recorded."));
        }

        return result.Disposition switch
        {
            IdempotentCreateDisposition.Created or IdempotentCreateDisposition.Replayed when result.Value is not null =>
                Result<TaskExecutionRunResponse>.Success(result.Value),
            IdempotentCreateDisposition.RequestMismatch => Result<TaskExecutionRunResponse>.Failure(new ApplicationErrorDetail(
                "TASK_EXECUTION_IDEMPOTENCY_CONFLICT",
                "The Idempotency-Key was already used for a different execution request.",
                Target: "header.Idempotency-Key")),
            _ => Result<TaskExecutionRunResponse>.Failure(new ApplicationErrorDetail(
                "TASK_EXECUTION_REPLAY_UNAVAILABLE",
                "The execution request could not be reconciled. Retry with a new Idempotency-Key."))
        };
    }

    private async Task<TaskExecutionScopeResponse> BuildTaskScopeResponseAsync(
        TaskItem task,
        bool canManage,
        CancellationToken cancellationToken)
    {
        var projectScope = await executionScopes.GetProjectScopeAsync(task.ProjectId, cancellationToken);
        var overrideScope = await executionScopes.GetTaskOverrideAsync(task.Id, cancellationToken);
        var latestRun = await executionScopes.GetLatestRunAsync(task.Id, cancellationToken);
        var effective = await EffectivePolicyAsync(projectScope, overrideScope, cancellationToken);
        var overridePolicy = overrideScope is null
            ? null
            : await TaskOverridePolicyAsync(overrideScope, cancellationToken);
        var inventory = await BuildSourceInventoryAsync(task, canManage, cancellationToken);
        var effectiveForResponse = RedactTaskPolicy(effective.Policy, inventory, canManage);
        var overrideForResponse = overridePolicy is null ? null : RedactTaskPolicy(overridePolicy, inventory, canManage);

        return new TaskExecutionScopeResponse(
            ToPolicyResponse(effectiveForResponse),
            effective.Origin,
            effective.ProjectVersion,
            overrideScope?.VersionNo,
            overrideForResponse is null ? null : ToPolicyResponse(overrideForResponse),
            canManage,
            latestRun is null ? null : await ToRunResponseAsync(latestRun, cancellationToken),
            "nextRun",
            canManage ? inventory : []);
    }

    private async Task<IReadOnlyList<TaskExecutionSourceInventoryItemResponse>> BuildSourceInventoryAsync(
        TaskItem task,
        bool canManage,
        CancellationToken cancellationToken)
    {
        if (!canManage || !_authorizationLookup.TryActor(out var actor))
            return [];

        var result = new List<TaskExecutionSourceInventoryItemResponse>();
        if (fileAuthorization is not null)
        {
            var attachments = await executionScopes.ListTaskSourceAttachmentsAsync(task.Id, cancellationToken);
            var seenFiles = new HashSet<Guid>();
            foreach (var attachment in attachments)
            {
                if (attachment.FileObject is not { } fileObject || !seenFiles.Add(fileObject.Id))
                    continue;
                if (!await fileAuthorization.CanViewAttachment(actor, attachment, cancellationToken))
                    continue;

                result.Add(new TaskExecutionSourceInventoryItemResponse(
                    TaskExecutionSourceKind.ProjectFile,
                    TaskExecutionSourcePolicyV2.ProjectFileSourceId(fileObject.Id),
                    string.IsNullOrWhiteSpace(attachment.FileName) ? fileObject.OriginalFileName : attachment.FileName));
            }
        }

        if (await CanManageTenantAsync(task.TenantId, cancellationToken))
        {
            var integrations = await executionScopes.ListActiveIntegrationAccountsAsync(cancellationToken);
            result.AddRange(integrations.Select(account => new TaskExecutionSourceInventoryItemResponse(
                TaskExecutionSourceKind.ConnectedApp,
                TaskExecutionSourcePolicyV2.ConnectedAppSourceId(account.Id),
                account.DisplayName)));
        }

        return result;
    }

    private async Task<bool> CanUseProjectItemRulesAsync(
        Project project,
        TaskExecutionSourcePolicyV2 policy,
        CancellationToken cancellationToken)
    {
        if (!_authorizationLookup.TryActor(out var actor))
            return false;

        var projectFileRules = policy.Items.Where(rule => rule.Kind == TaskExecutionSourceKind.ProjectFile).ToList();
        if (projectFileRules.Count > 0)
        {
            if (fileAuthorization is null)
                return false;

            var allowedFileIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var attachment in await executionScopes.ListProjectSourceAttachmentsAsync(project.Id, cancellationToken))
            {
                if (attachment.FileObject is { } fileObject &&
                    await fileAuthorization.CanViewAttachment(actor, attachment, cancellationToken))
                {
                    allowedFileIds.Add(TaskExecutionSourcePolicyV2.ProjectFileSourceId(fileObject.Id));
                }
            }

            if (projectFileRules.Any(rule => !allowedFileIds.Contains(rule.SourceId)))
                return false;
        }

        return await CanUseConnectedAppRulesAsync(project.TenantId, policy, cancellationToken);
    }

    private async Task<bool> CanUseTaskItemRulesAsync(
        TaskItem task,
        TaskExecutionSourcePolicyV2 policy,
        CancellationToken cancellationToken)
    {
        if (!_authorizationLookup.TryActor(out var actor))
            return false;

        var projectFileRules = policy.Items.Where(rule => rule.Kind == TaskExecutionSourceKind.ProjectFile).ToList();
        if (projectFileRules.Count > 0)
        {
            if (fileAuthorization is null)
                return false;

            var allowedFileIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var attachment in await executionScopes.ListTaskSourceAttachmentsAsync(task.Id, cancellationToken))
            {
                if (attachment.FileObject is { } fileObject &&
                    await fileAuthorization.CanViewAttachment(actor, attachment, cancellationToken))
                {
                    allowedFileIds.Add(TaskExecutionSourcePolicyV2.ProjectFileSourceId(fileObject.Id));
                }
            }

            if (projectFileRules.Any(rule => !allowedFileIds.Contains(rule.SourceId)))
                return false;
        }

        return await CanUseConnectedAppRulesAsync(task.TenantId, policy, cancellationToken);
    }

    private async Task<bool> CanUseConnectedAppRulesAsync(
        Guid tenantId,
        TaskExecutionSourcePolicyV2 policy,
        CancellationToken cancellationToken)
    {
        var connectedRules = policy.Items.Where(rule => rule.Kind == TaskExecutionSourceKind.ConnectedApp).ToList();
        var requiresTenantAdmin = policy.ConnectedApp != TaskExecutionSourceState.Exclude || connectedRules.Count > 0;
        if (!requiresTenantAdmin)
            return true;
        if (!await CanManageTenantAsync(tenantId, cancellationToken))
            return false;
        if (connectedRules.Count == 0)
            return true;

        var activeIds = (await executionScopes.ListActiveIntegrationAccountsAsync(cancellationToken))
            .Select(account => TaskExecutionSourcePolicyV2.ConnectedAppSourceId(account.Id))
            .ToHashSet(StringComparer.Ordinal);
        return connectedRules.All(rule => activeIds.Contains(rule.SourceId));
    }

    private async Task<bool> CanManageTenantAsync(Guid tenantId, CancellationToken cancellationToken) =>
        _authorizationLookup.TryActor(out var actor) && tenantAuthorization is not null &&
        await tenantAuthorization.CanManageTenantAsync(actor, tenantId, cancellationToken);

    private async Task<TaskExecutionSourcePolicyV2> ProjectPolicyAsync(
        ProjectExecutionScope? scope,
        CancellationToken cancellationToken)
    {
        if (scope is null)
            return TaskExecutionSourcePolicyV2.FromLegacy(false, false);

        var document = await executionScopes.GetSourcePolicyDocumentAsync(
            TaskExecutionSourcePolicyOwnerType.Project,
            scope.ProjectId,
            cancellationToken);
        return document is { ProjectScopeVersion: var version } && version == scope.VersionNo
            ? document.Policy
            : TaskExecutionSourcePolicyV2.FromLegacy(scope.WebEnabled, scope.ProjectFilesEnabled);
    }

    private async Task<TaskExecutionSourcePolicyV2> TaskOverridePolicyAsync(
        TaskExecutionScopeOverride scope,
        CancellationToken cancellationToken)
    {
        var document = await executionScopes.GetSourcePolicyDocumentAsync(
            TaskExecutionSourcePolicyOwnerType.Task,
            scope.TaskItemId,
            cancellationToken);
        return document is { TaskOverrideVersion: var version } && version == scope.VersionNo
            ? document.Policy
            : TaskExecutionSourcePolicyV2.FromLegacy(scope.WebEnabled, scope.ProjectFilesEnabled);
    }

    private async Task<EffectiveExecutionScope> EffectivePolicyAsync(
        ProjectExecutionScope? projectScope,
        TaskExecutionScopeOverride? taskOverride,
        CancellationToken cancellationToken)
    {
        if (taskOverride is not null)
        {
            return new EffectiveExecutionScope(
                TaskExecutionScopeOrigin.TaskOverride,
                projectScope?.VersionNo ?? 0,
                taskOverride.VersionNo,
                await TaskOverridePolicyAsync(taskOverride, cancellationToken));
        }

        return new EffectiveExecutionScope(
            TaskExecutionScopeOrigin.ProjectDefault,
            projectScope?.VersionNo ?? 0,
            null,
            await ProjectPolicyAsync(projectScope, cancellationToken));
    }

    private static ProjectExecutionScope NewProjectScope(
        Project project,
        Guid actor,
        bool webEnabled,
        bool projectFilesEnabled) => new()
    {
        TenantId = project.TenantId,
        WorkspaceId = project.WorkspaceId,
        ProjectId = project.Id,
        WebEnabled = webEnabled,
        ProjectFilesEnabled = projectFilesEnabled,
        UpdatedByUserId = actor
    };

    private static TaskExecutionScopeOverride NewTaskOverride(
        TaskItem task,
        Guid actor,
        bool webEnabled,
        bool projectFilesEnabled) => new()
    {
        TenantId = task.TenantId,
        WorkspaceId = task.WorkspaceId,
        ProjectId = task.ProjectId,
        TaskItemId = task.Id,
        WebEnabled = webEnabled,
        ProjectFilesEnabled = projectFilesEnabled,
        UpdatedByUserId = actor
    };

    private static ProjectExecutionScopeResponse ToProjectResponse(
        ProjectExecutionScope? scope,
        TaskExecutionSourcePolicyV2 policy,
        bool canManage) => new(
            ToPolicyResponse(policy),
            scope?.VersionNo ?? 0,
            canManage);

    private async Task<TaskExecutionRunResponse> ToRunResponseAsync(
        TaskExecutionRun run,
        CancellationToken cancellationToken)
    {
        var document = await executionScopes.GetSourcePolicyDocumentAsync(
            TaskExecutionSourcePolicyOwnerType.Run,
            run.Id,
            cancellationToken);
        var policy = document?.Policy ?? TaskExecutionSourcePolicyV2.FromLegacy(
            run.SnapshotWebEnabled,
            run.SnapshotProjectFilesEnabled);
        return ToRunResponse(run, policy);
    }

    private static TaskExecutionRunResponse ToRunResponse(TaskExecutionRun run, TaskExecutionSourcePolicyV2 policy) => new(
        run.Id,
        run.Status,
        run.FailureCode,
        run.RequestedAtUtc,
        run.FinishedAtUtc,
        run.SnapshotSchemaVersion,
        run.SnapshotScopeOrigin,
        run.SnapshotProjectScopeVersion,
        run.SnapshotTaskOverrideVersion,
        run.SnapshotWebEnabled,
        run.SnapshotProjectFilesEnabled,
        run.RuntimeProvider,
        run.RuntimeContractVersion,
        run.QueuedAtUtc,
        run.StartedAtUtc,
        run.SnapshotResearchPlanRevisionId,
        run.SnapshotResearchPlanRevisionNo,
        policy);

    private static TaskExecutionSourcePolicyResponse ToPolicyResponse(TaskExecutionSourcePolicyV2 policy) =>
        new(policy.WebEnabled, policy.ProjectFilesEnabled, policy);

    private static TaskExecutionSourcePolicyV2 RedactProjectPolicy(
        TaskExecutionSourcePolicyV2 policy,
        bool canManage,
        bool canManageConnectedApps)
    {
        if (!canManage)
            return policy with { Items = [] };
        if (canManageConnectedApps)
            return policy;
        return policy with
        {
            ConnectedApp = TaskExecutionSourceState.Exclude,
            Items = policy.Items.Where(rule => rule.Kind != TaskExecutionSourceKind.ConnectedApp).ToList()
        };
    }

    private static TaskExecutionSourcePolicyV2 RedactTaskPolicy(
        TaskExecutionSourcePolicyV2 policy,
        IReadOnlyList<TaskExecutionSourceInventoryItemResponse> inventory,
        bool canManage)
    {
        if (!canManage)
            return policy with { Items = [] };

        var visibleIds = inventory.Select(item => (item.Kind, item.SourceId)).ToHashSet();
        return policy with
        {
            Items = policy.Items
                .Where(rule => rule.Kind is TaskExecutionSourceKind.Web or TaskExecutionSourceKind.WebSite ||
                               visibleIds.Contains((rule.Kind, rule.SourceId)))
                .ToList()
        };
    }

    private static bool TryRequestedPolicy(
        TaskExecutionSourcePolicyV2? supplied,
        bool webEnabled,
        bool projectFilesEnabled,
        out TaskExecutionSourcePolicyV2 policy,
        out ApplicationErrorDetail? error)
    {
        error = null;
        if (supplied is null)
        {
            policy = TaskExecutionSourcePolicyV2.FromLegacy(webEnabled, projectFilesEnabled);
            return true;
        }

        if (!supplied.TryNormalize(out policy, out var target, out var message))
        {
            error = new ApplicationErrorDetail(
                "TASK_EXECUTION_VALIDATION_FAILED",
                message ?? "Source policy is invalid.",
                Target: target ?? "policyV2");
            return false;
        }

        if (policy.WebEnabled != webEnabled || policy.ProjectFilesEnabled != projectFilesEnabled)
        {
            error = new ApplicationErrorDetail(
                "TASK_EXECUTION_VALIDATION_FAILED",
                "The legacy compatibility flags contradict policyV2. Recompute them from the itemized policy before saving.",
                Target: "policyV2");
            return false;
        }

        return true;
    }

    private static IReadOnlyDictionary<string, object?> ScopeMetadata(TaskExecutionSourcePolicyV2 policy, long version) =>
        new Dictionary<string, object?>
        {
            ["webEnabled"] = policy.WebEnabled,
            ["projectFilesEnabled"] = policy.ProjectFilesEnabled,
            ["scopeVersion"] = version,
            ["policySchemaVersion"] = policy.SchemaVersion,
            ["webDefault"] = policy.Web.ToString(),
            ["webSiteDefault"] = policy.WebSite.ToString(),
            ["projectFileDefault"] = policy.ProjectFile.ToString(),
            ["connectedAppDefault"] = policy.ConnectedApp.ToString(),
            ["itemRuleCount"] = policy.Items.Count
        };

    private static IReadOnlyDictionary<string, object?> RunMetadata(TaskExecutionRun run, TaskExecutionSourcePolicyV2 policy) =>
        new Dictionary<string, object?>
        {
            ["snapshotSchemaVersion"] = run.SnapshotSchemaVersion,
            ["scopeOrigin"] = run.SnapshotScopeOrigin.ToString(),
            ["projectScopeVersion"] = run.SnapshotProjectScopeVersion,
            ["taskOverrideVersion"] = run.SnapshotTaskOverrideVersion,
            ["webEnabled"] = run.SnapshotWebEnabled,
            ["projectFilesEnabled"] = run.SnapshotProjectFilesEnabled,
            ["policySchemaVersion"] = policy.SchemaVersion,
            ["itemRuleCount"] = policy.Items.Count,
            ["researchPlanRevisionNo"] = run.SnapshotResearchPlanRevisionNo,
            ["runtimeProvider"] = run.RuntimeProvider.ToString(),
            ["runtimeContractVersion"] = run.RuntimeContractVersion,
            ["status"] = run.Status.ToString()
        };

    private static string CreateRunFingerprint(TaskItem task) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(task.Id.ToString("N"))));

    private static bool IsValidIdempotencyKey(string? value) =>
        value is { Length: >= 8 and <= 128 } &&
        !string.IsNullOrWhiteSpace(value) &&
        value.All(character => character is >= ' ' and <= '~');

    private static long NextVersion(long value) => Math.Max(1L, checked(value + 1L));

    private static void AdvanceTaskVersion(TaskItem task) => task.VersionNo = NextVersion(task.VersionNo);

    private static Result<T> Invalid<T>(string target, string message) =>
        Result<T>.Failure(new ApplicationErrorDetail("TASK_EXECUTION_VALIDATION_FAILED", message, Target: target));

    private static Result<T> Stale<T>() =>
        Result<T>.Failure(new ApplicationErrorDetail(
            "TASK_EXECUTION_STALE_VERSION",
            "The execution scope has changed. Refetch and retry."));

    private static Result<T> NotFound<T>() =>
        Result<T>.Failure(new ApplicationErrorDetail(
            "TASK_EXECUTION_NOT_FOUND",
            "The requested resource was not found."));

    private sealed record EffectiveExecutionScope(
        TaskExecutionScopeOrigin Origin,
        long ProjectVersion,
        long? OverrideVersion,
        TaskExecutionSourcePolicyV2 Policy);
}
