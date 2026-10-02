using Coglatas.Application.Common.Tenancy;
using Coglatas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Coglatas.Tests.PostgreSql;

/// <summary>Creates isolated PostgreSQL databases for provider and migration scenarios.</summary>
internal static class PostgreSqlMigrationTestDatabase
{
    private const string UseMigratedTemplateEnvironmentVariable = "COGLATAS_TEST_USE_MIGRATED_TEMPLATE";
    private static readonly SemaphoreSlim MigratedTemplateGate = new(1, 1);
    private static string? migratedTemplateDatabaseName;
    private static string? migratedTemplateServerIdentity;

    public static Task WithTemporaryDatabaseAsync(string connectionString, Func<string, Task> scenario) =>
        WithTemporaryDatabaseCoreAsync(connectionString, templateDatabaseName: null, scenario);

    /// <summary>
    /// Creates an isolated database that is already at the latest migration when the CI template
    /// optimization is enabled. Local/default execution preserves the original empty-db + migrate
    /// behavior so migration application remains observable outside the optimized CI path.
    /// </summary>
    public static async Task WithMigratedTemporaryDatabaseAsync(
        string connectionString,
        Func<string, Task> scenario)
    {
        if (!UseMigratedTemplate())
        {
            await WithTemporaryDatabaseAsync(connectionString, async database =>
            {
                await MigrateAsync(database);
                await scenario(database);
            });
            return;
        }

        var templateDatabaseName = await EnsureMigratedTemplateDatabaseAsync(connectionString);
        await WithTemporaryDatabaseCoreAsync(connectionString, templateDatabaseName, scenario);
    }

    private static async Task WithTemporaryDatabaseCoreAsync(
        string connectionString,
        string? templateDatabaseName,
        Func<string, Task> scenario)
    {
        var databaseName = $"coglatas_taskv1_migration_{Guid.NewGuid():N}";
        var source = new NpgsqlConnectionStringBuilder(connectionString);
        var temporary = new NpgsqlConnectionStringBuilder(connectionString) { Database = databaseName }.ConnectionString;

        await CreateDatabaseAsync(source.ConnectionString, databaseName, templateDatabaseName);

        try
        {
            await scenario(temporary);
        }
        finally
        {
            ClearPool(temporary);
            await DropDatabaseAsync(source.ConnectionString, databaseName);
        }
    }

    private static async Task<string> EnsureMigratedTemplateDatabaseAsync(string connectionString)
    {
        var source = new NpgsqlConnectionStringBuilder(connectionString);
        var serverIdentity = $"{source.Host}:{source.Port}/{source.Username}";

        await MigratedTemplateGate.WaitAsync();
        try
        {
            if (migratedTemplateDatabaseName is not null)
            {
                if (!string.Equals(migratedTemplateServerIdentity, serverIdentity, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "The migrated PostgreSQL test template cannot be reused across different server identities.");

                return migratedTemplateDatabaseName;
            }

            var templateDatabaseName = $"coglatas_test_template_{Environment.ProcessId}_{Guid.NewGuid():N}";
            var templateConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
            {
                Database = templateDatabaseName
            }.ConnectionString;

            await CreateDatabaseAsync(source.ConnectionString, templateDatabaseName, templateDatabaseName: null);
            try
            {
                await MigrateAsync(templateConnectionString);
                // EF/Npgsql returns the migration connection to its pool when the context is disposed.
                // A template database must have no active pooled connections before CREATE DATABASE ... TEMPLATE.
                ClearPool(templateConnectionString);
            }
            catch
            {
                ClearPool(templateConnectionString);
                await DropDatabaseAsync(source.ConnectionString, templateDatabaseName);
                throw;
            }

            migratedTemplateDatabaseName = templateDatabaseName;
            migratedTemplateServerIdentity = serverIdentity;
            return templateDatabaseName;
        }
        finally
        {
            MigratedTemplateGate.Release();
        }
    }

    private static async Task CreateDatabaseAsync(
        string adminConnectionString,
        string databaseName,
        string? templateDatabaseName)
    {
        await using var admin = new NpgsqlConnection(adminConnectionString);
        await admin.OpenAsync();

        var sql = templateDatabaseName is null
            ? $"CREATE DATABASE \"{databaseName}\""
            : $"CREATE DATABASE \"{databaseName}\" WITH TEMPLATE \"{templateDatabaseName}\"";

        await using var create = new NpgsqlCommand(sql, admin);
        await create.ExecuteNonQueryAsync();
    }

    private static async Task DropDatabaseAsync(string adminConnectionString, string databaseName)
    {
        await using var admin = new NpgsqlConnection(adminConnectionString);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
    }

    private static void ClearPool(string connectionString)
    {
        using var connection = new NpgsqlConnection(connectionString);
        NpgsqlConnection.ClearPool(connection);
    }

    private static bool UseMigratedTemplate() =>
        string.Equals(
            Environment.GetEnvironmentVariable(UseMigratedTemplateEnvironmentVariable),
            "true",
            StringComparison.OrdinalIgnoreCase);

    public static async Task MigrateAsync(string connectionString, string? targetMigration = null)
    {
        await using var context = CreatePlatformContext(connectionString);
        await context.GetService<IMigrator>().MigrateAsync(targetMigration);
    }

    public static AppDbContext CreatePlatformContext(string connectionString)
    {
        var tenant = new CurrentTenantService();
        tenant.SetPlatformScope();
        return new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options, tenant);
    }

    public static async Task ExecuteAsync(string connectionString, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<T> ScalarAsync<T>(string connectionString, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    public static async Task<List<T>> QueryAsync<T>(string connectionString, string sql, Func<NpgsqlDataReader, T> map, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<T>();
        while (await reader.ReadAsync()) rows.Add(map(reader));
        return rows;
    }
}
