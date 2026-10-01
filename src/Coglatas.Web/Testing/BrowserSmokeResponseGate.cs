using System.Collections.Concurrent;
using Coglatas.Application.Common.Interfaces;

namespace Coglatas.Web.Testing;

public static class BrowserSmokeTestBoundary
{
    public static bool IsEnabled(string environmentName, bool requested) =>
        requested &&
        string.Equals(environmentName, "Test", StringComparison.OrdinalIgnoreCase);
}

public sealed record BrowserSmokeResponseGateArmRequest(string Method, string Path);

public sealed record BrowserSmokeResponseGateSnapshot(string State, int? StatusCode);

public sealed class BrowserSmokeResponseGateRegistry
{
    public const string CookieName = "CoglatasBrowserSmokeResponseGate";
    public const string ResponseHeaderName = "X-Aip-Browser-Smoke-Response-Gate";

    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<Guid, Gate> _gates = new();
    private readonly ConcurrentDictionary<Guid, Guid> _gateIdsByOwner = new();

    public static bool IsAllowedTarget(string method, string path)
    {
        if (!HttpMethods.IsGet(method))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 4 &&
               string.Equals(segments[0], "api", StringComparison.Ordinal) &&
               string.Equals(segments[1], "projects", StringComparison.Ordinal) &&
               Guid.TryParse(segments[2], out _) &&
               (string.Equals(segments[3], "kanban", StringComparison.Ordinal) ||
                string.Equals(segments[3], "gantt", StringComparison.Ordinal));
    }

    public bool TryArm(Guid gateId, Guid ownerUserId, string method, string path)
    {
        if (!IsAllowedTarget(method, path) ||
            !_gateIdsByOwner.TryAdd(ownerUserId, gateId))
        {
            return false;
        }

        var gate = new Gate(
            gateId,
            ownerUserId,
            method,
            path,
            () => Complete(gateId, ownerUserId));
        if (_gates.TryAdd(gateId, gate))
        {
            return true;
        }

        gate.Dispose();
        _gateIdsByOwner.TryRemove(new KeyValuePair<Guid, Guid>(ownerUserId, gateId));
        return false;
    }

    public BrowserSmokeResponseGateSnapshot? GetSnapshot(Guid gateId, Guid ownerUserId)
    {
        return _gates.TryGetValue(gateId, out var gate) && gate.OwnerUserId == ownerUserId
            ? gate.Snapshot()
            : null;
    }

    public bool TryRelease(Guid gateId, Guid ownerUserId)
    {
        if (!_gates.TryGetValue(gateId, out var gate) || gate.OwnerUserId != ownerUserId)
        {
            return false;
        }

        gate.Release();
        return true;
    }

    public bool TryClaim(
        Guid gateId,
        Guid ownerUserId,
        string method,
        string path,
        out BrowserSmokeResponseGateLease? lease)
    {
        lease = null;
        if (!_gates.TryGetValue(gateId, out var gate) ||
            gate.OwnerUserId != ownerUserId ||
            !string.Equals(gate.Method, method, StringComparison.Ordinal) ||
            !string.Equals(gate.Path, path, StringComparison.Ordinal) ||
            !gate.TryClaim())
        {
            return false;
        }

        lease = new BrowserSmokeResponseGateLease(this, gate);
        return true;
    }

    internal void Complete(Guid gateId, Guid ownerUserId)
    {
        if (_gates.TryRemove(gateId, out var gate))
        {
            gate.Release();
            gate.Dispose();
        }

        _gateIdsByOwner.TryRemove(new KeyValuePair<Guid, Guid>(ownerUserId, gateId));
    }

    internal sealed class Gate : IDisposable
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Timer _expiry;
        private int _claimed;
        private int _ready;
        private int _released;
        private int _statusCode = -1;

        public Gate(
            Guid id,
            Guid ownerUserId,
            string method,
            string path,
            Action expire)
        {
            Id = id;
            OwnerUserId = ownerUserId;
            Method = method;
            Path = path;
            _expiry = new Timer(_ => expire(), null, MaximumLifetime, Timeout.InfiniteTimeSpan);
        }

        public Guid Id { get; }
        public Guid OwnerUserId { get; }
        public string Method { get; }
        public string Path { get; }

        public bool TryClaim()
        {
            return Interlocked.CompareExchange(ref _claimed, 1, 0) == 0;
        }

        public void MarkResponseReady(int responseStatusCode)
        {
            Interlocked.Exchange(ref _statusCode, responseStatusCode);
            Interlocked.Exchange(ref _ready, 1);
        }

        public async Task WaitForReleaseAsync(CancellationToken cancellationToken)
        {
            await _release.Task.WaitAsync(cancellationToken);
        }

        public void Release()
        {
            Interlocked.Exchange(ref _released, 1);
            _release.TrySetResult();
        }

        public BrowserSmokeResponseGateSnapshot Snapshot()
        {
            var state = Volatile.Read(ref _released) == 1
                ? "released"
                : Volatile.Read(ref _ready) == 1
                    ? "waiting"
                    : Volatile.Read(ref _claimed) == 1
                        ? "claimed"
                        : "armed";
            var responseStatusCode = Volatile.Read(ref _statusCode);
            return new BrowserSmokeResponseGateSnapshot(
                state,
                responseStatusCode < 0 ? null : responseStatusCode);
        }

        public void Dispose()
        {
            _expiry.Dispose();
        }
    }
}

public sealed class BrowserSmokeResponseGateLease
{
    private readonly BrowserSmokeResponseGateRegistry _registry;
    private readonly BrowserSmokeResponseGateRegistry.Gate _gate;

    internal BrowserSmokeResponseGateLease(
        BrowserSmokeResponseGateRegistry registry,
        BrowserSmokeResponseGateRegistry.Gate gate)
    {
        _registry = registry;
        _gate = gate;
    }

    public Guid Id => _gate.Id;

    public void MarkResponseReady(int statusCode)
    {
        _gate.MarkResponseReady(statusCode);
    }

    public async Task WaitForReleaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _gate.WaitForReleaseAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The request ended before the test released its one-shot gate.
        }
        finally
        {
            _registry.Complete(_gate.Id, _gate.OwnerUserId);
        }
    }
}

public sealed class BrowserSmokeResponseGateMiddleware(
    RequestDelegate next,
    BrowserSmokeResponseGateRegistry registry)
{
    public async Task InvokeAsync(HttpContext context, ICurrentUser currentUser)
    {
        if (currentUser.IsAuthenticated &&
            currentUser.UserId is { } userId &&
            context.Request.Cookies.TryGetValue(BrowserSmokeResponseGateRegistry.CookieName, out var gateCookie) &&
            Guid.TryParseExact(gateCookie, "N", out var gateId) &&
            registry.TryClaim(
                gateId,
                userId,
                context.Request.Method,
                context.Request.Path.Value ?? string.Empty,
                out var lease))
        {
            context.Response.Headers[BrowserSmokeResponseGateRegistry.ResponseHeaderName] = gateCookie;
            context.Response.OnStarting(async () =>
            {
                lease!.MarkResponseReady(context.Response.StatusCode);
                await lease.WaitForReleaseAsync(context.RequestAborted);
            });
        }

        await next(context);
    }
}
