namespace Coglatas.Web.Realtime;

public sealed class RealtimeDiagnostics
{
    private long _dispatchSuccessCount;
    private long _dispatchFailureCount;
    private long _subscriptionDenialCount;
    private long _dispatcherFailureCount;

    public void RecordDispatchSuccess() => Interlocked.Increment(ref _dispatchSuccessCount);
    public void RecordDispatchFailure() => Interlocked.Increment(ref _dispatchFailureCount);
    public void RecordSubscriptionDenial() => Interlocked.Increment(ref _subscriptionDenialCount);
    public void RecordDispatcherFailure() => Interlocked.Increment(ref _dispatcherFailureCount);

    public RealtimeDiagnosticCounters Snapshot() => new(
        Interlocked.Read(ref _dispatchSuccessCount),
        Interlocked.Read(ref _dispatchFailureCount),
        Interlocked.Read(ref _subscriptionDenialCount),
        Interlocked.Read(ref _dispatcherFailureCount));
}

public sealed record RealtimeDiagnosticCounters(long DispatchSuccessCount, long DispatchFailureCount, long SubscriptionDenialCount, long DispatcherFailureCount);
