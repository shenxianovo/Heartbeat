using Heartbeat.Agent.Models;
using Heartbeat.Agent.Services;

namespace Heartbeat.Agent.Tests.Services;

/// <summary>
/// Active 窗口从采集器自报 flushPeriodMs 派生（ADR-026 §3）：3× 容一次丢失 flush + 一次重试。
/// </summary>
public class CollectorActivityTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void NeverSeen_NotActive()
    {
        Assert.False(CollectorActivity.IsActive(lastSeen: null, entry: null, Now));
    }

    [Fact]
    public void WithinDerivedWindow_Active()
    {
        var entry = new CollectorEntry { FlushPeriodMs = 30000 }; // 窗口 90s
        Assert.True(CollectorActivity.IsActive(Now.AddSeconds(-80), entry, Now));
    }

    [Fact]
    public void BeyondDerivedWindow_NotActive()
    {
        var entry = new CollectorEntry { FlushPeriodMs = 30000 }; // 窗口 90s
        Assert.False(CollectorActivity.IsActive(Now.AddSeconds(-100), entry, Now));
    }

    [Fact]
    public void NoReportedPeriod_UsesDefaultWindow()
    {
        // 未报 flushPeriodMs → 回落 90s 默认窗口。
        Assert.True(CollectorActivity.IsActive(Now.AddSeconds(-80), entry: null, Now));
        Assert.False(CollectorActivity.IsActive(Now.AddSeconds(-100), entry: null, Now));
    }

    [Fact]
    public void LongerReportedPeriod_WidensWindow()
    {
        var entry = new CollectorEntry { FlushPeriodMs = 60000 }; // 窗口 180s
        Assert.True(CollectorActivity.IsActive(Now.AddSeconds(-150), entry, Now));
    }
}
