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
    public void Accept_SystemSource_Buffered()
    {
        // source 无关（ADR-020）：冒充守卫在 loopback 协议层，
        // 内置采集器进程内直调 Accept，system 段照常入缓冲。
        var svc = new SegmentIngestService(new FakeClock());

        var accepted = svc.Accept([Segment(source: "system", identityKey: "code|main.cs")]);

        Assert.Equal(1, accepted);
        Assert.Single(svc.GetAndClearSegments());
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

    [Fact]
    public void Reinject_Absent_Inserts()
    {
        var svc = new SegmentIngestService(new FakeClock());
        IUploadSource<ActivitySegmentItem> source = svc;
        var seg = Segment();

        source.Reinject([seg]);

        Assert.Same(seg, Assert.Single(svc.GetAndClearSegments()));
    }

    [Fact]
    public void Reinject_DoesNotRollBackNewerSnapshot()
    {
        // 不回滚（ADR-022）：批次在外期间 hub 已收到同 Id 更新快照，
        // 退回的旧快照不得覆盖——与服务端单调生长门同一条规则（ADR-018）。
        var svc = new SegmentIngestService(new FakeClock());
        IUploadSource<ActivitySegmentItem> source = svc;
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-5);
        var stale = Segment(start: t0, end: t0.AddMinutes(1));
        var newer = Segment(start: t0, end: t0.AddMinutes(3));
        newer.Id = stale.Id;

        svc.Accept([newer]);
        source.Reinject([stale]);

        var single = Assert.Single(svc.GetAndClearSegments());
        Assert.Equal(t0.AddMinutes(3), single.EndTime);
    }
}
