using Heartbeat.Core;
using Heartbeat.Core.DTOs.Collectors;
using Heartbeat.Core.DTOs.Knowledge;
using Heartbeat.Server.Services;

namespace Heartbeat.Server.Tests.Services;

public class RecapProjectionTests
{
    private static readonly DateTimeOffset Day = new(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateRange Window = new(
        new DateTime(2026, 7, 12, 0, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 7, 13, 0, 0, 0, DateTimeKind.Utc));

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

    private static StrandKnowledgeInput Strand(string name, string gloss, params MatcherDto[] matchers)
        => new(Guid.CreateVersion7(), null, name, gloss, null, null, matchers);

    [Fact]
    public void KnownStrands_MatcherHitsToday_AppendedAsBlock()
    {
        var known = new List<StrandKnowledgeInput>
        {
            Strand("HyperFrames", "我在搞的 AI 动效框架", AppMatcher("code.exe")),
            Strand("花生", "B 站实习部门的产品", UrlContains("huasheng.com")),
            Strand("缺席项目", "今天没出现", AppMatcher("never.exe")),
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
        var known = new List<StrandKnowledgeInput>
        {
            Strand("HyperFrames", "动效预研", PathMatcher("Code", "hyperframes")),
            Strand("别的项目", "不该出现", PathMatcher("Code", "unrelated")),
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

    // ---- 日期知识注入（ADR-031 §7）----

    [Fact]
    public void LeafHit_RendersRootToLeafPath()
    {
        var (root, leaf) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var strands = new List<StrandKnowledgeInput>
        {
            new(root, null, "哔哩哔哩实习", "2026 暑期实习", null, null, []),
            new(leaf, root, "Hyperframes", "产品调研", null, null, [AppMatcher("code.exe")]),
        };

        var result = RecapProjection.Project(
            [new("Main PC", ActivitySources.System, "code.exe|x", "code.exe", "x", Day.AddHours(9), Day.AddHours(10))],
            Window, TimeSpan.Zero, strands);

        Assert.Contains("哔哩哔哩实习 → Hyperframes：产品调研", result.Digest); // 叶带全祖先链
        Assert.Contains("- 哔哩哔哩实习：2026 暑期实习", result.Digest); // 祖先自身成行
        Assert.NotNull(result.KnowledgeHash);
    }

    [Fact]
    public void ExpiredStrand_NotInjected_EvenIfMatcherHits()
    {
        var strands = new List<StrandKnowledgeInput>
        {
            new(Guid.CreateVersion7(), null, "已结束的项目", "", null,
                DateOnly.FromDateTime(Day.Date).AddDays(-1), [AppMatcher("code.exe")]),
        };

        var result = RecapProjection.Project(
            [new("Main PC", ActivitySources.System, "code.exe|x", "code.exe", "x", Day.AddHours(9), Day.AddHours(10))],
            Window, TimeSpan.Zero, strands);

        Assert.DoesNotContain("已结束的项目", result.Digest);
    }

    [Fact]
    public void DayEpisodes_RenderedAsFacts_WithTimeAndContext()
    {
        var strand = Guid.CreateVersion7();
        var strands = new List<StrandKnowledgeInput>
        {
            new(strand, null, "Hyperframes", "", null, null, []),
        };
        var episodes = new List<EpisodeKnowledgeInput>
        {
            new(Guid.CreateVersion7(), DateOnly.FromDateTime(Day.Date), "和 mentor 对齐方案",
                Day.AddHours(14), Day.AddHours(15), strand),
            new(Guid.CreateVersion7(), DateOnly.FromDateTime(Day.Date), "下午去了趟牙医", null, null, null),
            new(Guid.CreateVersion7(), DateOnly.FromDateTime(Day.Date).AddDays(-1), "昨天的事", null, null, null),
        };

        var result = RecapProjection.Project(
            [Sys("vscode", null, Day.AddHours(9), Day.AddHours(10))],
            Window, TimeSpan.Zero, strands, episodes);

        Assert.Contains("当天事实", result.Digest);
        Assert.Contains("14:00–15:00左右 和 mentor 对齐方案（属于：Hyperframes）", result.Digest);
        Assert.Contains("- 下午去了趟牙医", result.Digest); // 独立 Episode 也作当天事实
        Assert.DoesNotContain("昨天的事", result.Digest); // 非当日 Episode 不注入
        Assert.Contains("- Hyperframes", result.Digest); // Episode 关联带入 Strand 语境
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

        // 声明驱动两层树(ADR-030 §7):url 节点聚合,tab_title 是其下一深度分解
        Assert.Contains($"{url} — 合计 30分，2 次｜其中: EF Core 迁移 30分", result.Digest);
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
        // UTC 01:00 在 UTC+8 显示为 09:00；投影只消费调用方给出的通用 instant window。
        var dayUtc8 = new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.FromHours(8));
        var window = new DateRange(
            new DateTime(2026, 7, 11, 16, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 7, 12, 16, 0, 0, DateTimeKind.Utc));
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
        Assert.Contains("example.com/ping — 合计 <1分，1 次｜其中: Ping <1分", result.Digest);
    }

    // ---- 声明驱动深度树（ADR-030 §7）----

    [Fact]
    public void PluginTrack_ThreeLayerDeclaration_RecursiveBreakdown_ServerCodeUntouched()
    {
        // browser v2 形状(site → url → tab_title):投影代码零改动,只换声明——ADR-030 的核心承诺。
        var v2 = new CollectorDeclarationDto
        {
            Source = ActivitySources.Browser,
            Version = 2,
            Layers =
            [
                new() { Readings = [new() { Name = "site", From = "attributes.site" }] },
                new() { Readings = [new() { Name = "url", From = DepthSlots.IdentityKey }] },
                new() { Readings = [new() { Name = "tab_title", From = DepthSlots.Title }] },
            ]
        };
        var tables = new DepthTables(SeedDeclarations.All.Append(v2));

        var segments = new List<RecapSegmentInput>
        {
            Sys("chrome", null, Day.AddHours(9), Day.AddHours(11)),
            new("Main PC", ActivitySources.Browser, "blog.shenxianovo.com/post-1", "chrome", "文章一",
                Day.AddHours(9), Day.AddHours(9).AddMinutes(40), """{"site":"shenxianovo.com"}"""),
            new("Main PC", ActivitySources.Browser, "heartbeat.shenxianovo.com/dashboard", "chrome", "看板",
                Day.AddHours(10), Day.AddHours(10).AddMinutes(30), """{"site":"shenxianovo.com"}"""),
            // 老段:无 attributes.site → 挂最深可用读数(url 直接成顶层节点)
            new("Main PC", ActivitySources.Browser, "old.example.com/page", "chrome", "旧页",
                Day.AddHours(10), Day.AddHours(10).AddMinutes(10)),
        };

        var result = RecapProjection.Project(segments, Window, TimeSpan.Zero, depthTables: tables);

        // site 节点聚合 70 分,下一深度是两条 url,url 下再挂 tab_title(递归)
        Assert.Contains("shenxianovo.com — 合计 1小时10分，2 次｜其中: blog.shenxianovo.com/post-1 40分｜其中: 文章一 40分 · heartbeat.shenxianovo.com/dashboard 30分｜其中: 看板 30分", result.Digest);
        Assert.Contains("old.example.com/page — 合计 10分，1 次｜其中: 旧页 10分", result.Digest);
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
            Window, TimeSpan.Zero, strands: null, episodes: null, recurringReadings: ["WeChat", "qq.com"]);

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
