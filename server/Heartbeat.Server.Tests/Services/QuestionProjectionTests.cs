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

    // 粗筛只做"限流+剔噪"，不判该不该问（那归 LLM 分诊）。以下测试锁定粗筛的确定性边界。

    [Fact]
    public void ShellApps_NeverShortlisted()
    {
        // OS 外壳 / 浏览器进程 / away：即使占满整天也零信息，绝不进短名单。
        var intervals = new[]
        {
            Iv(ActivitySources.System, "explorer", 8, 16),
            Iv(ActivitySources.System, "ShellExperienceHost", 8, 16),
            Iv(ActivitySources.System, "__away__", 8, 16),
            Iv(ActivitySources.System, "msedge", 8, 16),   // 浏览器进程：卫星，身份在域名侧
            Iv(ActivitySources.System, "chrome", 8, 16),
            Iv(ActivitySources.Browser, "newtab", 8, 16),
            Iv(ActivitySources.Browser, "localhost", 8, 16),
        };

        Assert.Empty(QuestionProjection.Shortlist(intervals, None, None));
    }

    [Fact]
    public void OneCandidatePerHandle()
    {
        // 每个把手一个候选（不聚簇成 constellation）。
        var intervals = new[]
        {
            Iv(ActivitySources.Browser, "huasheng.cn", 9, 12),
            Iv(ActivitySources.Browser, "github.com", 9.1, 11.5),
        };

        var shortlist = QuestionProjection.Shortlist(intervals, None, None);

        Assert.Equal(2, shortlist.Count);
        Assert.Contains(shortlist, c => c.Handle == Web("huasheng.cn"));
        Assert.Contains(shortlist, c => c.Handle == Web("github.com"));
    }

    [Fact]
    public void RecurringUbiquitousHandle_HasHigherGate()
    {
        // 微信天天出现（复现）：1.5 小时不够进名单——ubiquity 惩罚（复现 gate 2h）。
        var wechat = Sys("Weixin");
        var recurring = new HashSet<HandleRef> { wechat };
        var ninetyMinutes = new[] { Iv(ActivitySources.System, "Weixin", 9, 10.5) };

        Assert.Empty(QuestionProjection.Shortlist(ninetyMinutes, None, recurring));

        // 同一把手若非复现（第一次见），1.5h 过非复现 gate（30min）→ 进名单。
        var fresh = QuestionProjection.Shortlist(ninetyMinutes, None, None);
        Assert.Single(fresh);
        Assert.Equal(wechat, fresh[0].Handle);
    }

    [Fact]
    public void FirstDayHighSpecificity_MeaningfulTime_Shortlisted()
    {
        // 首见的具体域名，40 分钟 → 过非复现 gate。
        var intervals = new[] { Iv(ActivitySources.Browser, "newproject.com", 9, 9 + 40.0 / 60) };

        var c = Assert.Single(QuestionProjection.Shortlist(intervals, None, None));
        Assert.Equal(Web("newproject.com"), c.Handle);
    }

    [Fact]
    public void BelowNoiseFloor_Dropped()
    {
        var intervals = new[] { Iv(ActivitySources.Browser, "blip.com", 9, 9 + 30.0 / 3600) };
        Assert.Empty(QuestionProjection.Shortlist(intervals, None, None));
    }

    [Fact]
    public void ShortNonRecurring_BelowMeaningful_Dropped()
    {
        // 只 5 分钟、未过 gate：不进名单。
        var intervals = new[] { Iv(ActivitySources.Browser, "quick.com", 9, 9 + 5.0 / 60) };
        Assert.Empty(QuestionProjection.Shortlist(intervals, None, None));
    }

    [Fact]
    public void AdjudicatedHandles_Excluded()
    {
        var intervals = new[]
        {
            Iv(ActivitySources.Browser, "news.com", 9, 12),
            Iv(ActivitySources.Browser, "proj.com", 9, 12),
        };
        var adjudicated = new HashSet<HandleRef> { Web("news.com"), Web("proj.com") };

        Assert.Empty(QuestionProjection.Shortlist(intervals, adjudicated, None));
    }

    [Fact]
    public void ShortlistCapped_ByTime()
    {
        // 10 个高特异性域名各占大量时间，粗筛限流到 8（不选题——只控分诊调用量）。
        var intervals = Enumerable.Range(0, 10)
            .Select(i => Iv(ActivitySources.Browser, $"d{i}.com", i, i + 0.9))
            .ToArray();

        var shortlist = QuestionProjection.Shortlist(intervals, None, None);

        Assert.Equal(8, shortlist.Count);
    }

    [Fact]
    public void CoOccurring_OnlyTimeAdjacentShortlistedHandles()
    {
        // 候选的共现提示：同时段的另一候选把手进，OS 外壳不进（外壳压根不在候选集）。
        var intervals = new[]
        {
            Iv(ActivitySources.Browser, "huasheng.cn", 9, 12),
            Iv(ActivitySources.System, "Code", 9.2, 11.8),   // 贴邻候选 → 进提示
            Iv(ActivitySources.System, "explorer", 9, 12),   // 外壳 → 不进
        };

        var c = QuestionProjection.Shortlist(intervals, None, None).Single(x => x.Handle == Web("huasheng.cn"));

        Assert.Contains(Sys("Code"), c.CoOccurring);
        Assert.DoesNotContain(Sys("explorer"), c.CoOccurring);
    }

    [Fact]
    public void RecurringFlag_SetOnCandidate()
    {
        // 复现标记透传给候选，供分诊作先验。
        var intervals = new[] { Iv(ActivitySources.Browser, "daily.com", 9, 13) };
        var recurring = new HashSet<HandleRef> { Web("daily.com") };

        var c = Assert.Single(QuestionProjection.Shortlist(intervals, None, recurring));
        Assert.True(c.Recurring);
    }
}
