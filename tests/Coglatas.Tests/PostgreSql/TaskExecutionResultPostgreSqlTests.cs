using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Files;
using Coglatas.Application.Projects;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Persistence;
using Coglatas.Infrastructure.TaskExecution;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Coglatas.Tests.PostgreSql;

[Collection("PostgreSqlTaskV1")]
[Trait("Scope", "Issue463")]
public sealed class TaskExecutionResultPostgreSqlTests
{
    private const string PreviousMigration = "20260830150000_AddTaskExecutionMaterializedSources";

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task MigrationEnforcesOneImmutableScopedResultAndSourceSet()
    {
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(
            PostgreSqlTestEnvironment.RequireConnectionString(),
            async database =>
            {
                await PostgreSqlMigrationTestDatabase.MigrateAsync(database, PreviousMigration);
                var seeded = await SeedRunningMaterializationAsync(database, "result-contract");

                await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
                await using (var current = PostgreSqlMigrationTestDatabase.CreatePlatformContext(database))
                    Assert.Empty(await current.Database.GetPendingMigrationsAsync());

                var resultId = Guid.NewGuid();
                var completedAt = seeded.StartedAt.AddSeconds(1);
                await InsertResultAsync(database, resultId, seeded, completedAt);
                await InsertResultSourceAsync(database, Guid.NewGuid(), resultId, seeded, ordinal: 1);

                Assert.Equal(1L, await PostgreSqlMigrationTestDatabase.ScalarAsync<long>(
                    database,
                    "SELECT COUNT(*) FROM task_execution_results WHERE \"TaskExecutionRunId\" = @runId;",
                    ("runId", seeded.RunId)));
                Assert.Equal(1L, await PostgreSqlMigrationTestDatabase.ScalarAsync<long>(
                    database,
                    "SELECT COUNT(*) FROM task_execution_result_sources WHERE \"TaskExecutionResultId\" = @resultId;",
                    ("resultId", resultId)));

                var duplicate = await Assert.ThrowsAsync<PostgresException>(() =>
                    InsertResultAsync(database, Guid.NewGuid(), seeded, completedAt));
                Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);

                var duplicateSource = await Assert.ThrowsAsync<PostgresException>(() =>
                    InsertResultSourceAsync(database, Guid.NewGuid(), resultId, seeded, ordinal: 1));
                Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicateSource.SqlState);

                var immutable = await Assert.ThrowsAsync<PostgresException>(() =>
                    PostgreSqlMigrationTestDatabase.ExecuteAsync(
                        database,
                        "UPDATE task_execution_results SET \"Title\" = 'rewritten' WHERE \"Id\" = @id;",
                        ("id", resultId)));
                Assert.Equal(PostgresErrorCodes.RaiseException, immutable.SqlState);

                await PostgreSqlMigrationTestDatabase.ExecuteAsync(database, """
                    UPDATE task_execution_runs
                    SET "Status" = 'Succeeded', "FinishedAtUtc" = @finishedAt, "VersionNo" = "VersionNo" + 1
                    WHERE "Id" = @runId;
                    """, ("runId", seeded.RunId), ("finishedAt", completedAt));

                Assert.Equal("Succeeded", await PostgreSqlMigrationTestDatabase.ScalarAsync<string>(
                    database,
                    "SELECT \"Status\" FROM task_execution_runs WHERE \"Id\" = @runId;",
                    ("runId", seeded.RunId)));
            });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task ConcurrentResultCreationRetainsOneLogicalResultPerRun()
    {
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(
            PostgreSqlTestEnvironment.RequireConnectionString(),
            async database =>
            {
                await PostgreSqlMigrationTestDatabase.MigrateAsync(database, PreviousMigration);
                var seeded = await SeedRunningMaterializationAsync(database, "result-race");
                await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
                var completedAt = seeded.StartedAt.AddSeconds(1);

                async Task<bool> TryInsertAsync()
                {
                    try
                    {
                        await InsertResultAsync(database, Guid.NewGuid(), seeded, completedAt);
                        return true;
                    }
                    catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
                    {
                        return false;
                    }
                }

                var outcomes = await Task.WhenAll(TryInsertAsync(), TryInsertAsync());

                Assert.Single(outcomes, value => value);
                Assert.Single(outcomes, value => !value);
                Assert.Equal(1L, await PostgreSqlMigrationTestDatabase.ScalarAsync<long>(
                    database,
                    "SELECT COUNT(*) FROM task_execution_results WHERE \"TaskExecutionRunId\" = @runId;",
                    ("runId", seeded.RunId)));
            });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    public async Task RealRuntimeConsumesAuthorizedTextAndPersistsOneReloadableResult()
    {
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(
            PostgreSqlTestEnvironment.RequireConnectionString(),
            async database =>
            {
                await PostgreSqlMigrationTestDatabase.MigrateAsync(database);
                var graph = await TaskV1MigrationRawSqlSeed.CreateGraphAsync(database, "result-runtime");
                var currentTenant = new CurrentTenantService();
                currentTenant.SetTenant(graph.TenantId, "migration-tenant-result-runtime");
                var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(database).Options;
                await using var context = new AppDbContext(options, currentTenant);

                var bytes = "alpha beta\ngamma"u8.ToArray();
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
                var fileObject = new FileObject
                {
                    TenantId = graph.TenantId,
                    WorkspaceId = graph.WorkspaceId,
                    ProjectId = graph.ProjectId,
                    UploadedByUserId = graph.UserId,
                    OriginalFileName = "runtime-source.txt",
                    StorageKey = "runtime/result/source.txt",
                    ContentType = "text/plain",
                    SizeBytes = bytes.LongLength,
                    HashSha256 = hash,
                    Status = FileObjectStatus.Active
                };
                context.FileObjects.Add(fileObject);
                await context.SaveChangesAsync();

                var attachment = new Attachment
                {
                    TenantId = graph.TenantId,
                    FileObjectId = fileObject.Id,
                    WorkspaceId = graph.WorkspaceId,
                    OwnerType = AttachmentOwnerType.TaskItem,
                    OwnerId = graph.TaskId,
                    OwnerUserId = graph.UserId,
                    UploadedByUserId = graph.UserId,
                    FileName = "runtime-source.txt",
                    StoredFileName = "runtime-source.txt",
                    FilePath = "runtime/result/source.txt",
                    ContentType = "text/plain",
                    Extension = ".txt",
                    SizeBytes = bytes.LongLength,
                    StorageProvider = "LocalFileSystem",
                    StorageKey = fileObject.StorageKey,
                    ScanStatus = FileScanStatus.Clean
                };
                context.Attachments.Add(attachment);
                await context.SaveChangesAsync();

                var requestedAt = new DateTimeOffset(2026, 8, 30, 22, 30, 0, TimeSpan.Zero);
                var run = new TaskExecutionRun
                {
                    TenantId = graph.TenantId,
                    WorkspaceId = graph.WorkspaceId,
                    ProjectId = graph.ProjectId,
                    TaskItemId = graph.TaskId,
                    RequestedByUserId = graph.UserId,
                    RequestedAtUtc = requestedAt,
                    SnapshotScopeOrigin = TaskExecutionScopeOrigin.ProjectDefault,
                    SnapshotProjectScopeVersion = 1,
                    SnapshotWebEnabled = false,
                    SnapshotProjectFilesEnabled = true
                };
                context.TaskExecutionRuns.Add(run);
                await context.SaveChangesAsync();
                context.ChangeTracker.Clear();

                var runtime = new DurableTaskExecutionResultRuntime(
                    context,
                    currentTenant,
                    new AllowProjectAuthorization(),
                    new AllowFileAuthorization(),
                    new StaticFileStorage(fileObject.StorageKey, bytes),
                    new IncrementingClock(requestedAt.AddSeconds(1)),
                    new RecordingAuditLogger());
                var handle = new TaskExecutionRuntimeHandle(
                    run.Id,
                    graph.TenantId,
                    TaskExecutionRun.RuntimeContractVersion1);

                await runtime.ExecuteAsync(handle);
                await runtime.ExecuteAsync(handle);

                Assert.Equal("Succeeded", await PostgreSqlMigrationTestDatabase.ScalarAsync<string>(
                    database,
                    "SELECT \"Status\" FROM task_execution_runs WHERE \"Id\" = @runId;",
                    ("runId", run.Id)));
                Assert.Equal(1L, await PostgreSqlMigrationTestDatabase.ScalarAsync<long>(
                    database,
                    "SELECT COUNT(*) FROM task_execution_results WHERE \"TaskExecutionRunId\" = @runId;",
                    ("runId", run.Id)));
                Assert.Equal(1L, await PostgreSqlMigrationTestDatabase.ScalarAsync<long>(
                    database,
                    "SELECT COUNT(*) FROM task_execution_materialized_sources WHERE \"TaskExecutionRunId\" = @runId;",
                    ("runId", run.Id)));
                var body = await PostgreSqlMigrationTestDatabase.ScalarAsync<string>(
                    database,
                    "SELECT \"BodyMarkdown\" FROM task_execution_results WHERE \"TaskExecutionRunId\" = @runId;",
                    ("runId", run.Id));
                Assert.Contains("Lines: 2", body, StringComparison.Ordinal);
                Assert.Contains("Words: 3", body, StringComparison.Ordinal);
                Assert.DoesNotContain("alpha beta", body, StringComparison.Ordinal);
                Assert.DoesNotContain("runtime-source.txt", body, StringComparison.Ordinal);
            });
    }

    private static async Task<SeededMaterialization> SeedRunningMaterializationAsync(
        string database,
        string suffix)
    {
        var graph = await TaskV1MigrationRawSqlSeed.CreateGraphAsync(database, suffix);
        var currentTenant = new CurrentTenantService();
        currentTenant.SetTenant(graph.TenantId, $"migration-tenant-{suffix}");
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(database)
            .Options;
        await using var context = new AppDbContext(options, currentTenant);

        var fileHash = new string('a', 64);
        var fileObjectId = Guid.NewGuid();
        var storageKey = $"migration/{suffix}/source.txt";
        var createdAt = new DateTimeOffset(2026, 8, 30, 22, 0, 0, TimeSpan.Zero);

        // This helper intentionally seeds a schema pinned before the result
        // migration. Keep the file insert raw so later FileObject columns do
        // not make this historical migration test depend on the current EF
        // model (for example, SharingPolicy and SharingVersion).
        await PostgreSqlMigrationTestDatabase.ExecuteAsync(database, """
            INSERT INTO file_objects (
                "Id", "TenantId", "WorkspaceId", "ProjectId", "UploadedByUserId",
                "OriginalFileName", "StorageKey", "ContentType", "SizeBytes",
                "HashSha256", "Status", "CreatedAt")
            VALUES (
                @id, @tenantId, @workspaceId, @projectId, @userId,
                'migration-source.txt', @storageKey, 'text/plain', 17,
                @hash, 'Active', @createdAt);
            """,
            ("id", fileObjectId),
            ("tenantId", graph.TenantId),
            ("workspaceId", graph.WorkspaceId),
            ("projectId", graph.ProjectId),
            ("userId", graph.UserId),
            ("storageKey", storageKey),
            ("hash", fileHash),
            ("createdAt", createdAt));

        var attachment = new Attachment
        {
            TenantId = graph.TenantId,
            FileObjectId = fileObjectId,
            WorkspaceId = graph.WorkspaceId,
            OwnerType = AttachmentOwnerType.TaskItem,
            OwnerId = graph.TaskId,
            OwnerUserId = graph.UserId,
            UploadedByUserId = graph.UserId,
            FileName = "migration-source.txt",
            StoredFileName = "migration-source.txt",
            FilePath = $"migration/{suffix}/source.txt",
            ContentType = "text/plain",
            Extension = ".txt",
            SizeBytes = 17,
            StorageProvider = "LocalFileSystem",
            StorageKey = storageKey,
            ScanStatus = FileScanStatus.Clean
        };
        context.Attachments.Add(attachment);
        await context.SaveChangesAsync();

        var requestedAt = createdAt;
        var run = new TaskExecutionRun
        {
            TenantId = graph.TenantId,
            WorkspaceId = graph.WorkspaceId,
            ProjectId = graph.ProjectId,
            TaskItemId = graph.TaskId,
            RequestedByUserId = graph.UserId,
            RequestedAtUtc = requestedAt,
            SnapshotScopeOrigin = TaskExecutionScopeOrigin.ProjectDefault,
            SnapshotProjectScopeVersion = 1,
            SnapshotWebEnabled = false,
            SnapshotProjectFilesEnabled = true
        };
        context.TaskExecutionRuns.Add(run);
        await context.SaveChangesAsync();
        run.Status = TaskExecutionRunStatus.Queued;
        run.QueuedAtUtc = requestedAt.AddSeconds(1);
        run.VersionNo++;
        await context.SaveChangesAsync();
        run.Status = TaskExecutionRunStatus.Running;
        run.StartedAtUtc = requestedAt.AddSeconds(2);
        run.VersionNo++;
        await context.SaveChangesAsync();

        var sourceId = Guid.NewGuid();
        await PostgreSqlMigrationTestDatabase.ExecuteAsync(database, """
            INSERT INTO task_execution_materialized_sources (
                "Id", "TenantId", "WorkspaceId", "ProjectId", "TaskItemId",
                "TaskExecutionRunId", "FileObjectId", "AttachmentId", "SchemaVersion",
                "ContentSha256", "MediaType", "MaterializedByteCount", "MaterializedAtUtc")
            VALUES (
                @id, @tenantId, @workspaceId, @projectId, @taskId,
                @runId, @fileObjectId, @attachmentId, 1,
                @hash, 'text/plain', 17, @materializedAt);
            """,
            ("id", sourceId),
            ("tenantId", graph.TenantId),
            ("workspaceId", graph.WorkspaceId),
            ("projectId", graph.ProjectId),
            ("taskId", graph.TaskId),
            ("runId", run.Id),
            ("fileObjectId", fileObjectId),
            ("attachmentId", attachment.Id),
            ("hash", fileHash),
            ("materializedAt", run.StartedAtUtc!.Value.AddMilliseconds(1)));

        return new SeededMaterialization(
            graph.TenantId,
            graph.WorkspaceId,
            graph.ProjectId,
            graph.TaskId,
            run.Id,
            sourceId,
            run.StartedAtUtc.Value);
    }

    private static Task InsertResultAsync(
        string database,
        Guid resultId,
        SeededMaterialization seeded,
        DateTimeOffset completedAt) =>
        PostgreSqlMigrationTestDatabase.ExecuteAsync(database, """
            INSERT INTO task_execution_results (
                "Id", "TenantId", "WorkspaceId", "ProjectId", "TaskItemId",
                "TaskExecutionRunId", "SchemaVersion", "Status", "Title",
                "BodyMarkdown", "ContentSha256", "CompletedAtUtc", "CreatedAtUtc")
            VALUES (
                @id, @tenantId, @workspaceId, @projectId, @taskId,
                @runId, 1, 'Succeeded', 'Project Files Analysis Report',
                '# Project Files Analysis Report', @hash, @completedAt, @completedAt);
            """,
            ("id", resultId),
            ("tenantId", seeded.TenantId),
            ("workspaceId", seeded.WorkspaceId),
            ("projectId", seeded.ProjectId),
            ("taskId", seeded.TaskId),
            ("runId", seeded.RunId),
            ("hash", new string('b', 64)),
            ("completedAt", completedAt));

    private static Task InsertResultSourceAsync(
        string database,
        Guid linkId,
        Guid resultId,
        SeededMaterialization seeded,
        int ordinal) =>
        PostgreSqlMigrationTestDatabase.ExecuteAsync(database, """
            INSERT INTO task_execution_result_sources (
                "Id", "TenantId", "TaskExecutionResultId", "MaterializedSourceId", "Ordinal")
            VALUES (@id, @tenantId, @resultId, @sourceId, @ordinal);
            """,
            ("id", linkId),
            ("tenantId", seeded.TenantId),
            ("resultId", resultId),
            ("sourceId", seeded.MaterializedSourceId),
            ("ordinal", ordinal));

    private sealed class AllowProjectAuthorization : IProjectAuthorizationService
    {
        public Task<bool> CanViewProject(Guid userId, Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> CanManageProject(Guid userId, Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> CanCreateProject(Guid userId, Guid workspaceId, Guid groupId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class AllowFileAuthorization : IFileAuthorizationService
    {
        public Task<bool> CanUploadAttachment(Guid userId, AttachmentOwnerType ownerType, Guid ownerId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> CanViewWorkspaceFiles(Guid userId, Guid workspaceId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> CanViewAttachment(Guid userId, Attachment attachment, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> CanDownloadAttachment(Guid userId, Attachment attachment, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<IReadOnlySet<Guid>> GetDeletableWorkspaceAttachmentIdsAsync(Guid userId, Guid workspaceId, IReadOnlyCollection<Attachment> attachments, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
        public Task<bool> CanDeleteAttachment(Guid userId, Attachment attachment, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class StaticFileStorage(string storageKey, byte[] bytes) : IFileStorageService
    {
        public Task<Result> SaveAsync(string key, Stream stream, string contentType, CancellationToken cancellationToken = default) => Task.FromResult(Result.Failure("not supported"));
        public Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(key == storageKey ? new MemoryStream(bytes, writable: false) : throw new FileNotFoundException());
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(key == storageKey);
        public Task<string?> CreateSignedReadUrlAsync(string key, TimeSpan expiresIn, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private sealed class IncrementingClock(DateTimeOffset current) : IClock
    {
        private DateTimeOffset value = current;
        public DateTimeOffset UtcNow
        {
            get
            {
                var result = value;
                value = value.AddMilliseconds(1);
                return result;
            }
        }
    }

    private sealed class RecordingAuditLogger : IAuditLogger
    {
        public Task LogAsync(AuditLogEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed record SeededMaterialization(
        Guid TenantId,
        Guid WorkspaceId,
        Guid ProjectId,
        Guid TaskId,
        Guid RunId,
        Guid MaterializedSourceId,
        DateTimeOffset StartedAt);
}
