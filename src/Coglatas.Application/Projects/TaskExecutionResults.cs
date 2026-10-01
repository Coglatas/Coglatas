using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Files;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;

namespace Coglatas.Application.Projects;

public sealed record TaskExecutionPersistedResult(
    Guid Id,
    Guid TenantId,
    Guid WorkspaceId,
    Guid ProjectId,
    Guid TaskItemId,
    Guid TaskExecutionRunId,
    int SchemaVersion,
    string Status,
    string Title,
    string BodyMarkdown,
    string ContentSha256,
    DateTimeOffset CompletedAtUtc,
    DateTimeOffset CreatedAtUtc);

public sealed record TaskExecutionResultSourceReference(
    Guid MaterializedSourceId,
    Guid FileObjectId,
    Guid AttachmentId,
    string ContentSha256,
    string MediaType,
    long MaterializedByteCount);

public interface ITaskExecutionResultRepository
{
    Task<TaskExecutionPersistedResult?> GetByRunAsync(
        Guid runId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TaskExecutionResultSourceReference>> ListSourceReferencesAsync(
        Guid resultId,
        CancellationToken cancellationToken = default);
}

public sealed class UnavailableTaskExecutionResultRepository : ITaskExecutionResultRepository
{
    public Task<TaskExecutionPersistedResult?> GetByRunAsync(
        Guid runId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<TaskExecutionPersistedResult?>(null);

    public Task<IReadOnlyList<TaskExecutionResultSourceReference>> ListSourceReferencesAsync(
        Guid resultId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TaskExecutionResultSourceReference>>([]);
}

public sealed record TaskExecutionReportSourceInput(
    Guid ProvenanceId,
    string MediaType,
    string ContentSha256,
    long ByteCount,
    DateTimeOffset MaterializedAtUtc,
    string? Text = null);

public sealed record TaskExecutionReportDocument(
    int SchemaVersion,
    string Title,
    string BodyMarkdown,
    string ContentSha256);

public static class FirstPartyProjectFilesReportV1
{
    public const int SchemaVersion = 1;
    public const int MaxTitleLength = 200;
    public const int MaxBodyLength = 20_000;
    public const string Title = "Project Files Analysis Report";

    public static TaskExecutionReportDocument Build(
        IReadOnlyList<TaskExecutionReportSourceInput> sources,
        DateTimeOffset completedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count is <= 0 or > FirstPartyProjectFilesMaterializationV1.MaxSourceCount)
        {
            throw new ArgumentOutOfRangeException(nameof(sources));
        }
        if (sources.Select(source => source.ProvenanceId).Distinct().Count() != sources.Count)
        {
            throw new ArgumentException("Task execution report sources must be unique.", nameof(sources));
        }

        var builder = new StringBuilder();
        builder.AppendLine("# Project Files Analysis Report");
        builder.AppendLine();
        builder.AppendLine("This deterministic report was generated from server-authorized Project-file materialization. It contains no file names, storage locations, provider settings, credentials, or raw source body.");
        builder.AppendLine();
        builder.Append("Completed at: ").AppendLine(completedAtUtc.ToUniversalTime().ToString("O"));
        builder.AppendLine();

        long totalBytes = 0;
        for (var index = 0; index < sources.Count; index++)
        {
            var source = sources[index];
            ValidateSource(source);
            totalBytes = checked(totalBytes + source.ByteCount);
            if (totalBytes > FirstPartyProjectFilesMaterializationV1.MaxTotalBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(sources));
            }

            builder.Append("## Authorized source ").Append(index + 1).AppendLine();
            builder.AppendLine();
            builder.Append("- Media type: `").Append(source.MediaType).AppendLine("`");
            builder.Append("- Materialized bytes: ").Append(source.ByteCount).AppendLine();
            builder.Append("- Content SHA-256: `").Append(source.ContentSha256).AppendLine("`");
            builder.Append("- Materialized at: ").AppendLine(source.MaterializedAtUtc.ToUniversalTime().ToString("O"));

            if (source.Text is not null)
            {
                builder.Append("- Lines: ").Append(CountLines(source.Text)).AppendLine();
                builder.Append("- Words: ").Append(CountWords(source.Text)).AppendLine();
            }

            builder.AppendLine();
        }

        builder.AppendLine("## Aggregate");
        builder.AppendLine();
        builder.Append("- Authorized sources consumed: ").Append(sources.Count).AppendLine();
        builder.Append("- Total materialized bytes: ").Append(totalBytes).AppendLine();

        var body = builder.ToString().TrimEnd() + "\n";
        if (Title.Length > MaxTitleLength || body.Length > MaxBodyLength)
        {
            throw new InvalidOperationException("Task execution report exceeded its bounded contract.");
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        return new TaskExecutionReportDocument(SchemaVersion, Title, body, hash);
    }

    private static void ValidateSource(TaskExecutionReportSourceInput source)
    {
        if (source.ProvenanceId == Guid.Empty ||
            FirstPartyProjectFilesMaterializationV1.NormalizeSupportedMediaType(source.MediaType) != source.MediaType ||
            source.ByteCount < 0 ||
            source.ByteCount > FirstPartyProjectFilesMaterializationV1.MaxSourceBytes ||
            source.ContentSha256.Length != 64 ||
            source.ContentSha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Task execution source report input is invalid.", nameof(source));
        }
    }

    private static int CountLines(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        var count = 1;
        foreach (var character in text)
        {
            if (character == '\n')
            {
                count++;
            }
        }

        return count;
    }

    private static int CountWords(string text)
    {
        var count = 0;
        var insideWord = false;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                insideWord = false;
            }
            else if (!insideWord)
            {
                insideWord = true;
                count++;
            }
        }

        return count;
    }
}

public sealed record TaskExecutionReportResponse(
    Guid Id,
    int SchemaVersion,
    string Title,
    string BodyMarkdown,
    string ContentSha256,
    DateTimeOffset CompletedAtUtc);

public sealed record TaskExecutionResultResponse(
    Guid RunId,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] TaskExecutionRunStatus Status,
    string? FailureCode,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? QueuedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    TaskExecutionReportResponse? Report);

public interface ITaskExecutionResultService
{
    Task<Result<TaskExecutionResultResponse>> GetLatestAsync(
        Guid taskItemId,
        CancellationToken cancellationToken = default);

    Task<Result<TaskExecutionResultResponse>> GetAsync(
        Guid taskItemId,
        Guid runId,
        CancellationToken cancellationToken = default);
}

public sealed class TaskExecutionResultService(
    IProjectRepository projects,
    IProjectAuthorizationService projectAuthorization,
    ITaskExecutionScopeRepository executionScopes,
    ITaskExecutionResultRepository results,
    IFileRepository files,
    IFileAuthorizationService fileAuthorization,
    ICurrentUser currentUser) : ITaskExecutionResultService
{
    public async Task<Result<TaskExecutionResultResponse>> GetLatestAsync(
        Guid taskItemId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var task = await VisibleTaskAsync(taskItemId, cancellationToken);
            if (task is null)
            {
                return NotFound();
            }

            var run = await executionScopes.GetLatestRunAsync(task.Id, cancellationToken);
            return run is null || !MatchesTask(run, task)
                ? NotFound()
                : await BuildResponseAsync(task, run, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Unavailable();
        }
    }

    public async Task<Result<TaskExecutionResultResponse>> GetAsync(
        Guid taskItemId,
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var task = await VisibleTaskAsync(taskItemId, cancellationToken);
            if (task is null || runId == Guid.Empty)
            {
                return NotFound();
            }

            var run = await executionScopes.GetRunAsync(runId, cancellationToken);
            return run is null || !MatchesTask(run, task)
                ? NotFound()
                : await BuildResponseAsync(task, run, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Unavailable();
        }
    }

    private async Task<Result<TaskExecutionResultResponse>> BuildResponseAsync(
        TaskItem task,
        TaskExecutionRun run,
        CancellationToken cancellationToken)
    {
        if (run.Status != TaskExecutionRunStatus.Succeeded)
        {
            return Result<TaskExecutionResultResponse>.Success(ToResponse(run, null));
        }

        var persisted = await results.GetByRunAsync(run.Id, cancellationToken);
        if (persisted is null || !MatchesRun(persisted, run))
        {
            return Unavailable();
        }

        var references = await results.ListSourceReferencesAsync(persisted.Id, cancellationToken);
        if (references.Count is <= 0 or > FirstPartyProjectFilesMaterializationV1.MaxSourceCount ||
            references.Select(reference => reference.MaterializedSourceId).Distinct().Count() != references.Count ||
            references.Select(reference => reference.AttachmentId).Distinct().Count() != references.Count ||
            !await CanReadEverySourceAsync(task, run, references, cancellationToken))
        {
            return NotFound();
        }

        var report = new TaskExecutionReportResponse(
            persisted.Id,
            persisted.SchemaVersion,
            persisted.Title,
            persisted.BodyMarkdown,
            persisted.ContentSha256,
            persisted.CompletedAtUtc);
        return Result<TaskExecutionResultResponse>.Success(ToResponse(run, report));
    }

    private async Task<bool> CanReadEverySourceAsync(
        TaskItem task,
        TaskExecutionRun run,
        IReadOnlyList<TaskExecutionResultSourceReference> references,
        CancellationToken cancellationToken)
    {
        var actor = currentUser.UserId ?? Guid.Empty;
        foreach (var reference in references)
        {
            var attachment = await files.GetAttachmentAsync(reference.AttachmentId, cancellationToken);
            if (attachment is null ||
                attachment.Id != reference.AttachmentId ||
                attachment.FileObjectId != reference.FileObjectId ||
                attachment.TenantId != run.TenantId ||
                attachment.WorkspaceId != run.WorkspaceId ||
                attachment.OwnerType != AttachmentOwnerType.TaskItem ||
                attachment.OwnerId != task.Id ||
                attachment.DeletedAt.HasValue ||
                attachment.ScanStatus != FileScanStatus.Clean ||
                attachment.FileObject is not { } fileObject ||
                fileObject.Id != reference.FileObjectId ||
                fileObject.TenantId != run.TenantId ||
                fileObject.WorkspaceId != run.WorkspaceId ||
                fileObject.ProjectId != run.ProjectId ||
                fileObject.DeletedAt.HasValue ||
                fileObject.Status != FileObjectStatus.Active ||
                fileObject.SizeBytes != reference.MaterializedByteCount ||
                FirstPartyProjectFilesMaterializationV1.NormalizeSupportedMediaType(fileObject.ContentType) != reference.MediaType ||
                (!string.IsNullOrWhiteSpace(fileObject.HashSha256) &&
                 !string.Equals(fileObject.HashSha256.Trim(), reference.ContentSha256, StringComparison.OrdinalIgnoreCase)) ||
                !await fileAuthorization.CanViewAttachment(actor, attachment, cancellationToken))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<TaskItem?> VisibleTaskAsync(Guid taskItemId, CancellationToken cancellationToken)
    {
        var actor = currentUser.UserId ?? Guid.Empty;
        if (!currentUser.IsAuthenticated || actor == Guid.Empty || taskItemId == Guid.Empty)
        {
            return null;
        }

        var task = await projects.GetTaskAsync(taskItemId, cancellationToken);
        return task is { DeletedAt: null } &&
               await projectAuthorization.CanViewProject(actor, task.ProjectId, cancellationToken)
            ? task
            : null;
    }

    private static bool MatchesTask(TaskExecutionRun run, TaskItem task) =>
        run.TaskItemId == task.Id &&
        run.TenantId == task.TenantId &&
        run.WorkspaceId == task.WorkspaceId &&
        run.ProjectId == task.ProjectId;

    private static bool MatchesRun(TaskExecutionPersistedResult result, TaskExecutionRun run) =>
        result.TaskExecutionRunId == run.Id &&
        result.TenantId == run.TenantId &&
        result.WorkspaceId == run.WorkspaceId &&
        result.ProjectId == run.ProjectId &&
        result.TaskItemId == run.TaskItemId &&
        result.SchemaVersion == FirstPartyProjectFilesReportV1.SchemaVersion &&
        string.Equals(result.Status, nameof(TaskExecutionRunStatus.Succeeded), StringComparison.Ordinal) &&
        string.Equals(result.Title, FirstPartyProjectFilesReportV1.Title, StringComparison.Ordinal) &&
        result.Title.Length is > 0 and <= FirstPartyProjectFilesReportV1.MaxTitleLength &&
        result.BodyMarkdown.Length is > 0 and <= FirstPartyProjectFilesReportV1.MaxBodyLength &&
        result.ContentSha256.Length == 64 &&
        string.Equals(
            result.ContentSha256,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(result.BodyMarkdown))).ToLowerInvariant(),
            StringComparison.Ordinal) &&
        run.StartedAtUtc.HasValue &&
        run.FinishedAtUtc.HasValue &&
        result.CompletedAtUtc >= run.StartedAtUtc.Value &&
        result.CompletedAtUtc <= run.FinishedAtUtc.Value &&
        result.CreatedAtUtc == result.CompletedAtUtc;

    private static TaskExecutionResultResponse ToResponse(
        TaskExecutionRun run,
        TaskExecutionReportResponse? report) => new(
        run.Id,
        run.Status,
        SafeFailureCode(run.FailureCode),
        run.RequestedAtUtc,
        run.QueuedAtUtc,
        run.StartedAtUtc,
        run.FinishedAtUtc,
        report);

    private static string? SafeFailureCode(string? failureCode)
    {
        if (string.IsNullOrWhiteSpace(failureCode))
        {
            return null;
        }

        var normalized = failureCode.Trim();
        return normalized.Length <= 100 && normalized.All(character =>
            (character >= 'A' && character <= 'Z') ||
            (character >= '0' && character <= '9') ||
            character == '_')
            ? normalized
            : "TASK_EXECUTION_FAILED";
    }

    private static Result<TaskExecutionResultResponse> NotFound() =>
        Result<TaskExecutionResultResponse>.Failure(new ApplicationErrorDetail(
            "TASK_EXECUTION_RESULT_NOT_FOUND",
            "The execution result was not found."));

    private static Result<TaskExecutionResultResponse> Unavailable() =>
        Result<TaskExecutionResultResponse>.Failure(new ApplicationErrorDetail(
            "TASK_EXECUTION_RESULT_UNAVAILABLE",
            "The execution result is temporarily unavailable."));
}
