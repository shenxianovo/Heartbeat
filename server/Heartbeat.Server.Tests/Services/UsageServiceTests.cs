using Heartbeat.Core;
using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Heartbeat.Server.Services;
using Heartbeat.Server.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
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

    /// <summary>system 段上传项（ADR-020）：IdentityKey 由采集端计算。</summary>
    private static ActivitySegmentItem SystemItem(string app, DateTimeOffset start, DateTimeOffset end, string? title = null) => new()
    {
        Id = Guid.CreateVersion7(),
        Source = ActivitySources.System,
        IdentityKey = SystemIdentity.Key(AppIdentityKeys.FromLegacyWindowsAppName(app), title),
        AppIdentityKey = AppIdentityKeys.FromLegacyWindowsAppName(app),
        AppDisplayName = app,
        Title = title,
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
    public async Task SaveSegments_AllInvalid_SilentlyDropped()
    {
        using var db = CreateDbContext();
        var svc = new UsageService(db);

        // 校验丢弃不是错误（阈值细则见 SegmentValidationPolicyTests）——
        // 全无效批静默丢弃、不抛异常，钉住这条最意外的契约
        await svc.SaveSegmentsAsync(_deviceId,
        [
            SystemItem("App", default, Now),                              // default start
            SystemItem("App", Now.AddMinutes(-2), Now.AddMinutes(-5)),    // end < start
            SystemItem("App", Now.AddMinutes(20), Now.AddMinutes(30))     // future beyond skew
        ]);

        Assert.Empty(db.ActivitySegments);
    }

    [Fact]
    public async Task SaveSegments_SystemWithoutAppIdentity_ThrowsBeforeCreatingFacts()
    {
        using var db = CreateDbContext();
        var svc = new UsageService(db);
        var item = SystemItem("VSCode", Now.AddMinutes(-2), Now.AddMinutes(-1));
        item.AppIdentityKey = null;

        var exception = await Assert.ThrowsAsync<SegmentIngestContractException>(
            () => svc.SaveSegmentsAsync(_deviceId, [item]));

        Assert.Equal(SegmentIngestContractViolation.MissingSystemAppIdentity, exception.Violation);
        Assert.Empty(db.ActivitySegments);
        Assert.Empty(db.Apps);
        Assert.Empty(db.AppIdentities);
    }

    [Fact]
    public async Task SaveSegments_MalformedAppIdentity_ThrowsBeforeCreatingFacts()
    {
        using var db = CreateDbContext();
        var svc = new UsageService(db);
        var item = SystemItem("VSCode", Now.AddMinutes(-2), Now.AddMinutes(-1));
        item.AppIdentityKey = "sys:";

        var exception = await Assert.ThrowsAsync<SegmentIngestContractException>(
            () => svc.SaveSegmentsAsync(_deviceId, [item]));

        Assert.Equal(SegmentIngestContractViolation.MalformedAppIdentity, exception.Violation);
        Assert.Empty(db.ActivitySegments);
        Assert.Empty(db.Apps);
        Assert.Empty(db.AppIdentities);
    }

    [Fact]
    public async Task SaveSegments_LegacyAppName_ThrowsBeforeCreatingFacts()
    {
        using var db = CreateDbContext();
        var svc = new UsageService(db);
        var item = SystemItem("VSCode", Now.AddMinutes(-2), Now.AddMinutes(-1));
        item.AppName = "";

        var exception = await Assert.ThrowsAsync<SegmentIngestContractException>(
            () => svc.SaveSegmentsAsync(_deviceId, [item]));

        Assert.Equal(SegmentIngestContractViolation.LegacyAppName, exception.Violation);
        Assert.Empty(db.ActivitySegments);
        Assert.Empty(db.Apps);
        Assert.Empty(db.AppIdentities);
    }

    [Fact]
    public async Task SaveSegments_OutOfOrderOldSnapshot_DoesNotShrinkRow()
    {
        using var db = CreateDbContext();
        var svc = new UsageService(db);

        // 离线缓存迟到重传：旧快照晚于新快照到达，行不得回退（摄入可交换，ADR-018）
        var id = Guid.CreateVersion7();
        var t0 = Now.AddMinutes(-10);

        var newer = SystemItem("VSCode", t0, t0.AddMinutes(5));
        newer.Id = id;
        await svc.SaveSegmentsAsync(_deviceId, [newer]);

        var older = SystemItem("VSCode", t0, t0.AddMinutes(1));
        older.Id = id;
        await svc.SaveSegmentsAsync(_deviceId, [older]);

        var row = db.ActivitySegments.Single();
        Assert.Equal(t0.AddMinutes(5), row.EndTime);
    }

    [Fact]
    public async Task SaveSegments_DistinctIds_AdjacentSameActivity_StayTwoRows()
    {
        using var db = CreateDbContext();
        var svc = new UsageService(db);

        // ADR-018 行为变化：不同 Id 即不同活动，同 App+Title 首尾相连也不再启发式粘合
        var t0 = Now.AddMinutes(-10);
        await svc.SaveSegmentsAsync(_deviceId,
        [
            SystemItem("VSCode", t0, t0.AddMinutes(3)),
            SystemItem("VSCode", t0.AddMinutes(3), t0.AddMinutes(5))
        ]);

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
            AppIdentityKey = "win:msedge",
            AppDisplayName = "Microsoft Edge",
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
        Assert.NotNull(seg.AppIdentityId);
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
    public async Task SaveSegments_SystemSource_EntersStatsPath()
    {
        using var db = CreateDbContext();
        var svc = new UsageService(db);

        // ADR-020：system 段经 /segments 摄入路径（客户端已算好 IdentityKey）——
        // App 关联、快照生长、报表统计与旧 /usage 路径完全一致。
        // 校验策略拒收 >24h 前的段，须用相对当前的时间；再避开 UTC 午夜跨日。
        var start = Now.AddMinutes(-90);
        if (start.UtcDateTime.Date != start.AddMinutes(30).UtcDateTime.Date)
            start = start.AddHours(-1);

        var id = Guid.CreateVersion7();
        ActivitySegmentItem Snapshot(DateTimeOffset end) => new()
        {
            Id = id,
            Source = ActivitySources.System,
            IdentityKey = SystemIdentity.Key("win:vscode", "main.cs"),
            AppIdentityKey = "win:vscode",
            AppDisplayName = "VSCode",
            Title = "main.cs",
            StartTime = start,
            EndTime = end
        };

        await svc.SaveSegmentsAsync(_deviceId, [Snapshot(start.AddMinutes(10))]);
        await svc.SaveSegmentsAsync(_deviceId, [Snapshot(start.AddMinutes(30))]);

        var row = db.ActivitySegments.Single();
        Assert.Equal(ActivitySources.System, row.Source);
        Assert.NotNull(row.AppId); // AppIdentityKey 建立了 App 关联
        Assert.NotNull(row.AppIdentityId); // expand 阶段同时保存平台观测身份
        Assert.Equal(start.AddMinutes(30), row.EndTime); // 同 Id 快照生长

        var report = await new ReportService(db).GetDailyReportAsync("user-1", null, start);
        var item = Assert.Single(report.Apps);
        Assert.Equal("VSCode", item.AppName);
        Assert.Equal(1800, item.DurationSeconds); // 统计路径可见
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
    public async Task SaveSegments_ReuploadSameBatch_IsIdempotent()
    {
        using var db = CreateDbContext();
        var svc = new UsageService(db);

        // 两条不同活动的段，整批重传（离线缓存重试场景）
        List<ActivitySegmentItem> batch =
        [
            SystemItem("VSCode", Now.AddMinutes(-10), Now.AddMinutes(-8)),
            SystemItem("msedge", Now.AddMinutes(-7), Now.AddMinutes(-5))
        ];

        await svc.SaveSegmentsAsync(_deviceId, batch);
        await svc.SaveSegmentsAsync(_deviceId, batch);

        Assert.Equal(2, db.ActivitySegments.Count());
    }

    [Fact]
    public async Task SaveSegments_CreatesApp_WhenNotExists()
    {
        using var db = CreateDbContext();
        var svc = new UsageService(db);

        await svc.SaveSegmentsAsync(_deviceId,
        [
            SystemItem("NewApp1", Now.AddMinutes(-5), Now.AddMinutes(-3)),
            SystemItem("NewApp2", Now.AddMinutes(-3), Now.AddMinutes(-1))
        ]);

        Assert.Equal(2, db.Apps.Count());
        Assert.Equal(2, db.ActivitySegments.Count());
    }

    [Fact]
    public async Task SaveSegments_ReusesExistingApp()
    {
        using var db = CreateDbContext();
        var svc = new UsageService(db);

        var app = new App { Name = "VSCode" };
        db.AppIdentities.Add(new AppIdentity { Key = "win:vscode", App = app });
        await db.SaveChangesAsync();

        await svc.SaveSegmentsAsync(_deviceId, [SystemItem("VSCode", Now.AddMinutes(-5), Now.AddMinutes(-2))]);

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
        await svc.SaveSegmentsAsync(_deviceId, [SystemItem("VSCode", start, end)]);

        // 时长是派生量（ADR-018）：不落盘，查询投影现算
        var usage = Assert.Single(await svc.GetUsageAsync("user-1", null, null, null));
        Assert.Equal((int)(end - start).TotalSeconds, usage.DurationSeconds);
    }

    [Fact]
    public async Task SaveSegments_UnknownSimilarIdentities_CreateDistinctProvisionalApps()
    {
        using var db = CreateDbContext();
        var svc = new UsageService(db);
        var start = Now.AddMinutes(-10);

        ActivitySegmentItem Item(string identity, DateTimeOffset offset) => new()
        {
            Id = Guid.CreateVersion7(),
            Source = ActivitySources.System,
            IdentityKey = identity + "\n",
            AppIdentityKey = identity,
            AppDisplayName = "Visual Studio Code",
            StartTime = offset,
            EndTime = offset.AddMinutes(1)
        };

        await svc.SaveSegmentsAsync(_deviceId,
        [
            Item("win:vscode", start),
            Item("mac:com.microsoft.vscode", start.AddMinutes(2))
        ]);

        var identities = await db.AppIdentities.Include(x => x.App).OrderBy(x => x.Key).ToListAsync();
        Assert.Equal(2, identities.Count);
        Assert.Equal(2, identities.Select(x => x.AppId).Distinct().Count());
        Assert.All(identities, x => Assert.True(x.App.IsProvisional));
        Assert.Contains(identities, x => x.App.Key == "vscode");
        Assert.Contains(identities, x => x.App.Key == "microsoft.vscode");
    }

    [Fact]
    public async Task ExplicitCrossPlatformMapping_AggregatesProductAndPreservesRawIdentity()
    {
        using var db = CreateDbContext();
        var product = new App { Key = "vscode", DisplayName = "Visual Studio Code" };
        var windows = new AppIdentity { Key = "win:code", App = product };
        var mac = new AppIdentity { Key = "mac:com.microsoft.vscode", App = product };
        db.AppIdentities.AddRange(windows, mac);
        var secondDevice = new Device
        {
            OwnerId = "user-1",
            HardwareId = "hw-2",
            DeviceName = "Test Mac"
        };
        var otherOwnerDevice = new Device
        {
            OwnerId = "user-2",
            HardwareId = "hw-other",
            DeviceName = "Other PC"
        };
        db.Devices.AddRange(secondDevice, otherOwnerDevice);
        await db.SaveChangesAsync();

        var svc = new UsageService(db);
        var start = Now.AddMinutes(-20);
        ActivitySegmentItem Item(string identity, DateTimeOffset from, int minutes) => new()
        {
            Id = Guid.CreateVersion7(),
            Source = ActivitySources.System,
            IdentityKey = identity + "\nmain.cs",
            AppIdentityKey = identity,
            Title = "main.cs",
            StartTime = from,
            EndTime = from.AddMinutes(minutes)
        };

        await svc.SaveSegmentsAsync(_deviceId, [Item("win:code", start, 3)]);
        await svc.SaveSegmentsAsync(secondDevice.Id, [Item("mac:com.microsoft.vscode", start.AddMinutes(5), 4)]);
        await svc.SaveSegmentsAsync(otherOwnerDevice.Id, [Item("win:code", start.AddMinutes(10), 9)]);

        var report = await new ReportService(db).GetDailyReportAsync("user-1", null, start);
        var app = Assert.Single(report.Apps);
        Assert.Equal(product.Id, app.AppId);
        Assert.Equal("Visual Studio Code", app.AppName);
        Assert.Equal(7 * 60, app.DurationSeconds);

        var windowsOnly = Assert.Single((await new ReportService(db)
            .GetDailyReportAsync("user-1", _deviceId, start)).Apps);
        Assert.Equal(3 * 60, windowsOnly.DurationSeconds);

        var otherOwner = Assert.Single((await new ReportService(db)
            .GetDailyReportAsync("user-2", null, start)).Apps);
        Assert.Equal(9 * 60, otherOwner.DurationSeconds);

        var facts = await svc.GetUsageAsync("user-1", null, null, null);
        Assert.Equal(2, facts.Count);
        Assert.Equal(
            ["mac:com.microsoft.vscode", "win:code"],
            facts.Select(x => x.AppIdentityKey!).Order().ToArray());
        Assert.All(facts, x => Assert.Equal(product.Id, x.AppId));
    }

    [Fact]
    public async Task CrossPlatformProduct_BrowserEvidenceSharesAppDetailDimensionWithSystemFacts()
    {
        using var db = CreateDbContext();
        var product = new App { Key = "edge", DisplayName = "Microsoft Edge" };
        db.AppIdentities.AddRange(
            new AppIdentity { Key = "win:msedge", App = product },
            new AppIdentity { Key = "mac:com.microsoft.edgemac", App = product });
        var macDevice = new Device
        {
            OwnerId = "user-1",
            HardwareId = "hw-mac",
            DeviceName = "Test Mac"
        };
        db.Devices.Add(macDevice);
        await db.SaveChangesAsync();

        var svc = new UsageService(db);
        var start = Now.AddMinutes(-20);

        ActivitySegmentItem System(string identity, DateTimeOffset from) => new()
        {
            Id = Guid.CreateVersion7(),
            Source = ActivitySources.System,
            IdentityKey = SystemIdentity.Key(identity, "Heartbeat"),
            AppIdentityKey = identity,
            AppDisplayName = "Microsoft Edge",
            Title = "Heartbeat",
            StartTime = from,
            EndTime = from.AddMinutes(5)
        };

        ActivitySegmentItem Browser(string identity, string url, DateTimeOffset from) => new()
        {
            Id = Guid.CreateVersion7(),
            Source = "browser",
            IdentityKey = url,
            AppIdentityKey = identity,
            AppDisplayName = "Microsoft Edge",
            Title = "Heartbeat repository",
            StartTime = from.AddMinutes(1),
            EndTime = from.AddMinutes(4),
            Attributes = JsonSerializer.Deserialize<JsonElement>($$"""{"url":"{{url}}"}""")
        };

        await svc.SaveSegmentsAsync(_deviceId,
        [
            System("win:msedge", start),
            Browser("win:msedge", "https://github.com/example/heartbeat", start)
        ]);
        await svc.SaveSegmentsAsync(macDevice.Id,
        [
            System("mac:com.microsoft.edgemac", start.AddMinutes(10)),
            Browser("mac:com.microsoft.edgemac", "https://github.com/example/heartbeat/pulls", start.AddMinutes(10))
        ]);

        var systemFacts = await svc.GetUsageAsync("user-1", null, start, start.AddMinutes(20));
        Assert.Equal(2, systemFacts.Count);
        Assert.All(systemFacts, x => Assert.Equal(product.Id, x.AppId));

        var appDetailEvidence = await svc.GetSegmentsAsync(
            "user-1", null, "browser", product.Id, start, start.AddMinutes(20));
        Assert.Equal(2, appDetailEvidence.Count);
        Assert.All(appDetailEvidence, x =>
        {
            Assert.Equal(product.Id, x.AppId);
            Assert.Equal("edge", x.AppKey);
            Assert.Equal("Microsoft Edge", x.AppDisplayName);

            var system = Assert.Single(systemFacts, fact => fact.DeviceId == x.DeviceId);
            Assert.Equal(system.AppId, x.AppId);
            Assert.True(x.StartTime < system.EndTime && x.EndTime > system.StartTime);
        });
        Assert.Equal(
            ["mac:com.microsoft.edgemac", "win:msedge"],
            appDetailEvidence.Select(x => x.AppIdentityKey!).Order().ToArray());

        var windowsDetail = Assert.Single(await svc.GetSegmentsAsync(
            "user-1", _deviceId, "browser", product.Id, start, start.AddMinutes(20)));
        Assert.Equal("win:msedge", windowsDetail.AppIdentityKey);

        var macDetail = Assert.Single(await svc.GetSegmentsAsync(
            "user-1", macDevice.Id, "browser", product.Id, start, start.AddMinutes(20)));
        Assert.Equal("mac:com.microsoft.edgemac", macDetail.AppIdentityKey);
    }

    [Fact]
    public async Task SnapshotIdentityGuard_DoesNotReplaceOriginalAppIdentity()
    {
        using var db = CreateDbContext();
        var svc = new UsageService(db);
        var id = Guid.CreateVersion7();
        var start = Now.AddMinutes(-10);
        ActivitySegmentItem Item(string identity, DateTimeOffset end) => new()
        {
            Id = id,
            Source = "browser",
            IdentityKey = "https://example.com",
            AppIdentityKey = identity,
            StartTime = start,
            EndTime = end
        };

        await svc.SaveSegmentsAsync(_deviceId, [Item("win:code", start.AddMinutes(1))]);
        await svc.SaveSegmentsAsync(_deviceId, [Item("mac:com.microsoft.vscode", start.AddMinutes(2))]);

        var row = await db.ActivitySegments.Include(x => x.AppIdentity).SingleAsync();
        Assert.Equal("win:code", row.AppIdentity!.Key);
        Assert.Equal(start.AddMinutes(2), row.EndTime);
        Assert.Single(db.AppIdentities);
    }
}
