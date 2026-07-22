using Heartbeat.Core;
using Heartbeat.Core.DTOs.Knowledge;
using Heartbeat.Server.Services;

namespace Heartbeat.Server.Tests.Services;

public class RecapProjectionTests
{
    private static readonly DateTimeOffset Day = new(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateRange Window = DateRange.Day(Day);

    private static RecapSegmentInput Sys(string app, string? title, DateTimeOffset start, DateTimeOffset end, string device = "Main PC")
        => new(device, ActivitySources.System, $"{app}|{title}", app, title, start, end);

    private static RecapSegmentInput Browser(string url, string? title, DateTimeOffset start, DateTimeOffset end, string device = "Main PC")
        => new(device, "browser", url, "chrome", title, start, end);

    private static RecapProjectionResult Project(params RecapSegmentInput[] segments)
        => RecapProjection.Project(segments, Window, TimeSpan.Zero);

    private static MatcherDto AppMatcher(string app) => new()
    {
        Source = ActivitySources.System,
        Steps = [new() { Reading = "app", Op = MatcherOps.Equal, Value = app }]
    };

    private static MatcherDto UrlContains(string fragment) => new()
    {
        Source = ActivitySources.Browser,
        Steps = [new() { Reading = "url", Op = MatcherOps.Contains, Value = fragment }]
    };

    private static MatcherDto PathMatcher(string app, string titleFragment) => new()
    {
        Source = ActivitySources.System,
        Steps =
        [
            new() { Reading = "app", Op = MatcherOps.Equal, Value = app },
            new() { Reading = "title", Op = MatcherOps.Contains, Value = titleFragment },
        ]
    };

    [Fact]
    public void EmptyDay_IsEmpty_WatermarkAtWindowStart()
    {
        var result = Project();

        Assert.True(result.IsEmpty);
        Assert.Equal(Window.UtcStart, result.SegmentWatermarkUtc);
    }

    [Fact]
    public void SegmentsOutsideWindow_TreatedAsEmpty()
    {
        var result = Project(Sys("vscode", null, Day.AddHours(-2), Day));

        Assert.True(result.IsEmpty);
    }

    [Fact]
    public void AdjacentSameAppSegments_MergeIntoOneAttentionBlock()
    {
        // 两段之间 60s 缝隙（快照节律），应折叠为一个 09:00–10:01 的块
        var result = Project(
            Sys("vscode", "Heartbeat — a.cs", Day.AddHours(9), Day.AddHours(9).AddMinutes(30)),
            Sys("vscode", "Heartbeat — b.cs", Day.AddHours(9).AddMinutes(31), Day.AddHours(10).AddMinutes(1)));

        Assert.Contains("09:00–10:01 vscode（1小时01分）", result.Digest);
        Assert.DoesNotContain("09:31", result.Digest);
    }

    [Fact]
    public void DifferentApps_NotMerged()
    {
        var result = Project(
            Sys("vscode", null, Day.AddHours(9), Day.AddHours(10)),
            Sys("chrome", null, Day.AddHours(10), Day.AddHours(11)));

        Assert.Contains("09:00–10:00 vscode", result.Digest);
        Assert.Contains("10:00–11:00 chrome", result.Digest);
    }

    [Fact]
    public void KnownStrands_MatcherHitsToday_AppendedAsBlock()
    {
        var known = new List<KnownStrandInput>
        {
            new("HyperFrames", "我在搞的 AI 动效框架", [AppMatcher("code.exe")]),
            new("花生", "B 站实习部门的产品", [UrlContains("huasheng.com")]),
            new("缺席项目", "今天没出现", [AppMatcher("never.exe")]),
        };

        var result = RecapProjection.Project(
            [
                new("Main PC", ActivitySources.System, "code.exe|x", "code.exe", "x", Day.AddHours(9), Day.AddHours(11)),
                Browser("https://huasheng.com/dashboard", "花生看板", Day.AddHours(9), Day.AddHours(10)),
            ],
            Window, TimeSpan.Zero, known);

        Assert.Contains("已知脉络", result.Digest);
        Assert.Contains("HyperFrames：我在搞的 AI 动效框架", result.Digest);
        Assert.Contains("花生：B 站实习部门的产品", result.Digest);
        Assert.DoesNotContain("缺席项目", result.Digest); // 指纹今天没命中的 Strand 不进块
    }

    [Fact]
    public void KnownStrands_PathPredicate_L2MustAlsoMatch()
    {
        var known = new List<KnownStrandInput>
        {
            new("HyperFrames", "动效预研", [PathMatcher("Code", "hyperframes")]),
            new("别的项目", "不该出现", [PathMatcher("Code", "unrelated")]),
        };

        var result = RecapProjection.Project(
            [Sys("Code", "hyperframes-workspace — a.ts", Day.AddHours(9), Day.AddHours(10))],
            Window, TimeSpan.Zero, known);

        Assert.Contains("HyperFrames：动效预研", result.Digest);
        Assert.DoesNotContain("别的项目", result.Digest); // L1 命中但 L2 不中 → 不注入
    }

    [Fact]
    public void KnownStrands_Null_NoBlock()
    {
        var result = Project(Sys("vscode", null, Day.AddHours(9), Day.AddHours(10)));

        Assert.DoesNotContain("已知脉络", result.Digest);
    }

    [Fact]
    public void ShortBlock_DroppedFromTimeline_ButCountedInAppTotals()
    {
        var result = Project(
            Sys("vscode", null, Day.AddHours(9), Day.AddHours(10)),
            Sys("notepad", null, Day.AddHours(10), Day.AddHours(10).AddSeconds(30)));

        Assert.DoesNotContain("notepad（", result.Digest); // 时间轴无 notepad 行
        Assert.Contains("notepad <1分", result.Digest); // 应用时长如实累计
    }

    [Fact]
    public void MidnightCrossingSegment_ClippedToWindow_WatermarkClipped()
    {
        var result = Project(Sys("vscode", null, Day.AddHours(23), Day.AddHours(25)));

        Assert.Contains("23:00–24:00 vscode（1小时00分）", result.Digest);
        Assert.Equal(Window.UtcEnd, result.SegmentWatermarkUtc);
    }

    [Fact]
    public void AwaySegments_RenderedAsLeave_ExcludedFromAppRanking()
    {
        var result = Project(
            Sys("vscode", null, Day.AddHours(9), Day.AddHours(10)),
            Sys(SyntheticApps.Away, null, Day.AddHours(12), Day.AddHours(13)));

        Assert.Contains("12:00–13:00 离开（1小时00分）", result.Digest);
        Assert.Contains("离开合计：1小时00分", result.Digest);
        Assert.DoesNotContain("__away__", result.Digest);
    }

    [Fact]
    public void PluginSegments_SameIdentityKey_AggregatedWithVisitCount()
    {
        var url = "learn.microsoft.com/ef-core/migrations";
        var result = Project(
            Sys("chrome", null, Day.AddHours(9), Day.AddHours(10)),
            Browser(url, "EF Core 迁移", Day.AddHours(9), Day.AddHours(9).AddMinutes(20)),
            Browser(url, "EF Core 迁移", Day.AddHours(9).AddMinutes(40), Day.AddHours(9).AddMinutes(50)));

        Assert.Contains($"EF Core 迁移（{url}） — 合计 30分，2 次", result.Digest);
    }

    [Fact]
    public void PluginEntries_CappedAtTopN_OmissionNoted()
    {
        var segments = new List<RecapSegmentInput> { Sys("chrome", null, Day.AddHours(9), Day.AddHours(10)) };
        for (var i = 0; i < 32; i++)
            segments.Add(Browser($"example.com/page-{i}", null,
                Day.AddHours(9), Day.AddHours(9).AddMinutes(32 - i)));

        var result = RecapProjection.Project(segments, Window, TimeSpan.Zero);

        Assert.Contains("example.com/page-0", result.Digest); // 时长最长者保留
        Assert.DoesNotContain("example.com/page-31", result.Digest);
        Assert.Contains("另有 2 条较短的记录未列出", result.Digest);
    }

    [Fact]
    public void MultiDevice_SeparateSections_NoCrossDeviceMixing()
    {
        var result = Project(
            Sys("vscode", null, Day.AddHours(9), Day.AddHours(10), device: "Desktop"),
            Sys("chrome", null, Day.AddHours(9).AddMinutes(30), Day.AddHours(10), device: "Laptop"));

        Assert.Contains("## 设备「Desktop」", result.Digest);
        Assert.Contains("## 设备「Laptop」", result.Digest);
        Assert.Contains("设备：Desktop、Laptop", result.Digest);
    }

    [Fact]
    public void DisplayOffset_RendersLocalWallClock()
    {
        // UTC 01:00 在 UTC+8 显示为 09:00；日窗口本身由带时区的 date 参数切出
        var dayUtc8 = new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.FromHours(8));
        var window = DateRange.Day(dayUtc8);
        var seg = Sys("vscode", null, dayUtc8.AddHours(9), dayUtc8.AddHours(10));

        var result = RecapProjection.Project([seg], window, TimeSpan.FromHours(8));

        Assert.Contains("09:00–10:00 vscode", result.Digest);
        Assert.Contains("UTC+08:00", result.Digest);
    }

    [Fact]
    public void ZeroLengthPointEvent_InsideWindow_Kept()
    {
        var result = Project(
            Sys("chrome", null, Day.AddHours(9), Day.AddHours(10)),
            Browser("example.com/ping", "Ping", Day.AddHours(9), Day.AddHours(9)));

        Assert.False(result.IsEmpty);
        Assert.Contains("Ping（example.com/ping） — 合计 <1分，1 次", result.Digest);
    }

    // ---- 深度树分解（ADR-029 §2）----

    [Fact]
    public void Breakdown_ExpandedBlock_DistinctTitlesWithUnionDurations()
    {
        // 同一标题两段累计、不同标题分列；按时长降序
        var result = Project(
            Sys("Code", "hyperframes-workspace", Day.AddHours(9), Day.AddHours(9).AddMinutes(30)),
            Sys("Code", "heartbeat", Day.AddHours(9).AddMinutes(30), Day.AddHours(9).AddMinutes(45)),
            Sys("Code", "hyperframes-workspace", Day.AddHours(9).AddMinutes(45), Day.AddHours(10)));

        Assert.Contains("｜其中: hyperframes-workspace 45分 · heartbeat 15分", result.Digest);
    }

    [Fact]
    public void Breakdown_ExpandedBlock_CappedWithTailFold()
    {
        // 6 个不同标题连续切换，展开封顶 4 条，尾部折叠"其他 2 个"并合计时长
        var segments = new List<RecapSegmentInput>();
        var cursor = Day.AddHours(9);
        for (var i = 0; i < 6; i++)
        {
            var end = cursor.AddMinutes(10 - i);
            segments.Add(Sys("Code", $"file-{i}", cursor, end));
            cursor = end;
        }

        var result = RecapProjection.Project(segments, Window, TimeSpan.Zero);

        Assert.Contains("file-0 10分", result.Digest);
        Assert.Contains("file-3 7分", result.Digest);
        Assert.DoesNotContain("file-4 6分", result.Digest);
        Assert.Contains("其他 2 个 11分", result.Digest); // file-4 6分 + file-5 5分
    }

    [Fact]
    public void Breakdown_ShortBlock_OnlyTopReading_NoTailDurations()
    {
        // 块 5 分钟 < 展开门槛：只给头名读数，其余折叠
        var result = Project(
            Sys("vscode", null, Day.AddHours(8), Day.AddHours(9)), // 占位长块避免整日过空
            Sys("notepad", "notes-a", Day.AddHours(10), Day.AddHours(10).AddMinutes(3)),
            Sys("notepad", "notes-b", Day.AddHours(10).AddMinutes(3), Day.AddHours(10).AddMinutes(5)));

        Assert.Contains("notepad（5分）｜其中: notes-a 3分 · 其他 1 个 2分", result.Digest);
    }

    [Fact]
    public void Breakdown_AwayBlock_NoBreakdown()
    {
        var result = Project(
            Sys(SyntheticApps.Away, "ignored", Day.AddHours(12), Day.AddHours(13)));

        Assert.Contains("12:00–13:00 离开（1小时00分）", result.Digest);
        Assert.DoesNotContain("｜其中", result.Digest);
    }

    [Fact]
    public void RecurringReadings_RenderedAsAnnotation()
    {
        var result = RecapProjection.Project(
            [Sys("vscode", null, Day.AddHours(9), Day.AddHours(10))],
            Window, TimeSpan.Zero, knownStrands: null, recurringReadings: ["WeChat", "qq.com"]);

        Assert.Contains("近 14 天高频出现", result.Digest);
        Assert.Contains("WeChat、qq.com", result.Digest);
    }

    [Fact]
    public void RecurringReadings_EmptyOrNull_NoAnnotation()
    {
        var result = Project(Sys("vscode", null, Day.AddHours(9), Day.AddHours(10)));

        Assert.DoesNotContain("近 14 天高频出现", result.Digest);
    }
}
