using Heartbeat.Core;
using Heartbeat.Server.Services;

namespace Heartbeat.Server.Tests.Services;

/// <summary>
/// 生成互斥（ADR-042 §7）：进程内按 (OwnerId, WindowStart) 一把锁，撞上的请求不排队、直接 409。
/// 锁的粒度就是契约本身——粒度错了要么白挡（同一天并发烧两次 token），要么误挡（不同用户
/// 或不同日互相阻塞）。
/// </summary>
public class RecapGenerationLockTests
{
    private static readonly DateTimeOffset Day = new(2026, 7, 10, 0, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset WindowOf(DateTimeOffset date) => DateRange.Day(date).UtcStart;

    [Fact]
    public void SameKey_SecondAcquire_ReturnsNull()
    {
        var locks = new RecapGenerationLock();

        using var first = locks.TryAcquire("user-1", WindowOf(Day));

        Assert.NotNull(first);
        Assert.Null(locks.TryAcquire("user-1", WindowOf(Day))); // 第二个请求不排队
    }

    [Fact]
    public void AfterDispose_KeyIsAcquirableAgain()
    {
        var locks = new RecapGenerationLock();

        var first = locks.TryAcquire("user-1", WindowOf(Day));
        Assert.NotNull(first);
        first.Dispose();

        // 租约寿命 = 流的寿命：上一条流结束（含失败与客户端断开）后，重试必须能再拿到锁。
        using var second = locks.TryAcquire("user-1", WindowOf(Day));
        Assert.NotNull(second);
    }

    [Fact]
    public void RepeatedDispose_DoesNotReleaseSomeoneElsesLease()
    {
        var locks = new RecapGenerationLock();

        var first = locks.TryAcquire("user-1", WindowOf(Day))!;
        first.Dispose();
        using var second = locks.TryAcquire("user-1", WindowOf(Day));
        first.Dispose(); // 迭代器/using 嵌套下同一个租约可能被 dispose 两次

        Assert.NotNull(second);
        Assert.Null(locks.TryAcquire("user-1", WindowOf(Day))); // second 仍然握着
    }

    [Fact]
    public void DifferentOwnersOrDays_DoNotBlockEachOther()
    {
        var locks = new RecapGenerationLock();

        using var mine = locks.TryAcquire("user-1", WindowOf(Day));
        using var others = locks.TryAcquire("user-2", WindowOf(Day));
        using var anotherDay = locks.TryAcquire("user-1", WindowOf(Day.AddDays(-1)));

        Assert.NotNull(mine);
        Assert.NotNull(others);
        Assert.NotNull(anotherDay);
    }

    [Fact]
    public void SameDayWindow_DifferentInstants_CollideAsOne()
    {
        var locks = new RecapGenerationLock();

        // 键是 UTC 窗口起点而非入参时刻：同一天的两次请求，日期写法不同也算撞车。
        using var noon = locks.TryAcquire("user-1", WindowOf(Day.AddHours(12)));

        Assert.NotNull(noon);
        Assert.Null(locks.TryAcquire("user-1", WindowOf(Day.AddHours(23))));
    }
}
