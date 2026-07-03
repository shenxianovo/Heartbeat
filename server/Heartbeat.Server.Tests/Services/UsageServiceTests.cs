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
        IdentityKey = UsageMerger.SystemIdentityKey(appName, title),
        AppId = appId,
        Title = title,
        StartTime = start,
        EndTime = end,
        DurationSeconds = (int)(end - start).TotalSeconds
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
        Assert.Equal(UsageMerger.SystemIdentityKey("VSCode", null), segment.IdentityKey);
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
    public async Task SaveUsage_MergesWithExistingRecord_WhenOverlapping()
    {
        using var db = CreateDbContext();
        var svc = new UsageService(db);

        var app = new App { Name = "VSCode" };
        db.Apps.Add(app);
        await db.SaveChangesAsync();

        var existingStart = Now.AddMinutes(-10);
        var existingEnd = Now.AddMinutes(-5);
        db.ActivitySegments.Add(SystemSegment(app.Id, "VSCode", existingStart, existingEnd));
        await db.SaveChangesAsync();

        // Upload overlapping record
        var newStart = Now.AddMinutes(-6);
        var newEnd = Now.AddMinutes(-3);
        var request = new UsageUploadRequest
        {
            Usages = [Item("VSCode", newStart, newEnd)]
        };

        await svc.SaveUsageAsync(_deviceId, request);

        var segments = db.ActivitySegments.ToList();
        Assert.Single(segments);
        Assert.Equal(existingStart, segments[0].StartTime);
        Assert.Equal(newEnd, segments[0].EndTime);
    }

    [Fact]
    public async Task SaveUsage_DoesNotMerge_WhenGapExceedsTolerance()
    {
        using var db = CreateDbContext();
        var svc = new UsageService(db);

        var app = new App { Name = "VSCode" };
        db.Apps.Add(app);
        await db.SaveChangesAsync();

        db.ActivitySegments.Add(SystemSegment(app.Id, "VSCode", Now.AddMinutes(-15), Now.AddMinutes(-10)));
        await db.SaveChangesAsync();

        // New record starts 5 minutes after existing ends — no merge
        var request = new UsageUploadRequest
        {
            Usages = [Item("VSCode", Now.AddMinutes(-4), Now.AddMinutes(-2))]
        };

        await svc.SaveUsageAsync(_deviceId, request);

        Assert.Equal(2, db.ActivitySegments.Count());
    }

    [Fact]
    public async Task SaveUsage_DoesNotMerge_AcrossDifferentTitles()
    {
        using var db = CreateDbContext();
        var svc = new UsageService(db);

        var app = new App { Name = "msedge" };
        db.Apps.Add(app);
        await db.SaveChangesAsync();

        // 库内最新记录:同 App 但标题不同 → IdentityKey 不同,首尾相连也不续接(ADR-015/017)
        var existingEnd = Now.AddMinutes(-5);
        db.ActivitySegments.Add(SystemSegment(app.Id, "msedge", Now.AddMinutes(-10), existingEnd, "YouTube"));
        await db.SaveChangesAsync();

        var item = Item("msedge", existingEnd, Now.AddMinutes(-3));
        item.Title = "GitHub";
        var request = new UsageUploadRequest { Usages = [item] };

        await svc.SaveUsageAsync(_deviceId, request);

        Assert.Equal(2, db.ActivitySegments.Count());
    }

    [Fact]
    public async Task SaveSegments_PluginSource_PersistsWithAttributes_AndContinues()
    {
        using var db = CreateDbContext();
        var svc = new UsageService(db);

        ActivitySegmentItem BrowserSegment(DateTimeOffset start, DateTimeOffset end, string attrsJson) => new()
        {
            Id = Guid.CreateVersion7(),
            Source = "browser",
            IdentityKey = "https://example.com/page",
            AppName = "msedge",
            StartTime = start,
            EndTime = end,
            Attributes = JsonSerializer.Deserialize<JsonElement>(attrsJson)
        };

        // 第一批:落库 + 建 App 关联
        var t0 = Now.AddMinutes(-10);
        await svc.SaveSegmentsAsync(_deviceId, [BrowserSegment(t0, t0.AddMinutes(3), """{"url":"https://example.com/page"}""")]);

        var seg = db.ActivitySegments.Single();
        Assert.Equal("browser", seg.Source);
        Assert.NotNull(seg.AppId);
        Assert.Contains("example.com", seg.Attributes);

        // 第二批:首尾相连(flush 封口重开场景) → 续接为一条,attributes 取最新
        var t1 = t0.AddMinutes(3);
        await svc.SaveSegmentsAsync(_deviceId, [BrowserSegment(t1, t1.AddMinutes(2), """{"url":"https://example.com/page","scroll":42}""")]);

        var merged = db.ActivitySegments.Single();
        Assert.Equal(t0, merged.StartTime);
        Assert.Equal(t1.AddMinutes(2), merged.EndTime);
        Assert.Contains("scroll", merged.Attributes);
    }

    [Fact]
    public async Task SaveSegments_DifferentSource_DoesNotContinue()
    {
        using var db = CreateDbContext();
        var svc = new UsageService(db);

        // 同 IdentityKey、时间相连,但 Source 不同 → 不续接(续接隔离,ADR-017 §3a)
        var t0 = Now.AddMinutes(-10);
        ActivitySegmentItem Seg(string source, DateTimeOffset start, DateTimeOffset end) => new()
        {
            Id = Guid.CreateVersion7(),
            Source = source,
            IdentityKey = "same-key",
            StartTime = start,
            EndTime = end
        };

        await svc.SaveSegmentsAsync(_deviceId, [Seg("browser", t0, t0.AddMinutes(2))]);
        await svc.SaveSegmentsAsync(_deviceId, [Seg("vscode", t0.AddMinutes(2), t0.AddMinutes(4))]);

        Assert.Equal(2, db.ActivitySegments.Count());
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
            DurationSeconds = 120,
            Attributes = """{"url":"https://example.com"}"""
        });
        db.ActivitySegments.Add(new ActivitySegment
        {
            Id = Guid.CreateVersion7(),
            DeviceId = _deviceId,
            Source = "vscode",
            IdentityKey = "d:/repo/file.cs",
            StartTime = t0,
            EndTime = t0.AddMinutes(1),
            DurationSeconds = 60
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
    public async Task SaveUsage_CalculatesDurationSeconds()
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

        var segment = db.ActivitySegments.Single();
        Assert.Equal((int)(end - start).TotalSeconds, segment.DurationSeconds);
    }
}
