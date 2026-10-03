using System.Text.Json;
using Coglatas.Infrastructure.Persistence;

namespace Coglatas.Web.Testing;

/// <summary>Collects only explicit synthetic benchmark requests in the PERF-02 Test boundary.</summary>
internal sealed class PerformanceDbStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, pipeline) =>
        {
            // No product response/header/body is exported. The opaque capture ID is locally generated.
            var rawId = context.Request.Headers["X-Performance-Capture"].ToString();
            if (!Guid.TryParseExact(rawId, "N", out var captureId))
            {
                await pipeline(context);
                return;
            }
            var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
            var directory = configuration["COGLATAS_PERFORMANCE_DB_EVIDENCE_PATH"]
                ?? throw new InvalidOperationException("PERF-05 requires a local evidence directory.");
            var capture = context.RequestServices.GetRequiredService<PerformanceDbCapture>();
            using var measurement = capture.Begin();
            await pipeline(context);
            var commands = measurement.Snapshot();
            var evidence = new
            {
                schemaVersion = 1,
                status = context.Response.StatusCode,
                commandCount = commands.Count,
                totalDurationMs = commands.Sum(command => command.DurationMs),
                commands,
                slowestCommands = commands.OrderByDescending(command => command.DurationMs).Take(5).ToArray()
            };
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{captureId:N}.json");
            var temporary = path + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(evidence, new JsonSerializerOptions(JsonSerializerDefaults.Web)), context.RequestAborted);
            File.Move(temporary, path, overwrite: true);
        });
        next(app);
    };
}
