using Heartbeat.Core.DTOs.Usage;

namespace Heartbeat.Core.Tests;

public class SnapshotCompactionTests
{
    private static readonly DateTimeOffset Base = new(2025, 6, 1, 10, 0, 0, TimeSpan.Zero);

    private static AppUsageItem Item(Guid id, int startSec, int endSec) => new()
    {
        Id = id,
        AppName = "app",
        StartTime = Base.AddSeconds(startSec),
        EndTime = Base.AddSeconds(endSec)
    };

    [Fact]
    public void SameId_KeepsLatestSnapshot()
    {
        var id = Guid.CreateVersion7();
        var result = SnapshotCompaction.KeepLatest([Item(id, 0, 60), Item(id, 0, 120), Item(id, 0, 90)]);

        var single = Assert.Single(result);
        Assert.Equal(Base.AddSeconds(120), single.EndTime);
    }

    [Fact]
    public void DistinctIds_AllKept_SortedByStart()
    {
        var result = SnapshotCompaction.KeepLatest([
            Item(Guid.CreateVersion7(), 60, 120),
            Item(Guid.CreateVersion7(), 0, 59)
        ]);

        Assert.Equal(2, result.Count);
        Assert.Equal(Base, result[0].StartTime);
    }

    [Fact]
    public void LegacyEmptyIds_PassThrough_NotGrouped()
    {
        // 旧版缓存无 Id：绝不能把不同活动误并成一条
        var result = SnapshotCompaction.KeepLatest([
            Item(Guid.Empty, 0, 60),
            Item(Guid.Empty, 61, 120)
        ]);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void EmptyAndSingle_ReturnedAsIs()
    {
        Assert.Empty(SnapshotCompaction.KeepLatest(new List<AppUsageItem>()));
        Assert.Single(SnapshotCompaction.KeepLatest([Item(Guid.CreateVersion7(), 0, 60)]));
    }
}
