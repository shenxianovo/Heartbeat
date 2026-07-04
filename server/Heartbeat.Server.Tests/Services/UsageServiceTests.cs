using Heartbeat.Core;
using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Core.DTOs.Usage;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Heartbeat.Server.Services;
using Heartbeat.Server.Tests.Fixtures;
using System.Text.Json;

namespace Heartbeat.Server.Tests.Services;

[Collection("postgres")]
public class UsageServiceTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    private long _deviceId;

    protected override async Task SeedAsync(AppDbContext db)
    {
        var device = new Device
        {
            OwnerId = "user-1",
            HardwareId = "hw-1",
            DeviceName = "Test PC"
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        _deviceId = device.Id;
    }

    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    private static AppUsageItem Item(string app, DateTimeOffset start, DateTimeOffset end) => new()
    {
        Id = Guid.CreateVersion7(),
        AppName = app,
        StartTime = start,
        EndTime = end
    };

    private ActivitySegment SystemSegment(long appId, string appName, DateTimeOffset start, DateTimeOffset end, string? title = null) => new()
    {
        Id = Guid.CreateVersion7(),
        DeviceId = _deviceId,
        Source = ActivitySources.System,
        IdentityKey = SystemIdentity.Key(appName, title),
        AppId = appId,
        Title = title,
        StartTime = start,
        EndTime = end
    };

    [Fact]
    public async Task SaveUsage_ValidRecords_CreatesAppsAndSegments()
    {
        using var db = CreateDbContext();
        var svc = new UsageService(db);

        var start = Now.AddMinutes(-5);
        var end = Now.AddMinutes(-2);
        var request = new UsageUploadRequest
        {
            Usages = [Item("VSCode", start, end)]
        };

        await svc.SaveUsageAsync(_deviceId, request);

        Assert.Single(db.Apps);
        Assert.Equal("VSCode", db.Apps.First().Name);
        var segment = db.ActivitySegments.Single();
        Assert.Equal(ActivitySources.System, segment.Source);
        Assert.Equal(SystemIdentity.Key("VSCode", null), segment.IdentityKey);
        Assert.NotEqual(Guid.Empty, segment.Id);
    }

    [Fact]
    public async Task SaveUsage_FiltersInvalidRecords()
    {
        using var db = CreateDbContext();
        var svc = new UsageService(db);

        var request = new UsageUploadRequest
        {
            Usages =
            [
                Item("", Now.AddMinutes(-5), Now.AddMinutes(-2)),         // empty name
                Item("App", default, Now),                                 // default start
                Item("App", Now.AddMinutes(-2), Now.AddMinutes(-5)),       // end < start
                Item("App", new DateTimeOffset(2019, 1, 1, 0, 0, 0, TimeSpan.Zero), Now), // year < 2020
                Item("App", Now.AddHours(-25), Now),                       // duration > 24h
                Item("App", Now.AddMinutes(20), Now.AddMinutes(30)),       // future beyond skew
            ]
        };

        await svc.SaveUsageAsync(_deviceId, request);

        Assert.Empty(db.ActivitySegments);
    }

    [Fact]
    public async Task SaveUsage_SnapshotReupload_ExtendsExistingRow()
    {
        using var db = CreateDbContext();
        var svc = new UsageService(db);

        // 同 Id 快照重传（ADR-018）：flush 周期性上报进行中段，EndTime 单调生长
        var id = Guid.CreateVersion7();
        var t0 = Now.AddMinutes(-10);

        var snapshot1 = Item("VSCode", t0, t0.AddMinutes(1));
        snapshot1.Id = id;
        await svc.SaveUsageAsync(_deviceId, new UsageUploadRequest { Usages = [snapshot1] });

        var snapshot2 = Item("VSCode", t0, t0.AddMinutes(5));
        snapshot2.Id = id;
        await svc.SaveUsageAsync(_deviceId, new UsageUploadRequest { Usages = [snapshot2] });

        var row = db.ActivitySegments.Single();
        Assert.Equal(t0, row.StartTime);
        Assert.Equal(t0.AddMinutes(5), row.EndTime);
    }

    [Fact]
    public async Task SaveUsage_OutOfOrderOldSnapshot_DoesNotShrinkRow()
    {
        using var db = CreateDbContext();
        var svc = new UsageService(db);

        // 离线缓存迟到重传：旧快照晚于新快照到达，行不得回退（摄入可交换，ADR-018）
        var id = Guid.CreateVersion7();
        var t0 = Now.AddMinutes(-10);

        var newer = Item("VSCode", t0, t0.AddMinutes(5));
        newer.Id = id;
        await svc.SaveUsageAsync(_deviceId, new UsageUploadRequest { Usages = [newer] });

        var older = Item("VSCode", t0, t0.AddMinutes(1));
        older.Id = id;
        await svc.SaveUsageAsync(_deviceId, new UsageUploadRequest { Usages = [older] });

        var row = db.ActivitySegments.Single();
        Assert.Equal(t0.AddMinutes(5), row.EndTime);
    }

    [Fact]
    public async Task SaveUsage_DistinctIds_AdjacentSameActivity_StayTwoRows()
    {
        using var db = CreateDbContext();
        var svc = new UsageService(db);

        // ADR-018 行为变化：不同 Id 即不同活动，同 App+Title 首尾相连也不再启发式粘合
        var t0 = Now.AddMinutes(-10);
        var request = new UsageUploadRequest
        {
            Usages =
            [
                Item("VSCode", t0, t0.AddMinutes(3)),
                Item("VSCode", t0.AddMinutes(3), t0.AddMinutes(5))
            ]
        };

        await svc.SaveUsageAsync(_deviceId, request);

        Assert.Equal(2, db.ActivitySegments.Count());
    }

    [Fact]
    public async Task SaveSegments_PluginSnapshots_GrowOneRow_AttributesLastWriteWins()
    {
        using var db = CreateDbContext();
        var svc = new UsageService(db);

        var id = Guid.CreateVersion7();
        ActivitySegmentItem Snapshot(DateTimeOffset start, DateTimeOffset end, string attrsJson) => new()
        {
            Id = id,
            Source = "browser",
            IdentityKey = "https://example.com/page",
            AppName = "msedge",
            StartTime = start,
            EndTime = end,
            Attributes = JsonSerializer.Deserialize<JsonElement>(attrsJson)
        };

        // 第一批：落库 + 建 App 关联
        var t0 = Now.AddMinutes(-10);
        await svc.SaveSegmentsAsync(_deviceId, [Snapshot(t0, t0.AddMinutes(3), """{"url":"https://example.com/page"}""")]);

        var seg = db.ActivitySegments.Single();
        Assert.Equal("browser", seg.Source);
        Assert.NotNull(seg.AppId);
        Assert.Contains("example.com", seg.Attributes);

        // 第二批：同 Id 快照 → 同一行生长，attributes 后写胜（ADR-018）
        await svc.SaveSegmentsAsync(_deviceId, [Snapshot(t0, t0.AddMinutes(5), """{"url":"https://example.com/page","scroll":42}""")]);

        var grown = db.ActivitySegments.Single();
        Assert.Equal(t0, grown.StartTime);
        Assert.Equal(t0.AddMinutes(5), grown.EndTime);
        Assert.Contains("scroll", grown.Attributes);
    }

    [Fact]
    public async Task SaveSegments_InBatchSnapshotsSameId_ConvergeToOneRow()
    {
        using var db = CreateDbContext();
        var svc = new UsageService(db);

        // 枢纽攒批场景：插件 30s 推一次、Agent 1min 传一次，一批里带同 Id 多个快照
        var id = Guid.CreateVersion7();
        var t0 = Now.AddMinutes(-10);
        ActivitySegmentItem Snapshot(DateTimeOffset end) => new()
        {
            Id = id,
            Source = "vscode",
            IdentityKey = "d:/repo/file.cs",
            StartTime = t0,
            EndTime = end
        };

        await svc.SaveSegmentsAsync(_deviceId, [Snapshot(t0.AddSeconds(30)), Snapshot(t0.AddSeconds(60)), Snapshot(t0.AddSeconds(90))]);

        var row = db.ActivitySegments.Single();
        Assert.Equal(t0.AddSeconds(90), row.EndTime);
    }

    [Fact]
    public async Task SaveSegments_IdReuseWithDifferentIdentity_IsRejected()
    {
        using var db = CreateDbContext();
        var svc = new UsageService(db);

        // 身份守卫（ADR-018 §2）：同 Id 但 Source/IdentityKey 不符 → 拒收，既有行不动
        var id = Guid.CreateVersion7();
        var t0 = Now.AddMinutes(-10);
        ActivitySegmentItem Seg(string source, string key, DateTimeOffset end) => new()
        {
            Id = id,
            Source = source,
            IdentityKey = key,
            StartTime = t0,
            EndTime = end
        };

        await svc.SaveSegmentsAsync(_deviceId, [Seg("browser", "https://example.com", t0.AddMinutes(2))]);
        await svc.SaveSegmentsAsync(_deviceId, [Seg("vscode", "d:/repo/file.cs", t0.AddMinutes(9))]);

        var row = db.ActivitySegments.Single();
        Assert.Equal("browser", row.Source);
        Assert.Equal(t0.AddMinutes(2), row.EndTime);
    }

    [Fact]
    public async Task GetSegments_DefaultExcludesSystem_SourceParamFilters()
    {
        using var db = CreateDbContext();
        var svc = new UsageService(db);

        var app = new App { Name = "msedge" };
        db.Apps.Add(app);
        await db.SaveChangesAsync();

        var t0 = Now.AddMinutes(-10);
        db.ActivitySegments.Add(SystemSegment(app.Id, "msedge", t0, t0.AddMinutes(5)));
        db.ActivitySegments.Add(new ActivitySegment
        {
            Id = Guid.CreateVersion7(),
            DeviceId = _deviceId,
            Source = "browser",
            IdentityKey = "https://example.com",
            AppId = app.Id,
            StartTime = t0,
            EndTime = t0.AddMinutes(2),
            Attributes = """{"url":"https://example.com"}"""
        });
        db.ActivitySegments.Add(new ActivitySegment
        {
            Id = Guid.CreateVersion7(),
            DeviceId = _deviceId,
            Source = "vscode",
            IdentityKey = "d:/repo/file.cs",
            StartTime = t0,
            EndTime = t0.AddMinutes(1)
        });
        await db.SaveChangesAsync();

        // 默认:全部非 system 轨(system 轨走 GetUsageAsync,互补不重叠)
        var all = await svc.GetSegmentsAsync("user-1", null, null, null, null, null);
        Assert.Equal(2, all.Count);
        Assert.DoesNotContain(all, s => s.Source == ActivitySources.System);

        // source 过滤 + AppName 关联提示带出
        var browser = await svc.GetSegmentsAsync("user-1", null, "browser", null, null, null);
        var seg = Assert.Single(browser);
        Assert.Equal("msedge", seg.AppName);
        Assert.Contains("example.com", seg.Attributes);

        // appId 过滤:vscode 段无 AppId,不命中
        var byApp = await svc.GetSegmentsAsync("user-1", null, null, app.Id, null, null);
        Assert.Single(byApp);

        // owner 隔离
        Assert.Empty(await svc.GetSegmentsAsync("user-2", null, null, null, null, null));
    }

    [Fact]
    public async Task WindowQueries_UseOverlapSemantics_LongSegmentCrossingWindowIsVisible()
    {
        using var db = CreateDbContext();
        var svc = new UsageService(db);

        var app = new App { Name = "vscode" };
        db.Apps.Add(app);
        await db.SaveChangesAsync();

        // 3 小时长段（快照生长的产物），起点在查询窗口之前（ADR-018 §4）
        var t0 = Now.AddHours(-4);
        db.ActivitySegments.Add(SystemSegment(app.Id, "vscode", t0, t0.AddHours(3)));
        db.ActivitySegments.Add(new ActivitySegment
        {
            Id = Guid.CreateVersion7(),
            DeviceId = _deviceId,
            Source = "vscode",
            IdentityKey = "d:/repo/file.cs",
            StartTime = t0,
            EndTime = t0.AddHours(3)
        });
        await db.SaveChangesAsync();

        // 窗口 [t0+2h, t0+4h)：段起点在窗口外、区间与窗口重叠 → 两条查询路径都应返回
        var windowStart = t0.AddHours(2);
        var windowEnd = t0.AddHours(4);

        var usage = await svc.GetUsageAsync("user-1", null, windowStart, windowEnd);
        Assert.Single(usage);

        var segments = await svc.GetSegmentsAsync("user-1", null, null, null, windowStart, windowEnd);
        Assert.Single(segments);

        // 窗口完全在段结束之后 → 不返回
        Assert.Empty(await svc.GetUsageAsync("user-1", null, t0.AddHours(3.5), t0.AddHours(4)));
    }

    [Fact]
    public async Task SaveUsage_ReuploadSameBatch_IsIdempotent()
    {
        using var db = CreateDbContext();
        var svc = new UsageService(db);

        // 两条不可合并的段(不同 App),整批重传(离线缓存重试场景)
        var request = new UsageUploadRequest
        {
            Usages =
            [
                Item("VSCode", Now.AddMinutes(-10), Now.AddMinutes(-8)),
                Item("msedge", Now.AddMinutes(-7), Now.AddMinutes(-5))
            ]
        };

        await svc.SaveUsageAsync(_deviceId, request);
        await svc.SaveUsageAsync(_deviceId, request);

        Assert.Equal(2, db.ActivitySegments.Count());
    }

    [Fact]
    public async Task SaveUsage_CreatesApp_WhenNotExists()
    {
        using var db = CreateDbContext();
        var svc = new UsageService(db);

        var request = new UsageUploadRequest
        {
            Usages =
            [
                Item("NewApp1", Now.AddMinutes(-5), Now.AddMinutes(-3)),
                Item("NewApp2", Now.AddMinutes(-3), Now.AddMinutes(-1))
            ]
        };

        await svc.SaveUsageAsync(_deviceId, request);

        Assert.Equal(2, db.Apps.Count());
        Assert.Equal(2, db.ActivitySegments.Count());
    }

    [Fact]
    public async Task SaveUsage_ReusesExistingApp()
    {
        using var db = CreateDbContext();
        var svc = new UsageService(db);

        db.Apps.Add(new App { Name = "VSCode" });
        await db.SaveChangesAsync();

        var request = new UsageUploadRequest
        {
            Usages = [Item("VSCode", Now.AddMinutes(-5), Now.AddMinutes(-2))]
        };

        await svc.SaveUsageAsync(_deviceId, request);

        Assert.Single(db.Apps);
        Assert.Single(db.ActivitySegments);
    }

    [Fact]
    public async Task GetUsage_ComputesDurationSeconds_FromInterval()
    {
        using var db = CreateDbContext();
        var svc = new UsageService(db);

        var start = Now.AddMinutes(-5);
        var end = Now.AddMinutes(-2);
        var request = new UsageUploadRequest
        {
            Usages = [Item("VSCode", start, end)]
        };

        await svc.SaveUsageAsync(_deviceId, request);

        // 时长是派生量（ADR-018）：不落盘，查询投影现算
        var usage = Assert.Single(await svc.GetUsageAsync("user-1", null, null, null));
        Assert.Equal((int)(end - start).TotalSeconds, usage.DurationSeconds);
    }
}
