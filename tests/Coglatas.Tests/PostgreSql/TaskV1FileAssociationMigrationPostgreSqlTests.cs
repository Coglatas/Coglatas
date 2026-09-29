namespace Coglatas.Tests.PostgreSql;

[Collection("PostgreSqlTaskV1")]
[Trait("Scope", "TaskV1Prompt2C")]
public sealed class TaskV1FileAssociationMigrationPostgreSqlTests
{
    private const string PreviousMigration = "20260725060000_AddWorkspaceTimeZone";

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR03C")]
    public async Task TaskFileMigrationTombstonesOnlyLaterActiveDuplicatesAndLeavesPartialIndexReusable()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, async database =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(database, PreviousMigration);
            var graph = await TaskV1MigrationRawSqlSeed.CreateGraphAsync(database, "files-main");
            var taskB = Guid.NewGuid();
            var fileId = Guid.NewGuid();
            var first = Guid.Parse("00000000-0000-0000-0000-000000000010");
            var later = Guid.Parse("00000000-0000-0000-0000-000000000020");
            var latest = Guid.Parse("00000000-0000-0000-0000-000000000030");
            var deleted = Guid.NewGuid();
            var tieWinner = Guid.Parse("00000000-0000-0000-0000-000000000040");
            var tieLoser = Guid.Parse("00000000-0000-0000-0000-000000000050");
            var workspaceAttachment = Guid.NewGuid();
            var firstCreated = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
            var tiedCreated = firstCreated.AddHours(1);

            await PostgreSqlMigrationTestDatabase.ExecuteAsync(database, """
INSERT INTO task_items ("Id", "TenantId", "WorkspaceId", "ProjectId", "Title", "Status", "Priority", "ProgressPercent", "SortOrder", "CreatedByUserId", "CreatedAt", "Kind", "IsBlocked", "SortKey", "VersionNo")
VALUES (@taskB, @tenantId, @workspaceId, @projectId, 'Task B', 'Todo', 'Medium', 0, 1, @userId, @firstCreated, 'Task', false, 2048, 1);
INSERT INTO file_objects ("Id", "TenantId", "WorkspaceId", "ProjectId", "UploadedByUserId", "OriginalFileName", "StorageKey", "ContentType", "SizeBytes", "Status", "CreatedAt")
VALUES (@fileId, @tenantId, @workspaceId, @projectId, @userId, 'proof.txt', @storageKey, 'text/plain', 42, 'Active', @firstCreated);
""", ("taskB", taskB), ("fileId", fileId), ("tenantId", graph.TenantId), ("workspaceId", graph.WorkspaceId), ("projectId", graph.ProjectId), ("userId", graph.UserId), ("storageKey", $"migration/files/{fileId:N}"), ("firstCreated", firstCreated));

            foreach (var (id, _, ownerType, ownerId, createdAt, deletedAt) in new[]
            {
                (first, graph.TaskId, "TaskItem", (Guid?)graph.TaskId, firstCreated, (DateTimeOffset?)null),
                (later, graph.TaskId, "TaskItem", (Guid?)graph.TaskId, tiedCreated, (DateTimeOffset?)null),
                (latest, graph.TaskId, "TaskItem", (Guid?)graph.TaskId, tiedCreated.AddMinutes(1), (DateTimeOffset?)null),
                (deleted, graph.TaskId, "TaskItem", (Guid?)graph.TaskId, firstCreated, (DateTimeOffset?)firstCreated.AddDays(1)),
                (tieWinner, taskB, "TaskItem", (Guid?)taskB, tiedCreated, (DateTimeOffset?)null),
                (tieLoser, taskB, "TaskItem", (Guid?)taskB, tiedCreated, (DateTimeOffset?)null),
                (workspaceAttachment, graph.TaskId, "Workspace", (Guid?)graph.WorkspaceId, tiedCreated, (DateTimeOffset?)null)
            })
            {
                await PostgreSqlMigrationTestDatabase.ExecuteAsync(database, """
INSERT INTO attachments ("Id", "TenantId", "FileObjectId", "WorkspaceId", "OwnerType", "OwnerId", "OwnerUserId", "UploadedByUserId", "FileName", "StoredFileName", "FilePath", "ContentType", "Extension", "SizeBytes", "StorageProvider", "StorageKey", "ScanStatus", "CreatedAt", "DeletedAt")
VALUES (@id, @tenantId, @fileId, @workspaceId, @ownerType, @ownerId, @userId, @userId, 'proof.txt', 'proof.txt', 'proof.txt', 'text/plain', '.txt', 42, 'test', @attachmentKey, 'Clean', @createdAt, @deletedAt);
""", ("id", id), ("tenantId", graph.TenantId), ("fileId", fileId), ("workspaceId", graph.WorkspaceId), ("ownerType", ownerType), ("ownerId", ownerId), ("userId", graph.UserId), ("attachmentKey", $"migration/attachment/{id:N}"), ("createdAt", createdAt), ("deletedAt", deletedAt));
            }

            await PostgreSqlMigrationTestDatabase.MigrateAsync(database);

            Assert.Equal(1, await PostgreSqlMigrationTestDatabase.ScalarAsync<long>(database, "SELECT COUNT(*) FROM attachments WHERE \"OwnerType\" = 'TaskItem' AND \"OwnerId\" = @taskId AND \"FileObjectId\" = @fileId AND \"DeletedAt\" IS NULL;", ("taskId", graph.TaskId), ("fileId", fileId)));
            Assert.Equal(first, await PostgreSqlMigrationTestDatabase.ScalarAsync<Guid>(database, "SELECT \"Id\" FROM attachments WHERE \"OwnerType\" = 'TaskItem' AND \"OwnerId\" = @taskId AND \"FileObjectId\" = @fileId AND \"DeletedAt\" IS NULL;", ("taskId", graph.TaskId), ("fileId", fileId)));
            Assert.Equal(tieWinner, await PostgreSqlMigrationTestDatabase.ScalarAsync<Guid>(database, "SELECT \"Id\" FROM attachments WHERE \"OwnerType\" = 'TaskItem' AND \"OwnerId\" = @taskId AND \"FileObjectId\" = @fileId AND \"DeletedAt\" IS NULL;", ("taskId", taskB), ("fileId", fileId)));
            Assert.Equal(3, await PostgreSqlMigrationTestDatabase.ScalarAsync<long>(database, "SELECT COUNT(*) FROM attachments WHERE \"Id\" IN (@later, @latest, @tieLoser) AND \"DeletedAt\" IS NOT NULL AND \"DeletedByUserId\" = @userId AND \"DeleteReason\" = 'Duplicate task file association consolidated by migration';", ("later", later), ("latest", latest), ("tieLoser", tieLoser), ("userId", graph.UserId)));
            Assert.Equal(1, await PostgreSqlMigrationTestDatabase.ScalarAsync<long>(database, "SELECT COUNT(*) FROM attachments WHERE \"Id\" = @deleted AND \"DeletedAt\" = @deletedAt;", ("deleted", deleted), ("deletedAt", firstCreated.AddDays(1))));
            Assert.Equal(1, await PostgreSqlMigrationTestDatabase.ScalarAsync<long>(database, "SELECT COUNT(*) FROM attachments WHERE \"Id\" = @workspaceAttachment AND \"DeletedAt\" IS NULL;", ("workspaceAttachment", workspaceAttachment)));
            Assert.Equal(42L, await PostgreSqlMigrationTestDatabase.ScalarAsync<long>(database, "SELECT \"SizeBytes\" FROM file_objects WHERE \"Id\" = @fileId;", ("fileId", fileId)));

            await PostgreSqlMigrationTestDatabase.ExecuteAsync(database, "UPDATE attachments SET \"DeletedAt\" = @now WHERE \"Id\" = @id;", ("id", first), ("now", firstCreated.AddDays(2)));
            await PostgreSqlMigrationTestDatabase.ExecuteAsync(database, """
INSERT INTO attachments ("Id", "TenantId", "FileObjectId", "WorkspaceId", "OwnerType", "OwnerId", "OwnerUserId", "UploadedByUserId", "FileName", "StoredFileName", "FilePath", "ContentType", "Extension", "SizeBytes", "StorageProvider", "StorageKey", "ScanStatus", "CreatedAt")
VALUES (@id, @tenantId, @fileId, @workspaceId, 'TaskItem', @taskId, @userId, @userId, 'proof.txt', 'proof.txt', 'proof.txt', 'text/plain', '.txt', 42, 'test', @storageKey, 'Clean', @now);
""", ("id", Guid.NewGuid()), ("tenantId", graph.TenantId), ("fileId", fileId), ("workspaceId", graph.WorkspaceId), ("taskId", graph.TaskId), ("userId", graph.UserId), ("storageKey", $"migration/attachment/relinked-{Guid.NewGuid():N}"), ("now", firstCreated.AddDays(2)));
        });
    }
}
