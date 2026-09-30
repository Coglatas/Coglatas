using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Coglatas.Tests.PostgreSql;

/// <summary>Fresh-database migration contract checks. Upgrade scenarios use raw SQL only and live beside this suite.</summary>
[Collection("PostgreSqlTaskV1")]
[Trait("Scope", "TaskV1Prompt2C")]
public sealed class TaskV1MigrationPostgreSqlTests
{
    private const string Pr03CBaseMigration = "20260722230000_MigrateLegacyTaskComments";
    private const string BeforeTenantTableRepairMigration = "20260726150000_EnforceManualWatchOptOutExclusivity";
    private const string TenantTableRepairMigration = "20260728010000_CreateMissingTenantSettingsTable";

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR03C")]
    public async Task CleanDatabaseMigratesToLatestWithTaskV1DatabaseContracts()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await WithTemporaryDatabaseAsync(connectionString, async testConnectionString =>
        {
            await using var context = PostgreSqlMigrationTestDatabase.CreatePlatformContext(testConnectionString);
            await context.GetService<IMigrator>().MigrateAsync();

            Assert.Empty(await context.Database.GetPendingMigrationsAsync());
            Assert.False(context.Database.HasPendingModelChanges());
            Assert.True(await ScalarAsync<bool>(testConnectionString, "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'tenant_settings');"));
            Assert.True(await ScalarAsync<bool>(testConnectionString, "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'api_tokens');"));
            Assert.True(await ScalarAsync<bool>(testConnectionString, "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'export_jobs');"));
            Assert.True(await ScalarAsync<bool>(testConnectionString, "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'integration_accounts');"));
            Assert.True(await ScalarAsync<bool>(testConnectionString, "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'subscriptions');"));
            Assert.True(await ScalarAsync<bool>(testConnectionString, "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'usage_records');"));
            Assert.True(await ScalarAsync<bool>(testConnectionString, "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'webhook_endpoints');"));
            Assert.True(await ScalarAsync<bool>(testConnectionString, "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'workspaces' AND column_name = 'TimeZone');"));
            Assert.True(await ScalarAsync<bool>(testConnectionString, "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'work_item_watch_states' AND column_name = 'IsManualWatch');"));
            Assert.True(await ScalarAsync<bool>(testConnectionString, "SELECT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'CK_work_item_watch_states_manual_opt_out_exclusive');"));
            Assert.True(await ScalarAsync<bool>(testConnectionString, "SELECT EXISTS (SELECT 1 FROM pg_attrdef d JOIN pg_class c ON c.oid = d.adrelid JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = d.adnum WHERE c.relname = 'work_item_watch_states' AND a.attname = 'VersionNo' AND pg_get_expr(d.adbin, d.adrelid) = '1');"));
            Assert.True(await ScalarAsync<bool>(testConnectionString, "SELECT EXISTS (SELECT 1 FROM pg_attrdef d JOIN pg_class c ON c.oid = d.adrelid JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = d.adnum WHERE c.relname = 'project_task_labels' AND a.attname = 'VersionNo' AND pg_get_expr(d.adbin, d.adrelid) = '1');"));
            Assert.True(await ScalarAsync<bool>(testConnectionString, "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE tablename = 'attachments' AND indexname = 'IX_attachments_OwnerType_OwnerId_FileObjectId_active_task');"));
            Assert.True(await ScalarAsync<bool>(testConnectionString, "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE tablename = 'project_task_labels' AND indexname = 'IX_project_task_labels_TenantId_ProjectId_NormalizedName');"));
            Assert.True(await ScalarAsync<bool>(testConnectionString, "SELECT EXISTS (SELECT 1 FROM pg_attribute attribute JOIN pg_class table_class ON table_class.oid = attribute.attrelid WHERE table_class.relname = 'project_task_labels' AND attribute.attname = 'NormalizedName' AND attribute.attgenerated = 's');"));
            Assert.True(context.Model.FindEntityType(typeof(Domain.Entities.WorkItemWatchState))!.FindProperty(nameof(Domain.Entities.WorkItemWatchState.VersionNo))!.IsConcurrencyToken);
            Assert.NotNull(context.Model.FindEntityType(typeof(Domain.Entities.WorkItemWatchState))!.FindProperty(nameof(Domain.Entities.WorkItemWatchState.IsManualWatch)));
            Assert.True(context.Model.FindEntityType(typeof(Domain.Entities.ProjectTaskLabel))!.FindProperty(nameof(Domain.Entities.ProjectTaskLabel.VersionNo))!.IsConcurrencyToken);
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR03C")]
    public async Task Pr03cBaseSchemaUpgradesToLatestWithAllHistoricallyMissingTables()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await WithTemporaryDatabaseAsync(connectionString, async testConnectionString =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(testConnectionString, Pr03CBaseMigration);
            await AssertHistoricalTablesAsync(testConnectionString, expected: false);

            await PostgreSqlMigrationTestDatabase.MigrateAsync(testConnectionString);

            await AssertHistoricalTablesAsync(testConnectionString, expected: true);
            await AssertLatestMigrationStateAsync(testConnectionString);
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR03C")]
    public async Task TenantTableRepairSchemaUpgradesThroughTheFollowingPlatformTableRepair()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await WithTemporaryDatabaseAsync(connectionString, async testConnectionString =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(testConnectionString, TenantTableRepairMigration);
            Assert.True(await TableExistsAsync(testConnectionString, "tenant_settings"));
            Assert.False(await TableExistsAsync(testConnectionString, "api_tokens"));

            await PostgreSqlMigrationTestDatabase.MigrateAsync(testConnectionString);

            await AssertHistoricalTablesAsync(testConnectionString, expected: true);
            await AssertLatestMigrationStateAsync(testConnectionString);
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSQLIntegration")]
    [Trait("Scope", "TaskV1PR03C")]
    public async Task RepairMigrationsPreservePreExistingTablesAndDataAcrossRollbackAndReapply()
    {
        var connectionString = PostgreSqlTestEnvironment.RequireConnectionString();
        await WithTemporaryDatabaseAsync(connectionString, async testConnectionString =>
        {
            await PostgreSqlMigrationTestDatabase.MigrateAsync(testConnectionString);
            var graph = await TaskV1MigrationRawSqlSeed.CreateGraphAsync(testConnectionString, "repair-reapply");
            var usageRecordId = Guid.NewGuid();
            await PostgreSqlMigrationTestDatabase.ExecuteAsync(
                testConnectionString,
                """
                INSERT INTO usage_records (
                    "Id", "TenantId", "Date", "ActiveUserCount", "TotalUserCount",
                    "ProjectCount", "TaskCount", "FileCount", "StorageUsedBytes",
                    "ApiRequestCount", "CreatedAt")
                VALUES (
                    @id, @tenantId, DATE '2026-07-28', 1, 1, 1, 1, 0, 0, 1,
                    TIMESTAMPTZ '2026-07-28T00:00:00Z');
                """,
                ("id", usageRecordId),
                ("tenantId", graph.TenantId));

            await PostgreSqlMigrationTestDatabase.MigrateAsync(
                testConnectionString,
                BeforeTenantTableRepairMigration);
            await AssertHistoricalTablesAsync(testConnectionString, expected: true);
            Assert.Equal(
                1,
                await PostgreSqlMigrationTestDatabase.ScalarAsync<long>(
                    testConnectionString,
                    "SELECT COUNT(*) FROM usage_records WHERE \"Id\" = @id;",
                    ("id", usageRecordId)));

            await PostgreSqlMigrationTestDatabase.MigrateAsync(testConnectionString);

            await AssertHistoricalTablesAsync(testConnectionString, expected: true);
            Assert.Equal(
                1,
                await PostgreSqlMigrationTestDatabase.ScalarAsync<long>(
                    testConnectionString,
                    "SELECT COUNT(*) FROM usage_records WHERE \"Id\" = @id;",
                    ("id", usageRecordId)));
            await AssertLatestMigrationStateAsync(testConnectionString);
        });
    }

    private static async Task AssertLatestMigrationStateAsync(string connectionString)
    {
        await using var context = PostgreSqlMigrationTestDatabase.CreatePlatformContext(connectionString);
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.False(context.Database.HasPendingModelChanges());
    }

    private static async Task AssertHistoricalTablesAsync(string connectionString, bool expected)
    {
        foreach (var tableName in new[]
                 {
                     "tenant_settings",
                     "api_tokens",
                     "export_jobs",
                     "integration_accounts",
                     "subscriptions",
                     "usage_records",
                     "webhook_endpoints"
                 })
        {
            Assert.Equal(expected, await TableExistsAsync(connectionString, tableName));
        }
    }

    private static Task<bool> TableExistsAsync(string connectionString, string tableName) =>
        PostgreSqlMigrationTestDatabase.ScalarAsync<bool>(
            connectionString,
            "SELECT to_regclass(current_schema() || '.' || @tableName) IS NOT NULL;",
            ("tableName", tableName));

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static Task WithTemporaryDatabaseAsync(string connectionString, Func<string, Task> action)
        => PostgreSqlMigrationTestDatabase.WithTemporaryDatabaseAsync(connectionString, action);
}
