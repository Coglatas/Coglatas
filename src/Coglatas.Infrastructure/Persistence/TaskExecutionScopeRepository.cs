using System.Data;
using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Projects;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Coglatas.Infrastructure.Persistence;

public sealed class TaskExecutionScopeRepository(AppDbContext dbContext) : ITaskExecutionScopeRepository
{
    private static readonly JsonSerializerOptions PolicyJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly Dictionary<(TaskExecutionSourcePolicyOwnerType Type, Guid Id), TaskExecutionSourcePolicyDocument> _pendingUpserts = [];
    private readonly HashSet<(TaskExecutionSourcePolicyOwnerType Type, Guid Id)> _pendingDeletes = [];

    public Task<ProjectExecutionScope?> GetProjectScopeAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        dbContext.ProjectExecutionScopes.AsNoTracking().SingleOrDefaultAsync(scope => scope.ProjectId == projectId, cancellationToken);

    public Task<ProjectExecutionScope?> GetProjectScopeForUpdateAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        dbContext.ProjectExecutionScopes.SingleOrDefaultAsync(scope => scope.ProjectId == projectId, cancellationToken);

    public Task<TaskExecutionScopeOverride?> GetTaskOverrideAsync(Guid taskItemId, CancellationToken cancellationToken = default) =>
        dbContext.TaskExecutionScopeOverrides.AsNoTracking().SingleOrDefaultAsync(scope => scope.TaskItemId == taskItemId, cancellationToken);

    public Task<TaskExecutionScopeOverride?> GetTaskOverrideForUpdateAsync(Guid taskItemId, CancellationToken cancellationToken = default) =>
        dbContext.TaskExecutionScopeOverrides.SingleOrDefaultAsync(scope => scope.TaskItemId == taskItemId, cancellationToken);

    public Task<TaskExecutionRun?> GetLatestRunAsync(Guid taskItemId, CancellationToken cancellationToken = default) =>
        dbContext.TaskExecutionRuns.AsNoTracking()
            .Where(run => run.TaskItemId == taskItemId)
            .OrderByDescending(run => run.RequestedAtUtc)
            .ThenByDescending(run => run.Id)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<TaskExecutionRun?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default) =>
        dbContext.TaskExecutionRuns.AsNoTracking().SingleOrDefaultAsync(run => run.Id == runId, cancellationToken);

    public Task AddProjectScopeAsync(ProjectExecutionScope scope, CancellationToken cancellationToken = default)
    {
        dbContext.ProjectExecutionScopes.Add(scope);
        return Task.CompletedTask;
    }

    public Task AddTaskOverrideAsync(TaskExecutionScopeOverride scope, CancellationToken cancellationToken = default)
    {
        dbContext.TaskExecutionScopeOverrides.Add(scope);
        return Task.CompletedTask;
    }

    public Task AddRunAsync(TaskExecutionRun run, CancellationToken cancellationToken = default)
    {
        dbContext.TaskExecutionRuns.Add(run);
        return Task.CompletedTask;
    }

    public void RemoveTaskOverride(TaskExecutionScopeOverride scope) => dbContext.TaskExecutionScopeOverrides.Remove(scope);

    public async Task<TaskExecutionSourcePolicyDocument?> GetSourcePolicyDocumentAsync(
        TaskExecutionSourcePolicyOwnerType ownerType,
        Guid ownerId,
        CancellationToken cancellationToken = default)
    {
        if (_pendingDeletes.Contains((ownerType, ownerId))) return null;
        if (_pendingUpserts.TryGetValue((ownerType, ownerId), out var staged)) return staged;
        if (!UsesPostgreSql() || ownerId == Guid.Empty) return null;

        var connection = dbContext.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose) await connection.OpenAsync(cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = """
                SELECT "TenantId", "WorkspaceId", "ProjectId", "TaskItemId",
                       "ProjectScopeVersion", "TaskOverrideVersion", "PolicyJson"::text
                FROM task_execution_source_policy_documents
                WHERE "OwnerType" = @ownerType AND "OwnerId" = @ownerId
                """;
            AddParameter(command, "ownerType", ownerType.ToString());
            AddParameter(command, "ownerId", ownerId);

            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;

            var policy = JsonSerializer.Deserialize<TaskExecutionSourcePolicyV2>(reader.GetString(6), PolicyJsonOptions)
                ?? throw new InvalidDataException("Stored Task execution source policy is missing.");
            if (!policy.TryNormalize(out policy, out _, out _))
                throw new InvalidDataException("Stored Task execution source policy is invalid.");

            return new TaskExecutionSourcePolicyDocument(
                ownerType,
                ownerId,
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3),
                reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                policy);
        }
        finally
        {
            if (shouldClose) await connection.CloseAsync();
        }
    }

    public void StageSourcePolicyDocument(TaskExecutionSourcePolicyDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.OwnerId == Guid.Empty || document.TenantId == Guid.Empty || document.WorkspaceId == Guid.Empty || document.ProjectId == Guid.Empty)
            throw new ArgumentException("Source-policy scope identifiers must be non-empty.", nameof(document));
        if (!document.Policy.TryNormalize(out var normalized, out _, out _))
            throw new ArgumentException("Source-policy document is invalid.", nameof(document));

        _pendingDeletes.Remove((document.OwnerType, document.OwnerId));
        _pendingUpserts[(document.OwnerType, document.OwnerId)] = document with { Policy = normalized };
    }

    public void StageSourcePolicyDocumentDelete(TaskExecutionSourcePolicyOwnerType ownerType, Guid ownerId)
    {
        if (ownerType == TaskExecutionSourcePolicyOwnerType.Run)
            throw new InvalidOperationException("Run source-policy snapshots are immutable.");
        _pendingUpserts.Remove((ownerType, ownerId));
        _pendingDeletes.Add((ownerType, ownerId));
    }

    public bool HasPendingSourcePolicyDocuments => _pendingUpserts.Count > 0 || _pendingDeletes.Count > 0;

    public async Task FlushPendingSourcePolicyDocumentsAsync(CancellationToken cancellationToken = default)
    {
        if (!HasPendingSourcePolicyDocuments) return;
        if (!UsesPostgreSql())
        {
            ClearPendingSourcePolicyDocuments();
            return;
        }

        var connection = dbContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(cancellationToken);

        foreach (var key in _pendingDeletes.OrderBy(item => item.Type).ThenBy(item => item.Id))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = """
                DELETE FROM task_execution_source_policy_documents
                WHERE "OwnerType" = @ownerType AND "OwnerId" = @ownerId
                """;
            AddParameter(command, "ownerType", key.Type.ToString());
            AddParameter(command, "ownerId", key.Id);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var document in _pendingUpserts.Values.OrderBy(item => item.OwnerType).ThenBy(item => item.OwnerId))
        {
            if (document.OwnerType != TaskExecutionSourcePolicyOwnerType.Run &&
                !await ItemIdentitiesBelongToPolicyScopeAsync(document, cancellationToken))
            {
                throw new InvalidOperationException("One or more source-policy item identities are outside the authorized policy scope.");
            }

            await using var command = connection.CreateCommand();
            command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = document.OwnerType == TaskExecutionSourcePolicyOwnerType.Run
                ? """
                    INSERT INTO task_execution_source_policy_documents
                        ("OwnerType", "OwnerId", "TenantId", "WorkspaceId", "ProjectId", "TaskItemId",
                         "PolicySchemaVersion", "ProjectScopeVersion", "TaskOverrideVersion", "PolicyJson")
                    VALUES
                        (@ownerType, @ownerId, @tenantId, @workspaceId, @projectId, @taskItemId,
                         @schemaVersion, @projectScopeVersion, @taskOverrideVersion, CAST(@policyJson AS jsonb))
                    """
                : """
                    INSERT INTO task_execution_source_policy_documents
                        ("OwnerType", "OwnerId", "TenantId", "WorkspaceId", "ProjectId", "TaskItemId",
                         "PolicySchemaVersion", "ProjectScopeVersion", "TaskOverrideVersion", "PolicyJson")
                    VALUES
                        (@ownerType, @ownerId, @tenantId, @workspaceId, @projectId, @taskItemId,
                         @schemaVersion, @projectScopeVersion, @taskOverrideVersion, CAST(@policyJson AS jsonb))
                    ON CONFLICT ("OwnerType", "OwnerId") DO UPDATE SET
                        "TenantId" = EXCLUDED."TenantId", "WorkspaceId" = EXCLUDED."WorkspaceId",
                        "ProjectId" = EXCLUDED."ProjectId", "TaskItemId" = EXCLUDED."TaskItemId",
                        "PolicySchemaVersion" = EXCLUDED."PolicySchemaVersion",
                        "ProjectScopeVersion" = EXCLUDED."ProjectScopeVersion",
                        "TaskOverrideVersion" = EXCLUDED."TaskOverrideVersion",
                        "PolicyJson" = EXCLUDED."PolicyJson", "UpdatedAt" = NOW()
                    """;
            AddParameter(command, "ownerType", document.OwnerType.ToString());
            AddParameter(command, "ownerId", document.OwnerId);
            AddParameter(command, "tenantId", document.TenantId);
            AddParameter(command, "workspaceId", document.WorkspaceId);
            AddParameter(command, "projectId", document.ProjectId);
            AddParameter(command, "taskItemId", document.TaskItemId);
            AddParameter(command, "schemaVersion", TaskExecutionSourcePolicyV2.CurrentSchemaVersion);
            AddParameter(command, "projectScopeVersion", document.ProjectScopeVersion);
            AddParameter(command, "taskOverrideVersion", document.TaskOverrideVersion);
            AddParameter(command, "policyJson", JsonSerializer.Serialize(document.Policy, PolicyJsonOptions));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        ClearPendingSourcePolicyDocuments();
    }

    public void ClearPendingSourcePolicyDocuments()
    {
        _pendingUpserts.Clear();
        _pendingDeletes.Clear();
    }

    public async Task<IReadOnlyList<Attachment>> ListProjectSourceAttachmentsAsync(
        Guid projectId,
        CancellationToken cancellationToken = default) =>
        await dbContext.Set<Attachment>()
            .AsNoTracking()
            .Include(attachment => attachment.FileObject)
            .Where(attachment =>
                attachment.OwnerType == AttachmentOwnerType.TaskItem &&
                !attachment.DeletedAt.HasValue &&
                attachment.ScanStatus == FileScanStatus.Clean &&
                attachment.FileObject != null &&
                attachment.FileObject.ProjectId == projectId &&
                !attachment.FileObject.DeletedAt.HasValue &&
                attachment.FileObject.Status == FileObjectStatus.Active)
            .OrderBy(attachment => attachment.CreatedAt)
            .ThenBy(attachment => attachment.Id)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Attachment>> ListTaskSourceAttachmentsAsync(
        Guid taskItemId,
        CancellationToken cancellationToken = default) =>
        await dbContext.Set<Attachment>()
            .AsNoTracking()
            .Include(attachment => attachment.FileObject)
            .Where(attachment =>
                attachment.OwnerType == AttachmentOwnerType.TaskItem &&
                attachment.OwnerId == taskItemId &&
                !attachment.DeletedAt.HasValue &&
                attachment.ScanStatus == FileScanStatus.Clean &&
                attachment.FileObject != null &&
                !attachment.FileObject.DeletedAt.HasValue &&
                attachment.FileObject.Status == FileObjectStatus.Active)
            .OrderBy(attachment => attachment.CreatedAt)
            .ThenBy(attachment => attachment.Id)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<IntegrationAccount>> ListActiveIntegrationAccountsAsync(
        CancellationToken cancellationToken = default) =>
        await dbContext.Set<IntegrationAccount>()
            .AsNoTracking()
            .Where(account => account.DeletedAt == null && account.Status != IntegrationAccountStatus.Deleted)
            .OrderBy(account => account.DisplayName)
            .ThenBy(account => account.Id)
            .ToListAsync(cancellationToken);

    private async Task<bool> ItemIdentitiesBelongToPolicyScopeAsync(
        TaskExecutionSourcePolicyDocument document,
        CancellationToken cancellationToken)
    {
        foreach (var rule in document.Policy.Items)
        {
            switch (rule.Kind)
            {
                case TaskExecutionSourceKind.ProjectFile:
                    if (!TaskExecutionSourcePolicyV2.TryParseProjectFileSourceId(rule.SourceId, out var fileId))
                        return false;

                    if (document.OwnerType == TaskExecutionSourcePolicyOwnerType.Task)
                    {
                        if (!document.TaskItemId.HasValue) return false;
                        var taskOwnsFile = await dbContext.Set<Attachment>()
                            .AsNoTracking()
                            .AnyAsync(attachment =>
                                attachment.TenantId == document.TenantId &&
                                attachment.WorkspaceId == document.WorkspaceId &&
                                attachment.OwnerType == AttachmentOwnerType.TaskItem &&
                                attachment.OwnerId == document.TaskItemId.Value &&
                                attachment.FileObjectId == fileId &&
                                !attachment.DeletedAt.HasValue &&
                                attachment.ScanStatus == FileScanStatus.Clean &&
                                attachment.FileObject != null &&
                                attachment.FileObject.TenantId == document.TenantId &&
                                attachment.FileObject.WorkspaceId == document.WorkspaceId &&
                                attachment.FileObject.ProjectId == document.ProjectId &&
                                !attachment.FileObject.DeletedAt.HasValue &&
                                attachment.FileObject.Status == FileObjectStatus.Active,
                                cancellationToken);
                        if (!taskOwnsFile) return false;
                    }
                    else
                    {
                        var projectOwnsFile = await dbContext.Set<FileObject>()
                            .AsNoTracking()
                            .AnyAsync(file =>
                                file.Id == fileId &&
                                file.TenantId == document.TenantId &&
                                file.WorkspaceId == document.WorkspaceId &&
                                file.ProjectId == document.ProjectId &&
                                !file.DeletedAt.HasValue &&
                                file.Status == FileObjectStatus.Active,
                                cancellationToken);
                        if (!projectOwnsFile) return false;
                    }
                    break;

                case TaskExecutionSourceKind.ConnectedApp:
                    if (!TaskExecutionSourcePolicyV2.TryParseConnectedAppSourceId(rule.SourceId, out var accountId))
                        return false;
                    var accountExists = await dbContext.Set<IntegrationAccount>()
                        .AsNoTracking()
                        .AnyAsync(account =>
                            account.Id == accountId &&
                            account.TenantId == document.TenantId &&
                            account.DeletedAt == null &&
                            account.Status != IntegrationAccountStatus.Deleted,
                            cancellationToken);
                    if (!accountExists) return false;
                    break;

                case TaskExecutionSourceKind.Web:
                case TaskExecutionSourceKind.WebSite:
                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    private bool UsesPostgreSql() =>
        dbContext.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
