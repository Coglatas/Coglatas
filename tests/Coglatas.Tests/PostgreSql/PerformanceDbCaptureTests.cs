using System.Diagnostics;
using System.Text.Json;
using Coglatas.Infrastructure.Persistence;
using Npgsql;

namespace Coglatas.Tests.PostgreSql;

public sealed class PerformanceDbCaptureTests
{
    [Theory]
    [InlineData("SELECT * FROM task_items WHERE \"Title\" = 'secret-one' AND \"Id\" = @p0 LIMIT 5", "SELECT * FROM task_items WHERE \"Title\" = 'secret-two' AND \"Id\" = @p99 LIMIT 10")]
    [InlineData("SELECT $$protected-one$$, 42 -- password\nFROM task_items", "SELECT $tag$protected-two$tag$, 23 /* token */ FROM task_items")]
    public void FingerprintsNormalizeLiteralsParametersAndComments(string first, string second)
    {
        Assert.Equal(PerformanceDbCapture.SqlShape.Inspect(first).Fingerprint, PerformanceDbCapture.SqlShape.Inspect(second).Fingerprint);
    }

    [Fact]
    public void NestedLimitAndOrderCannotPretendToBeAnOuterPage()
    {
        var nested = PerformanceDbCapture.SqlShape.Inspect("SELECT * FROM task_items WHERE EXISTS (SELECT 1 FROM projects ORDER BY \"Id\" LIMIT 1)");
        Assert.False(nested.Bounded);
        Assert.False(nested.Ordered);
        var paged = PerformanceDbCapture.SqlShape.Inspect("SELECT * FROM task_items ORDER BY \"Id\" LIMIT @page");
        Assert.True(paged.Bounded);
        Assert.True(paged.Ordered);
    }

    [Fact]
    public void EvidenceCannotSerializeSensitiveSqlParametersOrErrors()
    {
        using var capture = new PerformanceDbCapture();
        using var measurement = capture.Begin();
        measurement.Record("SELECT 'protected-body' FROM task_items WHERE \"Id\" = @secret LIMIT 5", 2, failed: true);
        measurement.RecordRows("SELECT 'protected-body' FROM task_items WHERE \"Id\" = @secret LIMIT 5", 6);
        var evidence = JsonSerializer.Serialize(measurement.Snapshot());
        Assert.DoesNotContain("protected-body", evidence);
        Assert.DoesNotContain("@secret", evidence);
        Assert.DoesNotContain("SELECT", evidence);
        Assert.Equal(6, Assert.Single(measurement.Snapshot()).ReadOperations);
    }

    [Fact]
    public async Task ConcurrentExecutionContextsHaveIndependentMeasurements()
    {
        using var capture = new PerformanceDbCapture();
        var tasks = Enumerable.Range(1, 5).Select(index => Task.Run(async () =>
        {
            using var measurement = capture.Begin();
            await Task.Yield();
            using var source = new ActivitySource("Npgsql");
            using (var activity = source.StartActivity("query"))
            {
                Assert.NotNull(activity);
                activity.SetTag("db.query.text", "SELECT 1");
            }
            return measurement.Snapshot().Count;
        }));
        Assert.All(await Task.WhenAll(tasks), count => Assert.Equal(1, count));
    }

    [PostgreSqlFact]
    public async Task RealNpgsqlCountsExposeControlledNPlusOneButNotBatchedQueries()
    {
        using var capture = new PerformanceDbCapture();
        await using var connection = new NpgsqlConnection(PostgreSqlTestEnvironment.RequireConnectionString());
        await connection.OpenAsync();
        await using (var setup = new NpgsqlCommand("CREATE TEMP TABLE perf05_rows (id integer PRIMARY KEY); INSERT INTO perf05_rows SELECT generate_series(1, 20);", connection))
        {
            await setup.ExecuteNonQueryAsync();
        }
        async Task<int> CountAsync(int cardinality, bool batched)
        {
            using var measurement = capture.Begin();
            if (batched)
            {
                await using var command = new NpgsqlCommand("SELECT id FROM perf05_rows WHERE id <= @count ORDER BY id", connection);
                command.Parameters.AddWithValue("count", cardinality);
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) { }
            }
            else
            {
                for (var id = 1; id <= cardinality; id++)
                {
                    await using var command = new NpgsqlCommand("SELECT id FROM perf05_rows WHERE id = @id", connection);
                    command.Parameters.AddWithValue("id", id);
                    await command.ExecuteScalarAsync();
                }
            }
            return measurement.Snapshot().Count;
        }
        Assert.Equal(5, await CountAsync(5, batched: false));
        Assert.Equal(20, await CountAsync(20, batched: false));
        Assert.Equal(1, await CountAsync(5, batched: true));
        Assert.Equal(1, await CountAsync(20, batched: true));
    }
}
