using Heartbeat.Server.Calendar;
using Heartbeat.Server.Services;

namespace Heartbeat.Server.Tests.Services;

/// <summary>
/// 生成互斥（ADR-042 §7 / ADR-044 §3）：进程内按 (OwnerId, WindowKey) 一把锁，撞上的请求不排队、直接 409。
/// 锁的粒度就是契约本身——粒度错了要么白挡（同一天并发烧两次 token），要么误挡（不同用户
/// 或不同日互相阻塞）。
/// </summary>
public class RecapGenerationLockTests
{
    private static readonly ResolvedCalendarWindow Day = new(
        1, "day", "2026-07-10", "Etc/UTC",
        DateTimeOffset.Parse("2026-07-10T00:00:00Z"),
        DateTimeOffset.Parse("2026-07-11T00:00:00Z"),
        new NodaTime.LocalDate(2026, 7, 10),
        new NodaTime.LocalDate(2026, 7, 11));

    [Fact]
    public void SameKey_SecondAcquire_ReturnsNull()
    {
        var locks = new RecapGenerationLock();

        using var first = locks.TryAcquire("user-1", Day.WindowKey);

        Assert.NotNull(first);
        Assert.Null(locks.TryAcquire("user-1", Day.WindowKey)); // 第二个请求不排队
    }

    [Fact]
    public void AfterDispose_KeyIsAcquirableAgain()
    {
        var locks = new RecapGenerationLock();

        var first = locks.TryAcquire("user-1", Day.WindowKey);
        Assert.NotNull(first);
        first.Dispose();

        // 租约寿命 = 流的寿命：上一条流结束（含失败与客户端断开）后，重试必须能再拿到锁。
        using var second = locks.TryAcquire("user-1", Day.WindowKey);
        Assert.NotNull(second);
    }

    [Fact]
    public void RepeatedDispose_DoesNotReleaseSomeoneElsesLease()
    {
        var locks = new RecapGenerationLock();

        var first = locks.TryAcquire("user-1", Day.WindowKey)!;
        first.Dispose();
        using var second = locks.TryAcquire("user-1", Day.WindowKey);
        first.Dispose(); // 迭代器/using 嵌套下同一个租约可能被 dispose 两次

        Assert.NotNull(second);
        Assert.Null(locks.TryAcquire("user-1", Day.WindowKey)); // second 仍然握着
    }

    [Fact]
    public void DifferentOwnersOrWindowKeys_DoNotBlockEachOther()
    {
        var locks = new RecapGenerationLock();

        using var mine = locks.TryAcquire("user-1", Day.WindowKey);
        using var others = locks.TryAcquire("user-2", Day.WindowKey);
        using var anotherDay = locks.TryAcquire("user-1", (Day with
        {
            LocalDate = "2026-07-09",
            Start = Day.Start.AddDays(-1),
            EndExclusive = Day.EndExclusive.AddDays(-1),
        }).WindowKey);

        Assert.NotNull(mine);
        Assert.NotNull(others);
        Assert.NotNull(anotherDay);
    }

    [Fact]
    public void TimeZoneEndKindAndVersionEachProduceIndependentLocks()
    {
        var locks = new RecapGenerationLock();

        using var baseline = locks.TryAcquire("user-1", Day.WindowKey);
        using var timezone = locks.TryAcquire("user-1", (Day with { TimeZone = "Asia/Shanghai" }).WindowKey);
        using var end = locks.TryAcquire("user-1", (Day with { EndExclusive = Day.EndExclusive.AddHours(1) }).WindowKey);
        using var kind = locks.TryAcquire("user-1", (Day with { Kind = "week" }).WindowKey);
        using var version = locks.TryAcquire("user-1", (Day with { Version = 2 }).WindowKey);

        Assert.NotNull(baseline);
        Assert.NotNull(timezone);
        Assert.NotNull(end);
        Assert.NotNull(kind);
        Assert.NotNull(version);
    }
}
