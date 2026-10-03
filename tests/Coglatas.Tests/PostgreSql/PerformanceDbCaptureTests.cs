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
    public void DerivedRootPageIsRecognizedWithoutAcceptingPagedJoins()
    {
        var paged = PerformanceDbCapture.SqlShape.Inspect("SELECT t.* FROM (SELECT * FROM task_items ORDER BY \"Id\" LIMIT @page) AS t LEFT JOIN projects p ON p.\"Id\" = t.\"ProjectId\" ORDER BY t.\"Id\"");
        Assert.Equal("task_items", paged.RootTable);
        Assert.True(paged.Bounded);
        Assert.True(paged.Ordered);
        var joined = PerformanceDbCapture.SqlShape.Inspect("SELECT t.* FROM task_items t LEFT JOIN (SELECT * FROM projects ORDER BY \"Id\" LIMIT 5) p ON p.\"Id\" = t.\"ProjectId\"");
        Assert.Equal("task_items", joined.RootTable);
        Assert.False(joined.Bounded);
        Assert.False(joined.Ordered);
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
        static async Task<int> MeasureAsync(PerformanceDbCapture observer)
        {
            using var measurement = observer.Begin();
            await Task.Yield();
            using var source = new ActivitySource("Npgsql");
            using (var activity = source.StartActivity())
            {
                Assert.NotNull(activity);
                activity.SetTag("db.query.text", "SELECT 1");
            }
            return measurement.Snapshot().Count;
        }
        var tasks = new List<Task<int>>();
        for (var index = 0; index < 5; index++) tasks.Add(MeasureAsync(capture));
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
        static async Task<int> CountAsync(PerformanceDbCapture observer, NpgsqlConnection dbConnection, int cardinality, bool batched)
        {
            using var measurement = observer.Begin();
            if (batched)
            {
                await using var command = new NpgsqlCommand("SELECT id FROM perf05_rows WHERE id <= @count ORDER BY id", dbConnection);
                command.Parameters.AddWithValue("count", cardinality);
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) { }
            }
            else
            {
                for (var id = 1; id <= cardinality; id++)
                {
                    await using var command = new NpgsqlCommand("SELECT id FROM perf05_rows WHERE id = @id", dbConnection);
                    command.Parameters.AddWithValue("id", id);
                    await command.ExecuteScalarAsync();
                }
            }
            return measurement.Snapshot().Count;
        }
        Assert.Equal(5, await CountAsync(capture, connection, 5, batched: false));
        Assert.Equal(20, await CountAsync(capture, connection, 20, batched: false));
        Assert.Equal(1, await CountAsync(capture, connection, 5, batched: true));
        Assert.Equal(1, await CountAsync(capture, connection, 20, batched: true));
    }
}
