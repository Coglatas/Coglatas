using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Realtime;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;

namespace Coglatas.Application.Projects;

/// <summary>
/// Owns Task-bound Research Plan revisions. The Task Brief and execution source
/// policy remain separate Task contracts; this service never invokes execution.
/// </summary>
public sealed class ResearchPlanService(
    IProjectRepository projects,
    IProjectAuthorizationService projectAuthorization,
    IResearchPlanRepository researchPlans,
    ICurrentUser currentUser,
    IClock clock,
    IAuditLogger audit,
    IBusinessInvalidationPublisher invalidations,
    ITaskCommandUnitOfWork unitOfWork) : IResearchPlanService
{
    private readonly ProjectAuthorizationLookup _authorizationLookup = new(projects, projectAuthorization, currentUser);

    private const int MaximumSteps = 100;
    private const int MaximumTitleLength = 240;
    private const int MaximumObjectiveLength = 4_000;
    private const int MaximumScopeSummaryLength = 4_000;

    public async Task<Result<ResearchPlanResponse>> GetAsync(
        Guid taskItemId,
        CancellationToken cancellationToken = default)
    {
        var task = await _authorizationLookup.VisibleTaskAsync(taskItemId, cancellationToken);
        if (task is null)
            return NotFound<ResearchPlanResponse>();

        var plan = await researchPlans.GetForTaskAsync(task.Id, cancellationToken);
        var canManage = await _authorizationLookup.CanManageAsync(task.ProjectId, cancellationToken);
        return Result<ResearchPlanResponse>.Success(
            await BuildResponseAsync(plan, canManage, cancellationToken));
    }

    public async Task<Result<ResearchPlanPreviewResponse>> PreviewAsync(
        Guid taskItemId,
        PreviewResearchPlanRequest request,
        CancellationToken cancellationToken = default)
    {
        var task = await _authorizationLookup.ManagedTaskAsync(taskItemId, cancellationToken);
        if (task is null)
            return NotFound<ResearchPlanPreviewResponse>();

        if (!TryNormalizeSteps(request.Steps, out var steps, out var failure))
            return Result<ResearchPlanPreviewResponse>.Failure(failure!);
        if (request.ExpectedVersion < 0)
            return Invalid<ResearchPlanPreviewResponse>("expectedVersion", "Expected version must be zero or a positive integer.");

        var plan = await researchPlans.GetForTaskAsync(task.Id, cancellationToken);
        if (plan is null && request.ExpectedVersion != 0)
            return Stale<ResearchPlanPreviewResponse>();
        if (plan is not null && plan.VersionNo != request.ExpectedVersion)
            return Stale<ResearchPlanPreviewResponse>();

        var baseRevision = await GetCurrentRevisionAsync(plan, cancellationToken);
        return BuildPreview(plan?.VersionNo ?? 0, baseRevision, steps);
    }

    public async Task<Result<ResearchPlanResponse>> ReplaceAsync(
        Guid taskItemId,
        ReplaceResearchPlanRequest request,
        CancellationToken cancellationToken = default)
    {
        var task = await _authorizationLookup.ManagedTaskAsync(taskItemId, cancellationToken);
        if (task is null)
            return NotFound<ResearchPlanResponse>();

        if (!TryNormalizeSteps(request.Steps, out var steps, out var failure))
            return Result<ResearchPlanResponse>.Failure(failure!);

        if (request.ExpectedVersion < 0)
            return Invalid<ResearchPlanResponse>("expectedVersion", "Expected version must be zero or a positive integer.");

        var plan = await researchPlans.GetForTaskForUpdateAsync(task.Id, cancellationToken);
        if (plan is null && request.ExpectedVersion != 0)
            return Stale<ResearchPlanResponse>();
        if (plan is not null && plan.VersionNo != request.ExpectedVersion)
            return Stale<ResearchPlanResponse>();

        ResearchPlanPreviewResponse? reviewedPreview = null;
        if (request.PreviewFingerprint is not null)
        {
            var baseRevision = await GetCurrentRevisionAsync(plan, cancellationToken);
            var preview = BuildPreview(plan?.VersionNo ?? 0, baseRevision, steps);
            if (!preview.IsSuccess)
                return Result<ResearchPlanResponse>.Failure(preview.ErrorDetail!);

            reviewedPreview = preview.Value!;
            if (!string.Equals(
                    reviewedPreview.Fingerprint,
                    request.PreviewFingerprint.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                return PreviewMismatch<ResearchPlanResponse>();
            }
        }

        var actor = _authorizationLookup.Actor();
        if (plan is null)
        {
            plan = new ResearchPlan
            {
                TenantId = task.TenantId,
                WorkspaceId = task.WorkspaceId,
                ProjectId = task.ProjectId,
                TaskItemId = task.Id,
                VersionNo = 1
            };
            await researchPlans.AddPlanAsync(plan, cancellationToken);
        }
        else
        {
            plan.VersionNo = NextVersion(plan.VersionNo);
        }

        var priorRevisionNo = await researchPlans.GetLatestRevisionNumberAsync(plan.Id, cancellationToken);
        var revision = new ResearchPlanRevision
        {
            TenantId = task.TenantId,
            WorkspaceId = task.WorkspaceId,
            ProjectId = task.ProjectId,
            TaskItemId = task.Id,
            ResearchPlanId = plan.Id,
            RevisionNo = NextRevisionNo(priorRevisionNo),
            CreatedByUserId = actor,
            CreatedAtUtc = clock.UtcNow
        };
        var snapshotSteps = steps.Select((step, index) => new ResearchPlanStep
        {
            TenantId = task.TenantId,
            WorkspaceId = task.WorkspaceId,
            ProjectId = task.ProjectId,
            TaskItemId = task.Id,
            ResearchPlanId = plan.Id,
            ResearchPlanRevisionId = revision.Id,
            SortOrder = index + 1,
            Title = step.Title,
            Objective = step.Objective,
            ScopeSummary = step.ScopeSummary,
            Status = step.Status
        }).ToArray();

        plan.CurrentRevisionId = revision.Id;
        await researchPlans.AddRevisionAsync(revision, cancellationToken);
        await researchPlans.AddStepsAsync(snapshotSteps, cancellationToken);
        AdvanceTaskVersion(task);

        await audit.LogAsync(new AuditLogEntry(
            actor,
            "ResearchPlanRevisionSaved",
            "ResearchPlanRevision",
            revision.Id,
            WorkspaceId: task.WorkspaceId,
            ProjectId: task.ProjectId,
            Metadata: new Dictionary<string, object?>
            {
                ["planId"] = plan.Id,
                ["revisionNo"] = revision.RevisionNo,
                ["stepCount"] = snapshotSteps.Length,
                ["planVersion"] = plan.VersionNo,
                ["reviewedDiff"] = reviewedPreview is not null,
                ["changeCount"] = reviewedPreview?.Changes.Count
            }), cancellationToken);
        await invalidations.TaskChangedAsync(
            task,
            actor,
            "researchPlanChanged",
            ["researchPlan"],
            cancellationToken: cancellationToken);

        var save = await unitOfWork.SaveTaskCommandAsync(cancellationToken);
        if (!save.IsSaved)
            return Stale<ResearchPlanResponse>();

        return Result<ResearchPlanResponse>.Success(
            new ResearchPlanResponse(plan.Id, plan.VersionNo, ToRevisionResponse(revision, snapshotSteps), true));
    }

    private async Task<ResearchPlanResponse> BuildResponseAsync(
        ResearchPlan? plan,
        bool canManage,
        CancellationToken cancellationToken)
    {
        if (plan is null || !plan.CurrentRevisionId.HasValue)
            return new ResearchPlanResponse(plan?.Id, plan?.VersionNo ?? 0, null, canManage);

        var revision = await researchPlans.GetRevisionAsync(plan.Id, plan.CurrentRevisionId.Value, cancellationToken);
        return new ResearchPlanResponse(
            plan.Id,
            plan.VersionNo,
            revision is null ? null : ToRevisionResponse(revision, revision.Steps),
            canManage);
    }

    private Task<ResearchPlanRevision?> GetCurrentRevisionAsync(
        ResearchPlan? plan,
        CancellationToken cancellationToken) =>
        plan is null || !plan.CurrentRevisionId.HasValue
            ? Task.FromResult<ResearchPlanRevision?>(null)
            : researchPlans.GetRevisionAsync(plan.Id, plan.CurrentRevisionId.Value, cancellationToken);

    private static Result<ResearchPlanPreviewResponse> BuildPreview(
        long baseVersion,
        ResearchPlanRevision? baseRevision,
        IReadOnlyList<NormalizedStep> proposed)
    {
        var beforeSteps = (baseRevision?.Steps ?? [])
            .OrderBy(step => step.SortOrder)
            .ThenBy(step => step.Id)
            .ToArray();
        var beforeById = beforeSteps.ToDictionary(step => step.Id);
        var referencedIds = new HashSet<Guid>();

        for (var index = 0; index < proposed.Count; index++)
        {
            var baseStepId = proposed[index].BaseStepId;
            if (!baseStepId.HasValue)
                continue;

            if (!referencedIds.Add(baseStepId.Value))
            {
                return Invalid<ResearchPlanPreviewResponse>(
                    $"steps[{index}].baseStepId",
                    "Each current Research Plan step may be referenced at most once.");
            }

            if (!beforeById.ContainsKey(baseStepId.Value))
            {
                return Invalid<ResearchPlanPreviewResponse>(
                    $"steps[{index}].baseStepId",
                    "The base step is not part of the current Research Plan revision.");
            }
        }

        var proposedResponses = proposed
            .Select((step, index) => ToProposedStepResponse(step, index + 1))
            .ToArray();

        var priorMatchedOrder = beforeSteps
            .Where(step => referencedIds.Contains(step.Id))
            .Select((step, index) => new { step.Id, Index = index })
            .ToDictionary(item => item.Id, item => item.Index);
        var proposedMatchedOrder = proposed
            .Where(step => step.BaseStepId.HasValue)
            .Select((step, index) => new { Id = step.BaseStepId!.Value, Index = index })
            .ToDictionary(item => item.Id, item => item.Index);
        var reorderedIds = priorMatchedOrder
            .Where(pair => proposedMatchedOrder[pair.Key] != pair.Value)
            .Select(pair => pair.Key)
            .ToHashSet();

        var changes = new List<ResearchPlanStepDiffResponse>();
        foreach (var before in beforeSteps.Where(step => !referencedIds.Contains(step.Id)))
        {
            changes.Add(new ResearchPlanStepDiffResponse(
                ["Removed"],
                before.Id,
                before.SortOrder,
                null,
                ToStepResponse(before),
                null,
                []));
        }

        for (var index = 0; index < proposed.Count; index++)
        {
            var after = proposed[index];
            var afterResponse = proposedResponses[index];
            if (!after.BaseStepId.HasValue)
            {
                changes.Add(new ResearchPlanStepDiffResponse(
                    ["Added"],
                    null,
                    null,
                    index + 1,
                    null,
                    afterResponse,
                    []));
                continue;
            }

            var before = beforeById[after.BaseStepId.Value];
            var changedFields = ChangedFields(before, after);
            var kinds = new List<string>(2);
            if (changedFields.Count > 0)
                kinds.Add("Modified");
            if (reorderedIds.Contains(before.Id))
                kinds.Add("Reordered");
            if (kinds.Count == 0)
                continue;

            changes.Add(new ResearchPlanStepDiffResponse(
                kinds,
                before.Id,
                before.SortOrder,
                index + 1,
                ToStepResponse(before),
                afterResponse,
                changedFields));
        }

        var orderedChanges = changes
            .OrderBy(change => change.AfterPosition ?? change.BeforePosition ?? int.MaxValue)
            .ThenBy(change => change.Kinds[0], StringComparer.Ordinal)
            .ToArray();
        var impact = BuildImpact(beforeSteps.Length, proposed.Count, orderedChanges);
        var fingerprint = ComputeFingerprint(baseVersion, baseRevision?.Id, proposed);

        return Result<ResearchPlanPreviewResponse>.Success(new ResearchPlanPreviewResponse(
            baseVersion,
            baseRevision?.Id,
            baseRevision is null ? null : checked((int)baseRevision.RevisionNo),
            fingerprint,
            proposedResponses,
            orderedChanges,
            impact));
    }

    private static ResearchPlanImpactSummaryResponse BuildImpact(
        int beforeCount,
        int afterCount,
        IReadOnlyList<ResearchPlanStepDiffResponse> changes)
    {
        var executionCountChanged = beforeCount != afterCount;
        var executionOrderChanged = changes.Any(change => change.Kinds.Contains("Reordered", StringComparer.Ordinal));
        var sourceScopeGuidanceChanged = changes.Any(change =>
            change.ChangedFields.Contains("scopeSummary", StringComparer.Ordinal) ||
            change.Kinds.Contains("Added", StringComparer.Ordinal) && !string.IsNullOrWhiteSpace(change.After?.ScopeSummary) ||
            change.Kinds.Contains("Removed", StringComparer.Ordinal) && !string.IsNullOrWhiteSpace(change.Before?.ScopeSummary));
        var deliverableAlignmentReviewRequired = changes.Any(change =>
            change.Kinds.Contains("Added", StringComparer.Ordinal) ||
            change.Kinds.Contains("Removed", StringComparer.Ordinal) ||
            change.ChangedFields.Contains("title", StringComparer.Ordinal) ||
            change.ChangedFields.Contains("objective", StringComparer.Ordinal));

        var items = new List<ResearchPlanImpactItemResponse>();
        foreach (var change in changes)
        {
            if (change.Kinds.Contains("Added", StringComparer.Ordinal))
            {
                items.Add(new ResearchPlanImpactItemResponse(
                    "ExecutionStepAdded",
                    $"Step {change.AfterPosition} will be added to the saved execution plan.",
                    change.AfterPosition));
            }
            if (change.Kinds.Contains("Removed", StringComparer.Ordinal))
            {
                items.Add(new ResearchPlanImpactItemResponse(
                    "ExecutionStepRemoved",
                    $"Step {change.BeforePosition} will be removed from the saved execution plan.",
                    change.BeforePosition,
                    change.BaseStepId));
            }
            if (change.Kinds.Contains("Reordered", StringComparer.Ordinal))
            {
                items.Add(new ResearchPlanImpactItemResponse(
                    "ExecutionOrderChanged",
                    $"An existing step moves from position {change.BeforePosition} to {change.AfterPosition}.",
                    change.AfterPosition,
                    change.BaseStepId));
            }
            if (change.ChangedFields.Contains("scopeSummary", StringComparer.Ordinal))
            {
                items.Add(new ResearchPlanImpactItemResponse(
                    "SourceScopeGuidanceChanged",
                    $"Source-scope guidance changes for Step {change.AfterPosition}. Effective source access is unchanged by this plan edit and remains governed by the Task execution source policy.",
                    change.AfterPosition,
                    change.BaseStepId));
            }
            if (change.ChangedFields.Any(field => field is "title" or "objective" or "status"))
            {
                items.Add(new ResearchPlanImpactItemResponse(
                    "ExecutionStepContentChanged",
                    $"Execution-step content changes for Step {change.AfterPosition}.",
                    change.AfterPosition,
                    change.BaseStepId));
            }
        }

        if (deliverableAlignmentReviewRequired)
        {
            items.Add(new ResearchPlanImpactItemResponse(
                "DeliverableAlignmentReviewRequired",
                "Plan coverage changed. Review the Task deliverable before saving; Research Plan edits do not mutate the Task deliverable contract."));
        }

        return new ResearchPlanImpactSummaryResponse(
            beforeCount,
            afterCount,
            executionCountChanged,
            executionOrderChanged,
            sourceScopeGuidanceChanged,
            deliverableAlignmentReviewRequired,
            items);
    }

    private static bool TryNormalizeSteps(
        IReadOnlyList<ResearchPlanStepRequest>? source,
        out IReadOnlyList<NormalizedStep> normalized,
        out ApplicationErrorDetail? failure)
    {
        normalized = [];
        failure = null;
        if (source is null || source.Count > MaximumSteps)
        {
            failure = new ApplicationErrorDetail(
                "RESEARCH_PLAN_VALIDATION_FAILED",
                $"A Research Plan may contain at most {MaximumSteps} steps.",
                Target: "steps");
            return false;
        }

        var items = new List<NormalizedStep>(source.Count);
        for (var index = 0; index < source.Count; index++)
        {
            var step = source[index];
            if (step is null)
            {
                failure = InvalidStep(index, "A step is required.");
                return false;
            }

            if (step.BaseStepId == Guid.Empty)
            {
                failure = InvalidStep(index, "Base step id must be a non-empty identifier when supplied.", "baseStepId");
                return false;
            }

            var title = step.Title?.Trim();
            if (string.IsNullOrWhiteSpace(title) || title.Length > MaximumTitleLength)
            {
                failure = InvalidStep(index, $"Title is required and may contain at most {MaximumTitleLength} characters.", "title");
                return false;
            }

            var objective = step.Objective?.Trim() ?? string.Empty;
            if (objective.Length > MaximumObjectiveLength)
            {
                failure = InvalidStep(index, $"Objective may contain at most {MaximumObjectiveLength} characters.", "objective");
                return false;
            }

            var scopeSummary = step.ScopeSummary?.Trim() ?? string.Empty;
            if (scopeSummary.Length > MaximumScopeSummaryLength)
            {
                failure = InvalidStep(index, $"Scope summary may contain at most {MaximumScopeSummaryLength} characters.", "scopeSummary");
                return false;
            }

            if (!Enum.IsDefined(step.Status))
            {
                failure = InvalidStep(index, "Step status is invalid.", "status");
                return false;
            }

            items.Add(new NormalizedStep(step.BaseStepId, title, objective, scopeSummary, step.Status));
        }

        normalized = items;
        return true;
    }

    private static IReadOnlyList<string> ChangedFields(ResearchPlanStep before, NormalizedStep after)
    {
        var fields = new List<string>(4);
        if (!string.Equals(before.Title, after.Title, StringComparison.Ordinal)) fields.Add("title");
        if (!string.Equals(before.Objective, after.Objective, StringComparison.Ordinal)) fields.Add("objective");
        if (!string.Equals(before.ScopeSummary, after.ScopeSummary, StringComparison.Ordinal)) fields.Add("scopeSummary");
        if (before.Status != after.Status) fields.Add("status");
        return fields;
    }

    private static ResearchPlanRevisionResponse ToRevisionResponse(
        ResearchPlanRevision revision,
        IEnumerable<ResearchPlanStep> steps) =>
        new(
            revision.Id,
            checked((int)revision.RevisionNo),
            revision.CreatedAtUtc,
            revision.CreatedByUserId,
            steps
                .OrderBy(step => step.SortOrder)
                .ThenBy(step => step.Id)
                .Select(ToStepResponse)
                .ToArray());

    private static ResearchPlanStepResponse ToStepResponse(ResearchPlanStep step) =>
        new(
            step.Id,
            step.SortOrder,
            step.Title,
            step.Objective,
            step.ScopeSummary,
            step.Status);

    private static ResearchPlanProposedStepResponse ToProposedStepResponse(NormalizedStep step, int position) =>
        new(
            step.BaseStepId,
            position,
            step.Title,
            step.Objective,
            step.ScopeSummary,
            step.Status);

    private static string ComputeFingerprint(
        long baseVersion,
        Guid? baseRevisionId,
        IReadOnlyList<NormalizedStep> steps)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            baseVersion,
            baseRevisionId,
            steps = steps.Select((step, index) => new
            {
                position = index + 1,
                step.BaseStepId,
                step.Title,
                step.Objective,
                step.ScopeSummary,
                status = step.Status.ToString()
            })
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static ApplicationErrorDetail InvalidStep(int index, string message, string? field = null) =>
        new(
            "RESEARCH_PLAN_VALIDATION_FAILED",
            message,
            Target: field is null ? $"steps[{index}]" : $"steps[{index}].{field}");

    private static long NextRevisionNo(long? prior) => Math.Max(1L, checked((prior ?? 0) + 1L));
    private static long NextVersion(long value) => Math.Max(1L, checked(value + 1L));
    private static void AdvanceTaskVersion(TaskItem task) => task.VersionNo = NextVersion(task.VersionNo);

    private static Result<T> Invalid<T>(string target, string message) =>
        Result<T>.Failure(new ApplicationErrorDetail("RESEARCH_PLAN_VALIDATION_FAILED", message, Target: target));

    private static Result<T> PreviewMismatch<T>() =>
        Result<T>.Failure(new ApplicationErrorDetail(
            "RESEARCH_PLAN_PREVIEW_MISMATCH",
            "The Research Plan draft no longer matches the reviewed diff. Review the latest changes before saving."));

    private static Result<T> Stale<T>() =>
        Result<T>.Failure(new ApplicationErrorDetail(
            "RESEARCH_PLAN_STALE_VERSION",
            "The Research Plan has changed. Refetch and retry."));

    private static Result<T> NotFound<T>() =>
        Result<T>.Failure(new ApplicationErrorDetail(
            "RESEARCH_PLAN_NOT_FOUND",
            "The requested resource was not found."));

    private sealed record NormalizedStep(
        Guid? BaseStepId,
        string Title,
        string Objective,
        string ScopeSummary,
        ResearchPlanStepStatus Status);
}
