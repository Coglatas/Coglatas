using Coglatas.Web.Realtime;

namespace Coglatas.Tests.Realtime;

public sealed class RealtimeDiagnosticsAuditTests
{
    [Fact]
    public void Parallel_recording_preserves_all_public_snapshot_counters()
    {
        var diagnostics = new RealtimeDiagnostics();
        Parallel.For(0, 128, _ =>
        {
            diagnostics.RecordDispatchSuccess();
            diagnostics.RecordDispatchFailure();
            diagnostics.RecordSubscriptionDenial();
            diagnostics.RecordDispatcherFailure();
        });
        var snapshot = diagnostics.Snapshot();
        Assert.Equal(128L, snapshot.DispatchSuccessCount);
        Assert.Equal(128L, snapshot.DispatchFailureCount);
        Assert.Equal(128L, snapshot.SubscriptionDenialCount);
        Assert.Equal(128L, snapshot.DispatcherFailureCount);
    }
}
