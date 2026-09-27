using System.Data;
using System.Data.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Projects;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Coglatas.Infrastructure.TaskExecution;

public sealed partial class DurableTaskExecutionResultRuntime
{
    private async Task InsertProvenanceAsync(
        TaskExecutionRun run,
        IReadOnlyList<RuntimeSource> sources,
        CancellationToken cancellationToken)
    {
        foreach (var source in sources)
        {
            var provenance = new TaskExecutionMaterializedSource
            {
                TenantId = run.TenantId,
                WorkspaceId = run.WorkspaceId,
                ProjectId = run.ProjectId,
                TaskItemId = run.TaskItemId,
                TaskExecutionRunId = run.Id,
                FileObjectId = source.FileObjectId,
                AttachmentId = source.AttachmentId,
                SchemaVersion = TaskExecutionMaterializedSource.SchemaVersion1,
                ContentSha256 = source.ReportSource.ContentSha256,
                MediaType = source.ReportSource.MediaType,
                MaterializedByteCount = source.ReportSource.ByteCount,
                MaterializedAtUtc = source.ReportSource.MaterializedAtUtc
            };

            await using var command = CreateCommand("""
                INSERT INTO task_execution_materialized_sources (
                    "Id", "TenantId", "WorkspaceId", "ProjectId", "TaskItemId",
                    "TaskExecutionRunId", "FileObjectId", "AttachmentId", "SchemaVersion",
                    "ContentSha256", "MediaType", "MaterializedByteCount", "MaterializedAtUtc")
                VALUES (
                    @id, @tenantId, @workspaceId, @projectId, @taskItemId,
                    @runId, @fileObjectId, @attachmentId, @schemaVersion,
                    @contentSha256, @mediaType, @byteCount, @materializedAtUtc);
                """);
            AddParameter(command, "id", source.ProvenanceId);
            AddParameter(command, "tenantId", provenance.TenantId);
            AddParameter(command, "workspaceId", provenance.WorkspaceId);
            AddParameter(command, "projectId", provenance.ProjectId);
            AddParameter(command, "taskItemId", provenance.TaskItemId);
            AddParameter(command, "runId", provenance.TaskExecutionRunId);
            AddParameter(command, "fileObjectId", provenance.FileObjectId);
            AddParameter(command, "attachmentId", provenance.AttachmentId);
            AddParameter(command, "schemaVersion", provenance.SchemaVersion);
            AddParameter(command, "contentSha256", provenance.ContentSha256);
            AddParameter(command, "mediaType", provenance.MediaType);
            AddParameter(command, "byteCount", provenance.MaterializedByteCount);
            AddParameter(command, "materializedAtUtc", provenance.MaterializedAtUtc);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task<Guid> InsertResultAsync(
        TaskExecutionRun run,
        TaskExecutionReportDocument document,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        var resultId = Guid.NewGuid();
        await using var command = CreateCommand("""
            INSERT INTO task_execution_results (
                "Id", "TenantId", "WorkspaceId", "ProjectId", "TaskItemId",
                "TaskExecutionRunId", "SchemaVersion", "Status", "Title",
                "BodyMarkdown", "ContentSha256", "CompletedAtUtc", "CreatedAtUtc")
            VALUES (
                @id, @tenantId, @workspaceId, @projectId, @taskItemId,
                @runId, @schemaVersion, 'Succeeded', @title,
                @bodyMarkdown, @contentSha256, @completedAtUtc, @createdAtUtc);
            """);
        AddParameter(command, "id", resultId);
        AddParameter(command, "tenantId", run.TenantId);
        AddParameter(command, "workspaceId", run.WorkspaceId);
        AddParameter(command, "projectId", run.ProjectId);
        AddParameter(command, "taskItemId", run.TaskItemId);
        AddParameter(command, "runId", run.Id);
        AddParameter(command, "schemaVersion", document.SchemaVersion);
        AddParameter(command, "title", document.Title);
        AddParameter(command, "bodyMarkdown", document.BodyMarkdown);
        AddParameter(command, "contentSha256", document.ContentSha256);
        AddParameter(command, "completedAtUtc", completedAt);
        AddParameter(command, "createdAtUtc", completedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return resultId;
    }

    private async Task InsertResultLinksAsync(
        TaskExecutionRun run,
        Guid resultId,
        IReadOnlyList<RuntimeSource> sources,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < sources.Count; index++)
        {
            await using var command = CreateCommand("""
                INSERT INTO task_execution_result_sources (
                    "Id", "TenantId", "TaskExecutionResultId", "MaterializedSourceId", "Ordinal")
                VALUES (@id, @tenantId, @resultId, @sourceId, @ordinal);
                """);
            AddParameter(command, "id", Guid.NewGuid());
            AddParameter(command, "tenantId", run.TenantId);
            AddParameter(command, "resultId", resultId);
            AddParameter(command, "sourceId", sources[index].ProvenanceId);
            AddParameter(command, "ordinal", index + 1);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task SucceedRunAsync(
        TaskExecutionRun run,
        Guid resultId,
        TaskExecutionReportDocument? document,
        CancellationToken cancellationToken)
    {
        if (run.Status != TaskExecutionRunStatus.Running)
        {
            return;
        }

        run.Status = TaskExecutionRunStatus.Succeeded;
        run.FailureCode = null;
        run.FinishedAtUtc = clock.UtcNow;
        run.VersionNo++;
        await audit.LogAsync(new AuditLogEntry(
            run.RequestedByUserId,
            "TaskExecutionRunSucceeded",
            "TaskExecutionRun",
            run.Id,
            WorkspaceId: run.WorkspaceId,
            ProjectId: run.ProjectId,
            TenantId: run.TenantId,
            Metadata: new Dictionary<string, object?>
            {
                ["resultId"] = resultId,
                ["resultSchemaVersion"] = FirstPartyProjectFilesReportV1.SchemaVersion,
                ["resultContentSha256"] = document?.ContentSha256
            }), cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task FailRunAsync(
        TaskExecutionRun run,
        string failureCode,
        CancellationToken cancellationToken)
    {
        if (run.Status != TaskExecutionRunStatus.Running)
        {
            return;
        }

        run.Status = TaskExecutionRunStatus.Failed;
        run.FailureCode = failureCode.Length <= 100 ? failureCode : GenericFailureCode;
        run.FinishedAtUtc = clock.UtcNow;
        run.VersionNo++;
        await AuditLifecycleAsync(run, "TaskExecutionRunFailed", cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task FailAfterUnexpectedErrorAsync(
        TaskExecutionRuntimeHandle handle,
        CancellationToken cancellationToken)
    {
        if (!IsCurrentTenant(handle))
        {
            return;
        }

        try
        {
            dbContext.ChangeTracker.Clear();
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            var run = await LockRunAsync(handle.RunId, cancellationToken);
            if (!MatchesHandle(run, handle) || TaskExecutionRunLifecycle.IsTerminal(run!.Status))
            {
                await transaction.CommitAsync(cancellationToken);
                return;
            }

            await TaskExecutionRunStateTransitions.AdvanceToRunningAsync(
                run,
                dbContext,
                clock,
                AuditLifecycleAsync,
                cancellationToken);

            if (run.Status == TaskExecutionRunStatus.Running)
            {
                await FailRunAsync(run, GenericFailureCode, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            // Keep the accepted run durable and never surface source/provider or
            // database diagnostics through the HTTP response.
        }
    }

    private async Task<TaskExecutionRun?> LockRunAsync(
        Guid runId,
        CancellationToken cancellationToken)
    {
        if (dbContext.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
        {
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""SELECT 1 FROM task_execution_runs WHERE "Id" = {runId} AND "TenantId" = {currentTenant.TenantId} FOR UPDATE""",
                cancellationToken);
        }

        return await dbContext.Set<TaskExecutionRun>()
            .SingleOrDefaultAsync(run => run.Id == runId, cancellationToken);
    }

    private async Task<Guid?> GetExistingResultIdAsync(
        Guid runId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand("""
            SELECT "Id"
            FROM task_execution_results
            WHERE "TenantId" = @tenantId AND "TaskExecutionRunId" = @runId
            LIMIT 1;
            """);
        AddParameter(command, "tenantId", currentTenant.TenantId);
        AddParameter(command, "runId", runId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is Guid id ? id : null;
    }

    private async Task<long> CountResultSourcesAsync(
        Guid resultId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand("""
            SELECT COUNT(*)
            FROM task_execution_result_sources
            WHERE "TenantId" = @tenantId AND "TaskExecutionResultId" = @resultId;
            """);
        AddParameter(command, "tenantId", currentTenant.TenantId);
        AddParameter(command, "resultId", resultId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private async Task<IReadOnlyList<RuntimeSource>> LoadExistingProvenanceAsync(
        TaskExecutionRun run,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand("""
            SELECT "Id", "FileObjectId", "AttachmentId", "MediaType",
                   "ContentSha256", "MaterializedByteCount", "MaterializedAtUtc"
            FROM task_execution_materialized_sources
            WHERE "TenantId" = @tenantId AND "TaskExecutionRunId" = @runId
            ORDER BY "MaterializedAtUtc", "Id";
            """);
        AddParameter(command, "tenantId", run.TenantId);
        AddParameter(command, "runId", run.Id);

        var sources = new List<RuntimeSource>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var provenanceId = reader.GetGuid(0);
            sources.Add(new RuntimeSource(
                provenanceId,
                reader.GetGuid(1),
                reader.GetGuid(2),
                new TaskExecutionReportSourceInput(
                    provenanceId,
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetInt64(5),
                    reader.GetFieldValue<DateTimeOffset>(6))));
        }

        return sources;
    }

    private DbCommand CreateCommand(string commandText)
    {
        var connection = dbContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException("Task execution transaction connection is not open.");
        }

        var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();
        return command;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private Task AuditLifecycleAsync(
        TaskExecutionRun run,
        string action,
        CancellationToken cancellationToken) =>
        audit.LogAsync(new AuditLogEntry(
            run.RequestedByUserId,
            action,
            "TaskExecutionRun",
            run.Id,
            WorkspaceId: run.WorkspaceId,
            ProjectId: run.ProjectId,
            TenantId: run.TenantId,
            Metadata: new Dictionary<string, object?>
            {
                ["status"] = run.Status.ToString(),
                ["runtimeProvider"] = run.RuntimeProvider.ToString(),
                ["runtimeContractVersion"] = run.RuntimeContractVersion
            }), cancellationToken);

    private bool IsCurrentTenant(TaskExecutionRuntimeHandle handle) =>
        currentTenant is { IsAvailable: true, IsPlatformScope: false } &&
        currentTenant.TenantId != Guid.Empty &&
        currentTenant.TenantId == handle.TenantId;

    private static bool MatchesHandle(
        TaskExecutionRun? run,
        TaskExecutionRuntimeHandle handle) =>
        run is
        {
            RuntimeProvider: FirstPartyProjectFilesRuntimeV1.Provider,
            RuntimeContractVersion: FirstPartyProjectFilesRuntimeV1.ContractVersion
        } &&
        run.Id == handle.RunId &&
        run.TenantId == handle.TenantId &&
        handle.RuntimeContractVersion == FirstPartyProjectFilesRuntimeV1.ContractVersion;

    private sealed record RuntimeSource(
        Guid ProvenanceId,
        Guid FileObjectId,
        Guid AttachmentId,
        TaskExecutionReportSourceInput ReportSource);

    private sealed record MaterializationOutcome(
        IReadOnlyList<RuntimeSource> Sources,
        string? FailureCode)
    {
        public static MaterializationOutcome Succeeded(IReadOnlyList<RuntimeSource> sources) =>
            new(sources, null);

        public static MaterializationOutcome Failed(string failureCode) =>
            new([], failureCode);
    }
}
