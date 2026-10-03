using System.Data.Common;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Coglatas.Infrastructure.Persistence;

/// <summary>Request-local, Test-only Npgsql observation. Never exports SQL, parameters, or Activity events.</summary>
public sealed class PerformanceDbCapture : DbCommandInterceptor, IDisposable
{
    private readonly AsyncLocal<Measurement?> _current = new();
    private readonly ActivityListener _listener;

    public PerformanceDbCapture()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Npgsql",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                _current.Value is null ? ActivitySamplingResult.None : ActivitySamplingResult.AllData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) =>
                _current.Value is null ? ActivitySamplingResult.None : ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                var sql = activity.GetTagItem("db.query.text") as string ?? activity.GetTagItem("db.statement") as string;
                if (sql is not null)
                {
                    _current.Value?.Record(sql, activity.Duration.TotalMilliseconds, activity.Status == ActivityStatusCode.Error);
                }
            }
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public Measurement Begin()
    {
        if (_current.Value is not null)
        {
            throw new InvalidOperationException("A PERF-05 capture is already active in this execution context.");
        }
        var measurement = new Measurement(() => _current.Value = null);
        _current.Value = measurement;
        return measurement;
    }

    public override InterceptionResult DataReaderDisposing(DbCommand command, DataReaderDisposingEventData eventData, InterceptionResult result)
    {
        // EF counts Read attempts, including the final false read. This is a row upper bound, not an exact row count.
        _current.Value?.RecordRows(command.CommandText, eventData.ReadCount);
        return result;
    }

    public void Dispose() => _listener.Dispose();

    public sealed class Measurement(Action close) : IDisposable
    {
        private readonly object _gate = new();
        private readonly List<CommandEvidence> _commands = [];
        private readonly Dictionary<string, Queue<int>> _rows = new(StringComparer.Ordinal);
        private bool _closed;

        public void Record(string sql, double durationMs, bool failed = false)
        {
            var shape = SqlShape.Inspect(sql);
            lock (_gate)
            {
                if (_closed) return;
                int? rows = _rows.TryGetValue(shape.Fingerprint, out var pending) && pending.Count > 0 ? pending.Dequeue() : null;
                _commands.Add(new CommandEvidence(shape.Fingerprint, durationMs, rows, failed, shape.RootTable, shape.Bounded, shape.Ordered));
            }
        }

        public void RecordRows(string sql, int rows)
        {
            var fingerprint = SqlShape.Inspect(sql).Fingerprint;
            lock (_gate)
            {
                if (_closed) return;
                var index = _commands.FindLastIndex(command => command.Fingerprint == fingerprint && command.ReadOperations is null);
                if (index >= 0)
                {
                    _commands[index] = _commands[index] with { ReadOperations = rows };
                }
                else
                {
                    if (!_rows.TryGetValue(fingerprint, out var pending)) _rows[fingerprint] = pending = new Queue<int>();
                    pending.Enqueue(rows);
                }
            }
        }

        public IReadOnlyList<CommandEvidence> Snapshot()
        {
            lock (_gate) return _commands.ToArray();
        }

        public void Dispose()
        {
            lock (_gate) _closed = true;
            close();
        }
    }

    public sealed record CommandEvidence(string Fingerprint, double DurationMs, int? ReadOperations, bool Failed, string? RootTable, bool Bounded, bool Ordered);

    public sealed record SqlShape(string Fingerprint, string? RootTable, bool Bounded, bool Ordered)
    {
        // Only known schema names can leave the process. Unknown SQL is still counted.
        private static readonly HashSet<string> Tables = new(StringComparer.Ordinal)
        {
            "task_items", "tasks", "projects", "workspaces", "attachments", "file_objects",
            "conversations", "messages", "notifications", "announcements"
        };
        private static readonly Regex Tokens = new(
            "--[^\\r\\n]*|/\\*[\\s\\S]*?\\*/|\\$(?<tag>[A-Za-z_][A-Za-z_0-9]*|)\\$[\\s\\S]*?\\$\\k<tag>\\$|(?:[eE])?'(?:''|\\\\.|[^'])*'|\"(?:\"\"|[^\"])*\"|@[A-Za-z_][A-Za-z_0-9]*|\\$[0-9]+|\\b[0-9]+(?:\\.[0-9]+)?\\b|[A-Za-z_][A-Za-z_0-9]*|[^\\s]",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        public static SqlShape Inspect(string sql)
        {
            var canonical = new StringBuilder();
            var outer = new List<string>();
            var depth = 0;
            foreach (Match match in Tokens.Matches(sql))
            {
                var token = match.Value;
                if (token.StartsWith("--", StringComparison.Ordinal) || token.StartsWith("/*", StringComparison.Ordinal)) continue;
                if (token.StartsWith('\'') || token.StartsWith("E'", StringComparison.OrdinalIgnoreCase) || token.StartsWith('$') || token.StartsWith('@') || char.IsDigit(token[0])) token = "?";
                canonical.Append(token.ToLowerInvariant()).Append(' ');
                if (token == ")") depth--;
                if (depth == 0) outer.Add(token);
                if (token == "(") depth++;
            }
            var from = outer.FindIndex(token => token.Equals("FROM", StringComparison.OrdinalIgnoreCase));
            var table = from >= 0 && from + 1 < outer.Count ? outer[from + 1].Trim('"') : null;
            var rootTable = table is not null && Tables.Contains(table) ? table : null;
            var bounded = outer.Any(token => token.Equals("LIMIT", StringComparison.OrdinalIgnoreCase));
            var ordered = outer.Zip(outer.Skip(1)).Any(pair => pair.First.Equals("ORDER", StringComparison.OrdinalIgnoreCase) && pair.Second.Equals("BY", StringComparison.OrdinalIgnoreCase));
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
            return new SqlShape(fingerprint, rootTable, bounded, ordered);
        }
    }
}
