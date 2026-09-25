using System;
using AIRadio.Desktop.Services;

namespace AIRadio.Desktop.Tests;

public sealed class SourceHealthRegistryDiagnosticsTests
{
    [Fact]
    public void Snapshot_BoundsRecentWindowAndKeepsOperationTimestamps()
    {
        var now = new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero);
        var registry = new SourceHealthRegistry(() => now);
        registry.RecordSuccess("source", SourceOperation.Search);
        var searchAt = now;
        now = now.AddMinutes(1);
        registry.RecordSuccess("source", SourceOperation.Resolution);
        for (var index = 0; index < 21; index++)
            registry.RecordTransportFailure("source", SourceOperation.Resolution);

        var snapshot = registry.Snapshot("source");
        Assert.Equal(searchAt, snapshot.LastSearchSuccessUtc);
        Assert.Equal(now, snapshot.LastResolutionSuccessUtc);
        Assert.Equal(20, snapshot.RecentRequestCount);
        Assert.Equal(0, snapshot.RecentSuccessCount);
        Assert.True(snapshot.CircuitRemaining > TimeSpan.Zero);
        registry.Reset("source");
        Assert.Equal(TimeSpan.Zero, registry.Snapshot("source").CircuitRemaining);
    }

    [Fact]
    public void BusinessResponse_ResetsCircuitWithoutClaimingPlayableMedia()
    {
        var registry = new SourceHealthRegistry();
        registry.RecordTransportFailure("source", SourceOperation.Resolution);
        registry.RecordTransportFailure("source", SourceOperation.Resolution);

        registry.RecordBusinessResponse("source");

        var snapshot = registry.Snapshot("source");
        Assert.Equal(0, snapshot.ConsecutiveTransportFailures);
        Assert.Null(snapshot.LastResolutionSuccessUtc);
        Assert.Equal(3, snapshot.RecentRequestCount);
        Assert.Equal(1, snapshot.RecentSuccessCount);
    }
}
