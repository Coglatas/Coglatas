using System.Text.Json;
using System.Text.Json.Serialization;

namespace Coglatas.Application.Projects;

/// <summary>Distinguishes an omitted PATCH member from an explicitly supplied JSON null.</summary>
[JsonConverter(typeof(OptionalStringJsonConverter))]
public readonly record struct OptionalString(bool IsSpecified, string? Value)
{
    public static implicit operator OptionalString(string? value) => new(true, value);
}

/// <summary>Distinguishes an omitted JSON number from an explicitly supplied null.</summary>
public readonly record struct OptionalInt64(bool IsSpecified, long? Value);

public sealed class OptionalStringJsonConverter : JsonConverter<OptionalString>
{
    public override bool HandleNull => true;
    public override OptionalString Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? new(true, null) : new(true, reader.GetString());
    public override void Write(Utf8JsonWriter writer, OptionalString value, JsonSerializerOptions options)
    {
        if (value.Value is null) writer.WriteNullValue(); else writer.WriteStringValue(value.Value);
    }
}

public sealed record TaskChecklistResponse(Guid Id, string Text, bool IsCompleted, DateTimeOffset? CompletedAt, Guid? CompletedByUserId, long SortKey, long Version);
public sealed record CreateTaskChecklistRequest(string Text);
public sealed record UpdateTaskChecklistRequest(string? Text, bool? IsCompleted, long ExpectedVersion);
public sealed record TaskCommentMentionResponse(Guid UserId, string DisplayName);
public sealed record TaskCommentResponse(Guid Id, Guid TaskId, TaskPersonSummary? Author, string? BodyPlainText, bool IsImportant, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt, DateTimeOffset? DeletedAt, long Version, bool CanEdit, bool CanDelete, bool CanMarkImportant, IReadOnlyList<TaskCommentMentionResponse> Mentions);
public sealed record TaskCommentPage(IReadOnlyList<TaskCommentResponse> Items, int Page, int PageSize, int TotalCount, bool HasMore);
public sealed record CreateTaskCommentRequest(string BodyPlainText, bool IsImportant = false);
public sealed record UpdateTaskCommentRequest(string? BodyPlainText, bool? IsImportant, long ExpectedVersion);
public sealed record ProjectTaskLabelResponse(Guid Id, string Name, string? Description, long SortKey, bool IsArchived, long Version);
public sealed record CreateProjectTaskLabelRequest(string Name, string? Description, long? SortKey = null);
[JsonConverter(typeof(UpdateProjectTaskLabelRequestJsonConverter))]
public sealed record UpdateProjectTaskLabelRequest(OptionalString Name, OptionalString Description, OptionalInt64 SortKey, long? ExpectedVersion);

/// <summary>
/// Constructor binding collapses JSON null and a missing value for value-object
/// parameters.  This converter keeps PATCH presence explicit for description.
/// </summary>
public sealed class UpdateProjectTaskLabelRequestJsonConverter : JsonConverter<UpdateProjectTaskLabelRequest>
{
    public override UpdateProjectTaskLabelRequest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("A label patch object is required.");

        var name = root.TryGetProperty("name", out var nameProperty)
            ? new OptionalString(true, ReadStringOrNull(nameProperty, "name"))
            : default;
        var description = root.TryGetProperty("description", out var descriptionProperty)
            ? new OptionalString(true, ReadStringOrNull(descriptionProperty, "description"))
            : default;
        var sortKey = root.TryGetProperty("sortKey", out var sortKeyProperty)
            ? new OptionalInt64(true, ReadInt64OrNull(sortKeyProperty, "sortKey"))
            : default;
        var expectedVersion = root.TryGetProperty("expectedVersion", out var versionProperty) ? ReadInt64OrNull(versionProperty, "expectedVersion") : null;
        return new UpdateProjectTaskLabelRequest(name, description, sortKey, expectedVersion);
    }

    public override void Write(Utf8JsonWriter writer, UpdateProjectTaskLabelRequest value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.Name.IsSpecified)
        {
            if (value.Name.Value is null) writer.WriteNull("name"); else writer.WriteString("name", value.Name.Value);
        }
        if (value.Description.IsSpecified)
        {
            if (value.Description.Value is null) writer.WriteNull("description"); else writer.WriteString("description", value.Description.Value);
        }
        if (value.SortKey.IsSpecified)
        {
            if (value.SortKey.Value.HasValue) writer.WriteNumber("sortKey", value.SortKey.Value.Value); else writer.WriteNull("sortKey");
        }
        if (value.ExpectedVersion.HasValue) writer.WriteNumber("expectedVersion", value.ExpectedVersion.Value);
        writer.WriteEndObject();
    }

    private static string? ReadStringOrNull(JsonElement property, string name) => property.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => property.GetString(),
        _ => throw new JsonException($"'{name}' must be a string or null.")
    };

    private static long? ReadInt64OrNull(JsonElement property, string name)
    {
        if (property.ValueKind == JsonValueKind.Null)
            return null;
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out var value))
            throw new JsonException($"'{name}' must be an integer or null.");
        return value;
    }
}
public sealed record TaskLabelAssociationRequest(long ExpectedVersion);
public sealed record TaskSubresourceSummary(int ChecklistCompletedCount, int ChecklistTotalCount, int CommentCount, int LabelCount, int SubtaskCount);
public sealed record TaskSubtaskResponse(Guid Id, Guid ParentTaskId, string Title, Guid? WorkflowStageId, string WorkflowStageName, string StageCategory, string Priority, int ProgressPercent, TaskPersonSummary? PrimaryAssignee, DateOnly? PlannedEndDate, DateTimeOffset? DeadlineAt, bool IsOverdue, long Version);
public sealed record TaskSubtaskPage(IReadOnlyList<TaskSubtaskResponse> Items, int Page, int PageSize, int TotalCount, bool HasMore);
public sealed record CreateTaskSubtaskRequest(
    string Title,
    string? Description,
    Domain.Enums.TaskPriority Priority = Domain.Enums.TaskPriority.Medium,
    string? Goal = null,
    string? Deliverable = null,
    string? Constraints = null);
public sealed record TaskActivityLogResponse(
    Guid Id,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] Domain.Enums.ActivityLogType ActivityType,
    string Body,
    DateTimeOffset OccurredAt,
    TaskPersonSummary Author);
public sealed record TaskActivityLogPage(IReadOnlyList<TaskActivityLogResponse> Items, int Page, int PageSize, int TotalCount, bool HasMore);
public sealed record TaskFileAssociationResponse(Guid Id, Guid FileObjectId, string FileName, string ContentType, long SizeBytes, string ScanStatus, DateTimeOffset CreatedAt, string AccessState, bool CanOpen, bool CanRequestDownloadGrant, bool DownloadGrantRequired, string? RestrictionCode);
public sealed record TaskFileAssociationPage(IReadOnlyList<TaskFileAssociationResponse> Items, int Page, int PageSize, int TotalCount, bool HasMore);
public sealed record CreateTaskFileAssociationRequest(Guid AttachmentId, long ExpectedVersion);
public sealed record ReorderTaskChecklistRequest(IReadOnlyList<Guid> OrderedItemIds, long ExpectedTaskVersion);
public sealed record TaskChecklistOrderResponse(IReadOnlyList<TaskChecklistResponse> Items, long TaskVersion);
public sealed record TaskMentionCandidateResponse(Guid UserId, string DisplayName);
