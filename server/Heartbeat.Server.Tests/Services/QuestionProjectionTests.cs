using Heartbeat.Core;
using Heartbeat.Server.Services;

namespace Heartbeat.Server.Tests.Services;

public class QuestionProjectionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 8, 0, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(double hours) => T0.AddHours(hours);

    private static HandleInterval Iv(string source, string token, double startHour, double endHour) =>
        new(source, token, At(startHour), At(endHour));

    private static HandleRef Ref(string source, string token) => new(source, token);
    private static HandleRef Sys(string token) => new(ActivitySources.System, token);
    private static HandleRef Web(string token) => new(ActivitySources.Browser, token);

    private static readonly IReadOnlySet<HandleRef> None = new HashSet<HandleRef>();

    [Fact]
    public void ShellApps_NeverAsked()
    {
        // OS 外壳 / away：即使占满整天也零信息，不问。
        var intervals = new[]
        {
            Iv(ActivitySources.System, "explorer", 8, 16),
            Iv(ActivitySources.System, "ShellExperienceHost", 8, 16),
            Iv(ActivitySources.System, "__away__", 8, 16),
            Iv(ActivitySources.Browser, "newtab", 8, 16),
        };

        Assert.Empty(QuestionProjection.Project(intervals, None, None));
    }

    [Fact]
    public void SingleHandle_PerQuestion_NotAConstellation()
    {
        // 一个高特异性把手 = 一个问题；共现的另一个高特异性把手只作提示，不并进锚点。
        var intervals = new[]
        {
            Iv(ActivitySources.Browser, "huasheng.cn", 9, 12),
            Iv(ActivitySources.Browser, "github.com", 9.1, 11.5),
        };

        var qs = QuestionProjection.Project(intervals, None, None);

        Assert.All(qs, q => Assert.NotEqual(default, q.Anchor)); // 每个问题一个锚点
        Assert.Contains(qs, q => q.Anchor == Web("huasheng.cn"));
    }

    [Fact]
    public void BrowserDomain_OutranksBareSystemApp()
    {
        // 同样时长下，具体域名比裸系统进程更该问（特异性先验，ADR-028 §3）。
        var intervals = new[]
        {
            Iv(ActivitySources.System, "POWERPNT", 9, 12),
            Iv(ActivitySources.Browser, "huasheng.cn", 9, 12),
        };

        var qs = QuestionProjection.Project(intervals, None, None);

        Assert.Equal(Web("huasheng.cn"), qs[0].Anchor); // 域名排第一
    }

    [Fact]
    public void RecurringUbiquitousHandle_HasHigherGate()
    {
        // 微信天天出现（复现）：1.5 小时不够问——ubiquity 惩罚（复现 gate 2h）。
        var wechat = Sys("Weixin");
        var recurring = new HashSet<HandleRef> { wechat };
        var ninetyMinutes = new[] { Iv(ActivitySources.System, "Weixin", 9, 10.5) };

        Assert.Empty(QuestionProjection.Project(ninetyMinutes, None, recurring));

        // 同一把手若非复现（第一次见），1.5h 过非复现 gate（30min）→ 问。
        var fresh = QuestionProjection.Project(ninetyMinutes, None, None);
        Assert.Single(fresh);
        Assert.Equal(wechat, fresh[0].Anchor);
    }

    [Fact]
    public void FirstDayHighSpecificity_MeaningfulTime_Asked()
    {
        // 首见的具体域名，40 分钟 → 值得问（过非复现 gate）。
        var intervals = new[] { Iv(ActivitySources.Browser, "newproject.com", 9, 9 + 40.0 / 60) };

        var q = Assert.Single(QuestionProjection.Project(intervals, None, None));
        Assert.Equal(Web("newproject.com"), q.Anchor);
    }

    [Fact]
    public void BelowNoiseFloor_Dropped()
    {
        var intervals = new[] { Iv(ActivitySources.Browser, "blip.com", 9, 9 + 30.0 / 3600) };
        Assert.Empty(QuestionProjection.Project(intervals, None, None));
    }

    [Fact]
    public void ShortNonRecurring_BelowMeaningful_Dropped()
    {
        // 高特异性但只 5 分钟、未过 gate：不问。
        var intervals = new[] { Iv(ActivitySources.Browser, "quick.com", 9, 9 + 5.0 / 60) };
        Assert.Empty(QuestionProjection.Project(intervals, None, None));
    }

    [Fact]
    public void AdjudicatedHandles_ProduceNoQuestion()
    {
        var intervals = new[]
        {
            Iv(ActivitySources.Browser, "news.com", 9, 12),
            Iv(ActivitySources.Browser, "proj.com", 9, 12),
        };
        var adjudicated = new HashSet<HandleRef> { Web("news.com"), Web("proj.com") };

        Assert.Empty(QuestionProjection.Project(intervals, adjudicated, None));
    }

    [Fact]
    public void CapsAtThree_ByScore()
    {
        // 5 个高特异性域名各占大量时间，只端出头部 3。
        var intervals = new[]
        {
            Iv(ActivitySources.Browser, "a.com", 0, 1),
            Iv(ActivitySources.Browser, "b.com", 1, 3),
            Iv(ActivitySources.Browser, "c.com", 3, 4),
            Iv(ActivitySources.Browser, "d.com", 4, 8),   // 最长
            Iv(ActivitySources.Browser, "e.com", 8, 9),
        };

        var qs = QuestionProjection.Project(intervals, None, None);

        Assert.Equal(3, qs.Count);
        Assert.Equal(Web("d.com"), qs[0].Anchor); // 4h 排第一
    }

    [Fact]
    public void CoOccurring_OnlyTimeAdjacentHighSpecificityHandles()
    {
        // 锚点的共现提示：同时段的另一高特异性把手进，OS 外壳不进。
        var intervals = new[]
        {
            Iv(ActivitySources.Browser, "huasheng.cn", 9, 12),
            Iv(ActivitySources.System, "Code", 9.2, 11.8),   // 贴邻高特异性 → 进提示
            Iv(ActivitySources.System, "explorer", 9, 12),   // 外壳 → 不进
        };

        var q = QuestionProjection.Project(intervals, None, None).Single(x => x.Anchor == Web("huasheng.cn"));

        Assert.Contains(Sys("Code"), q.CoOccurring);
        Assert.DoesNotContain(Sys("explorer"), q.CoOccurring);
    }
}
