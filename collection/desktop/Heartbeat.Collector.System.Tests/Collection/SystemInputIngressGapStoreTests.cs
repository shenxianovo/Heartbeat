using Heartbeat.Collector.System.Collection;

namespace Heartbeat.Collector.System.Tests.Collection;

public sealed class SystemInputIngressGapStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"heartbeat-system-input-gaps-{Guid.NewGuid():N}");

    [Fact]
    public void DropRangeAndInFlightClaimSurviveCrashWithoutMergingNewLossIntoAcknowledgedGap()
    {
        var path = Path.Combine(_directory, "input-ingress-gaps.json");
        var start = new DateTimeOffset(2026, 8, 30, 10, 0, 0, TimeSpan.Zero);
        var store = SystemInputIngressGapStore.Open(path);
        store.RecordDrop(start);
        store.RecordDrop(start.AddSeconds(1));

        var claimed = Assert.IsType<SystemInputIngressGap>(store.Claim());
        Assert.Equal(start, claimed.Start);
        Assert.Equal(start.AddSeconds(1) + TimeSpan.FromTicks(1), claimed.End);
        Assert.Equal(2, claimed.EstimatedFactsLost);
        Assert.Equal(7, claimed.GapId.Version);

        store.RecordDrop(start.AddSeconds(2));
        var restarted = SystemInputIngressGapStore.Open(path);
        Assert.Equal(claimed, restarted.Claim());
        restarted.Acknowledge(claimed.GapId);

        var next = Assert.IsType<SystemInputIngressGap>(restarted.Claim());
        Assert.NotEqual(claimed.GapId, next.GapId);
        Assert.Equal(start.AddSeconds(2), next.Start);
        Assert.Equal(start.AddSeconds(2) + TimeSpan.FromTicks(1), next.End);
        Assert.Equal(1, next.EstimatedFactsLost);
        restarted.Acknowledge(next.GapId);
        Assert.Null(SystemInputIngressGapStore.Open(path).Claim());
    }

    [Fact]
    public void ConcurrentOutOfOrderDropsPersistTheExactCoveringRange()
    {
        var path = Path.Combine(_directory, "input-ingress-gaps.json");
        var later = new DateTimeOffset(2026, 8, 30, 10, 0, 2, TimeSpan.Zero);
        var earlier = later.AddSeconds(-2);
        var store = SystemInputIngressGapStore.Open(path);

        store.RecordDrop(later);
        store.RecordDrop(earlier);

        var gap = Assert.IsType<SystemInputIngressGap>(
            SystemInputIngressGapStore.Open(path).Peek());
        Assert.Equal(earlier, gap.Start);
        Assert.Equal(later.AddTicks(1), gap.End);
        Assert.Equal(2, gap.EstimatedFactsLost);
    }

    [Fact]
    public void DropAtClaimedInstantPersistsASecondStableGapIdentity()
    {
        var path = Path.Combine(_directory, "input-ingress-gaps.json");
        var occurredAt = new DateTimeOffset(2026, 8, 30, 10, 0, 0, TimeSpan.Zero);
        var store = SystemInputIngressGapStore.Open(path);
        store.RecordDrop(occurredAt);
        var first = Assert.IsType<SystemInputIngressGap>(store.Claim());

        store.RecordDrop(occurredAt);
        var restarted = SystemInputIngressGapStore.Open(path);
        restarted.Acknowledge(first.GapId);
        var second = Assert.IsType<SystemInputIngressGap>(restarted.Claim());

        Assert.NotEqual(first.GapId, second.GapId);
        Assert.Equal(first.Start, second.Start);
        Assert.Equal(first.End, second.End);
        Assert.Equal(1, second.EstimatedFactsLost);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
