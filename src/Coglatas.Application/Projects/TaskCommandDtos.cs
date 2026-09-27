using Coglatas.Domain.Enums;
using Coglatas.Application.Common;
using Coglatas.Application.Planning;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Coglatas.Application.Projects;

// API response properties are consumed by JSON serialization.
// ReSharper disable NotAccessedPositionalProperty.Global

public sealed record TaskTransitionRequest(Guid WorkflowStageId, long ExpectedVersion, string? Reason = null);
/// <summary>Ordinary Task-body fields only. Workflow state changes use the transition command.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TaskUpdateDetailsRequest
{
    // System.Text.Json/OpenAPI use the parameterless constructor and property
    // contract. This keeps omitted PATCH members at their default sentinel value
    // without exposing custom-value-type constructor defaults to JsonSchemaExporter.
    public TaskUpdateDetailsRequest()
    {
    }

    // Preserve the existing source-level construction surface used by command
    // tests and application code while keeping it out of the JSON constructor
    // contract selected by System.Text.Json.
    public TaskUpdateDetailsRequest(
        string? Title,
        string? Description,
        TaskPriority? Priority,
        DateOnly? PlannedStartDate,
        DateOnly? PlannedEndDate,
        int? ProgressPercent,
        long ExpectedVersion,
        OptionalDateTimeOffset DeadlineAt = default,
        OptionalString Goal = default,
        OptionalString Deliverable = default,
        OptionalString Constraints = default)
    {
        this.Title = Title;
        this.Description = Description;
        this.Priority = Priority;
        this.PlannedStartDate = PlannedStartDate;
        this.PlannedEndDate = PlannedEndDate;
        this.ProgressPercent = ProgressPercent;
        this.ExpectedVersion = ExpectedVersion;
        this.DeadlineAt = DeadlineAt;
        this.Goal = Goal;
        this.Deliverable = Deliverable;
        this.Constraints = Constraints;
    }

    public string? Title { get; init; }
    public string? Description { get; init; }
    public TaskPriority? Priority { get; init; }
    public DateOnly? PlannedStartDate { get; init; }
    public DateOnly? PlannedEndDate { get; init; }
    public int? ProgressPercent { get; init; }
    public long ExpectedVersion { get; init; }
    public OptionalDateTimeOffset DeadlineAt { get; init; }
    public OptionalString Goal { get; init; }
    public OptionalString Deliverable { get; init; }
    public OptionalString Constraints { get; init; }
}

public static class TaskBriefText
{
    public const int MaximumFieldLength = 4_000;

    private static bool ExceedsMaximum(string? value) =>
        value?.Trim().Length > MaximumFieldLength;

    public static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    public static ApplicationErrorDetail? Validate(
        string? goal,
        string? deliverable,
        string? constraints)
    {
        if (ExceedsMaximum(goal))
            return TooLong("Goal", "goal");
        if (ExceedsMaximum(deliverable))
            return TooLong("Deliverable", "deliverable");
        if (ExceedsMaximum(constraints))
            return TooLong("Constraints", "constraints");
        return null;
    }

    private static ApplicationErrorDetail TooLong(string label, string target) => new(
        "TASK_BRIEF_FIELD_TOO_LONG",
        $"Task brief {label} must be {MaximumFieldLength} characters or fewer.",
        Target: target);
}

[JsonConverter(typeof(TaskBriefValueSourceJsonConverter))]
public enum TaskBriefValueSource
{
    NotSet,
    TaskSpecific
}

public sealed class TaskBriefValueSourceJsonConverter : JsonConverter<TaskBriefValueSource>
{
    public override TaskBriefValueSource Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Task brief value source must be a string.");

        return reader.GetString() switch
        {
            "notSet" => TaskBriefValueSource.NotSet,
            "taskSpecific" => TaskBriefValueSource.TaskSpecific,
            _ => throw new JsonException("Task brief value source is invalid.")
        };
    }

    public override void Write(Utf8JsonWriter writer, TaskBriefValueSource value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            TaskBriefValueSource.NotSet => "notSet",
            TaskBriefValueSource.TaskSpecific => "taskSpecific",
            _ => throw new JsonException("Task brief value source is invalid.")
        });
}

public sealed record TaskBriefFieldResponse(string? Value, TaskBriefValueSource Source);
public sealed record TaskBriefResponse(
    TaskBriefFieldResponse Goal,
    TaskBriefFieldResponse Deliverable,
    TaskBriefFieldResponse Constraints);

/// <summary>Distinguishes an omitted hard-deadline PATCH member from an explicit JSON null.</summary>
[JsonConverter(typeof(OptionalDateTimeOffsetJsonConverter))]
public readonly record struct OptionalDateTimeOffset(bool IsSpecified, DateTimeOffset? Value)
{
    public static implicit operator OptionalDateTimeOffset(DateTimeOffset? value) => new(true, value);
}

public sealed class OptionalDateTimeOffsetJsonConverter : JsonConverter<OptionalDateTimeOffset>
{
    public override bool HandleNull => true;

    public override OptionalDateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return new OptionalDateTimeOffset(true, null);
        }

        if (reader.TokenType != JsonTokenType.String || !reader.TryGetDateTimeOffset(out var value))
        {
            throw new JsonException("'deadlineAt' must be an ISO 8601 timestamp or null.");
        }

        return new OptionalDateTimeOffset(true, value);
    }

    public override void Write(Utf8JsonWriter writer, OptionalDateTimeOffset value, JsonSerializerOptions options)
    {
        if (value.Value.HasValue)
        {
            writer.WriteStringValue(value.Value.Value);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
public sealed record TaskBlockedStateRequest(bool IsBlocked, string? Reason, long ExpectedVersion);
public sealed record TaskRelationshipUserRequest(Guid? UserId, long ExpectedVersion);
public sealed record TaskTargetGroupRequest(Guid? GroupId, long ExpectedVersion);
public sealed record TaskCollaboratorRequest(Guid UserId, long ExpectedVersion);
public sealed record TaskReviewRequest(long ExpectedVersion, string? Reason = null, Guid? ReturnWorkflowStageId = null);
public sealed record TaskClaimRequest(long ExpectedVersion);
public sealed record TaskRestoreRequest(long ExpectedVersion);
public sealed record TaskDeleteRequest(long ExpectedVersion);
public sealed record TaskWatchStateResponse(bool IsWatching, bool IsExplicitOptOut, string[] AutomaticSources, long Version);
public sealed record TaskWatchRequest([param: System.ComponentModel.DataAnnotations.Range(typeof(long), "0", "9223372036854775807")] long ExpectedVersion);

/// <summary>
/// Gantt scheduling owns only day-precision planned dates. MilestoneDate is
/// applicable only to the compatibility Milestone aggregate.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TaskScheduleUpdateRequest(
    [property: JsonRequired] DateOnly? PlannedStartDate,
    [property: JsonRequired] DateOnly? PlannedEndDate,
    DateOnly? MilestoneDate,
    long ExpectedVersion);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TaskProgressUpdateRequest(int? ProgressPercent, long ExpectedVersion);

public sealed record GanttEditCommandResponse(
    Guid TaskId,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] WorkItemKind Kind,
    DateOnly? PlannedStartDate,
    DateOnly? PlannedEndDate,
    DateOnly? MilestoneDate,
    int ProgressPercent,
    long Version,
    IReadOnlyList<GanttWarningResponse> Warnings);

public sealed record TaskPersonSummary(Guid UserId, string DisplayName);
public sealed record TaskRelationshipsResponse(
    TaskPersonSummary? PrimaryAssignee,
    Guid? TargetGroupId,
    IReadOnlyList<TaskPersonSummary> Collaborators,
    TaskPersonSummary? Reviewer,
    long Version);

public sealed record CanonicalTaskResponse(
    Guid Id,
    Guid TenantId,
    Guid WorkspaceId,
    Guid ProjectId,
    WorkItemKind Kind,
    Guid? ParentTaskId,
    Guid? MilestoneId,
    string Title,
    string? Description,
    Guid? WorkflowStageId,
    string WorkflowStageName,
    TaskStageCategory StageCategory,
    string Priority,
    bool IsBlocked,
    string? BlockedReason,
    DateOnly? PlannedStartDate,
    DateOnly? PlannedEndDate,
    DateTimeOffset? DeadlineAt,
    DateTimeOffset? ActualStartAt,
    DateTimeOffset? CompletedAt,
    int ProgressPercent,
    bool ProgressIsDerived,
    int? EstimatedEffortMinutes,
    TaskPersonSummary? PrimaryAssignee,
    Guid? TargetGroupId,
    int CollaboratorCount,
    TaskPersonSummary? Reviewer,
    bool IsOverdue,
    IReadOnlyList<string> DependencyWarnings,
    long Version,
    TaskCommandPermissions UiPermissions,
    IReadOnlyList<TaskStageCategory> AllowedTransitions,
    TaskReviewStatus ReviewStatus,
    TaskSubresourceSummary? Subresources = null,
    TaskBriefResponse? Brief = null);

public sealed record TaskCommandPermissions(bool CanUpdate, bool CanAssign, bool CanDelete, bool CanReview, bool CanOverrideReview, bool CanClaim);
public sealed record TaskCommandResponse(CanonicalTaskResponse Task, IReadOnlyList<string> Warnings, bool OverrideApplied = false);
public sealed record TaskDetailPermissions(
    bool CanCreateSubtask,
    bool CanCreateChecklistItem,
    bool CanUpdateChecklistItems,
    bool CanDeleteChecklistItems,
    bool CanReorderChecklist,
    bool CanCreateComment,
    bool CanMarkCommentImportant,
    bool CanApplyLabels,
    bool CanManageLabelDefinitions,
    bool CanAssociateFiles,
    bool CanRemoveFiles,
    bool CanChangeWatch);

public sealed record CanonicalTaskDetailResponse(
    CanonicalTaskResponse Task,
    TaskRelationshipsResponse Relationships,
    TaskDetailPermissions Permissions,
    IReadOnlyList<TaskChecklistResponse> Checklist,
    IReadOnlyList<ProjectTaskLabelResponse> Labels,
    TaskWatchStateResponse WatchState,
    TaskSubtaskPage Subtasks,
    TaskCommentPage Comments,
    TaskFileAssociationPage Files);
