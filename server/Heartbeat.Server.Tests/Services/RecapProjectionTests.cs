using Heartbeat.Core;
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
    public void KnownStrands_PresentHandles_AppendedAsBlock()
    {
        var known = new Dictionary<HandleRef, StrandGloss>
        {
            [new(ActivitySources.System, "code.exe")] = new("HyperFrames", "我在搞的 AI 动效框架"),
            [new("browser", "huasheng.com")] = new("花生", "敏毕设"),
            [new(ActivitySources.System, "never.exe")] = new("缺席项目", "今天没出现"),
        };

        var result = RecapProjection.Project(
            [
                new("Main PC", ActivitySources.System, "code.exe|x", "code.exe", "x", Day.AddHours(9), Day.AddHours(11)),
                Browser("https://huasheng.com/dashboard", "花生看板", Day.AddHours(9), Day.AddHours(10)),
            ],
            Window, TimeSpan.Zero, known);

        Assert.Contains("已知脉络", result.Digest);
        Assert.Contains("HyperFrames：我在搞的 AI 动效框架", result.Digest);
        Assert.Contains("花生：敏毕设", result.Digest);
        Assert.DoesNotContain("缺席项目", result.Digest); // 今天没出现的 Strand 不进块
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
}
