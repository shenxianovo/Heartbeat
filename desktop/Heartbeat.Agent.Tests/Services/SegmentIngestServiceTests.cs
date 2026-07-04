using Heartbeat.Agent.Services;
using Heartbeat.Core;
using Heartbeat.Core.DTOs.Segments;

namespace Heartbeat.Agent.Tests.Services;

public class SegmentIngestServiceTests
{
    private sealed class FakeClock : Heartbeat.Agent.Utils.IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
    }

    private static ActivitySegmentItem Segment(
        string source = "browser",
        string identityKey = "https://example.com",
        DateTimeOffset? start = null,
        DateTimeOffset? end = null)
    {
        var s = start ?? DateTimeOffset.UtcNow.AddMinutes(-5);
        return new ActivitySegmentItem
        {
            Id = Guid.CreateVersion7(),
            Source = source,
            IdentityKey = identityKey,
            StartTime = s,
            EndTime = end ?? s.AddMinutes(2)
        };
    }

    [Fact]
    public void Accept_ValidSegments_Buffered()
    {
        var svc = new SegmentIngestService(new FakeClock());

        var accepted = svc.Accept([Segment(), Segment(identityKey: "https://other.com")]);

        Assert.Equal(2, accepted);
        Assert.Equal(2, svc.GetAndClearSegments().Count);
    }

    [Fact]
    public void Accept_SystemSource_Throws()
    {
        var svc = new SegmentIngestService(new FakeClock());

        Assert.Throws<InvalidSourceException>(() => svc.Accept([Segment(source: "system")]));
        Assert.Throws<InvalidSourceException>(() => svc.Accept([Segment(source: "System")]));
    }

    [Fact]
    public void Accept_MissingId_AssignsUuid()
    {
        var svc = new SegmentIngestService(new FakeClock());
        var seg = Segment();
        seg.Id = Guid.Empty;

        var accepted = svc.Accept([seg]);

        Assert.Equal(1, accepted);
        Assert.NotEqual(Guid.Empty, svc.GetAndClearSegments()[0].Id);
    }

    [Fact]
    public void Accept_ZeroLengthSegment_Accepted()
    {
        // 点事件 = 零长度段(ADR-017 §3)
        var svc = new SegmentIngestService(new FakeClock());
        var t = DateTimeOffset.UtcNow.AddMinutes(-1);

        var accepted = svc.Accept([Segment(start: t, end: t)]);

        Assert.Equal(1, accepted);
    }

    [Fact]
    public void Accept_InvalidSegments_Filtered()
    {
        var svc = new SegmentIngestService(new FakeClock());
        var missingKey = Segment();
        missingKey.IdentityKey = "";
        var reversed = Segment(start: DateTimeOffset.UtcNow, end: DateTimeOffset.UtcNow.AddMinutes(-5));

        var accepted = svc.Accept([missingKey, reversed]);

        Assert.Equal(0, accepted);
        Assert.Empty(svc.GetAndClearSegments());
    }

    [Fact]
    public void Accept_SameIdSnapshot_ReplacesEarlier()
    {
        // 缓冲按 Id 键控（ADR-018）：同段后到快照覆盖先到，攒批自动压缩
        var svc = new SegmentIngestService(new FakeClock());
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-5);
        var first = Segment(start: t0, end: t0.AddMinutes(1));
        var second = Segment(start: t0, end: t0.AddMinutes(3));
        second.Id = first.Id;

        svc.Accept([first]);
        svc.Accept([second]);

        var drained = svc.GetAndClearSegments();
        var single = Assert.Single(drained);
        Assert.Equal(first.Id, single.Id);
        Assert.Equal(t0.AddMinutes(3), single.EndTime);
    }

    [Fact]
    public void GetAndClearSegments_DrainsBuffer()
    {
        var svc = new SegmentIngestService(new FakeClock());
        svc.Accept([Segment()]);

        Assert.Single(svc.GetAndClearSegments());
        Assert.Empty(svc.GetAndClearSegments());
    }
}
