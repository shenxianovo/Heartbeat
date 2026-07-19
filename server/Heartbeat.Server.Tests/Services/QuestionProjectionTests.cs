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

    private static readonly IReadOnlySet<HandleRef> None = new HashSet<HandleRef>();

    [Fact]
    public void SeparateSessions_DoNotCluster_GameVsProject()
    {
        // 上午打游戏（8–10），中间空 2h，下午做项目（12–14.5）。两会话间隔远超阈值 → 各成一簇。
        var intervals = new[]
        {
            Iv(ActivitySources.System, "Minecraft.exe", 8, 10),
            Iv(ActivitySources.System, "code.exe", 12, 14),
            Iv(ActivitySources.Browser, "localhost", 12.5, 14.5),
        };

        var clusters = QuestionProjection.Project(intervals, None, None);

        Assert.Equal(2, clusters.Count);
        var game = clusters.Single(c => c.Handles.Contains(Ref(ActivitySources.System, "Minecraft.exe")));
        Assert.Single(game.Handles); // 游戏簇里没有项目把手
        var project = clusters.Single(c => c.Handles.Contains(Ref(ActivitySources.Browser, "localhost")));
        Assert.Equal(2, project.Handles.Count);
        Assert.DoesNotContain(Ref(ActivitySources.System, "Minecraft.exe"), project.Handles);
    }

    [Fact]
    public void InterleavedWithinSession_ClusterTogether()
    {
        // 分钟级交错（同一注意力语境）→ 同一簇，交给表单去勾（ADR-028 §4）。
        var intervals = new[]
        {
            Iv(ActivitySources.System, "code.exe", 9, 9.4),
            Iv(ActivitySources.Browser, "localhost", 9.4, 9.7),
            Iv(ActivitySources.System, "blender.exe", 9.7, 10.2),
        };

        var cluster = Assert.Single(QuestionProjection.Project(intervals, None, None));
        Assert.Equal(3, cluster.Handles.Count);
    }

    [Fact]
    public void BelowNoiseFloor_Dropped()
    {
        // 30s 顺手一开，低于噪声地板 → 无问题。
        var intervals = new[] { Iv(ActivitySources.System, "notepad.exe", 9, 9 + 30.0 / 3600) };

        Assert.Empty(QuestionProjection.Project(intervals, None, None));
    }

    [Fact]
    public void ShortButNotRecurring_BelowMeaningful_Dropped()
    {
        // 5 分钟、非复现、未达有意义时长 → 不问。
        var intervals = new[] { Iv(ActivitySources.System, "weibo.exe", 9, 9 + 5.0 / 60) };

        Assert.Empty(QuestionProjection.Project(intervals, None, None));
    }

    [Fact]
    public void ShortButRecurring_Kept()
    {
        // 同样 5 分钟，但复现 → gate 放行（每天短暂反复的仪式值得命名）。
        var intervals = new[] { Iv(ActivitySources.System, "anki.exe", 9, 9 + 5.0 / 60) };
        var recurring = new HashSet<HandleRef> { Ref(ActivitySources.System, "anki.exe") };

        var cluster = Assert.Single(QuestionProjection.Project(intervals, None, recurring));
        Assert.Equal(Ref(ActivitySources.System, "anki.exe"), cluster.Anchor);
    }

    [Fact]
    public void MeaningfulTime_FirstDay_Kept_WithoutRecurrence()
    {
        // 首日 4h 大项目，从未复现，仍应问（有意义时长 OR 复现）。
        var intervals = new[] { Iv(ActivitySources.Browser, "newproject.com", 9, 13) };

        Assert.Single(QuestionProjection.Project(intervals, None, None));
    }

    [Fact]
    public void AdjudicatedHandles_ProduceNoQuestion()
    {
        // 已 Mute / 已绑定 Strand 的把手不再被问（diff 生效）。
        var intervals = new[]
        {
            Iv(ActivitySources.Browser, "news.com", 9, 11),
            Iv(ActivitySources.System, "code.exe", 9, 11),
        };
        var adjudicated = new HashSet<HandleRef>
        {
            Ref(ActivitySources.Browser, "news.com"),   // muted
            Ref(ActivitySources.System, "code.exe"),    // strand-bound
        };

        Assert.Empty(QuestionProjection.Project(intervals, adjudicated, None));
    }

    [Fact]
    public void SelfAnchorPriority_BoundHandleNotPulledByNewAnchor()
    {
        // blender 已绑给别的 Strand；今天与一个未知新锚点同会话共现。
        // 自锚优先：blender 退出跟车，不被吸进新簇（ADR-028 §3）。
        var intervals = new[]
        {
            Iv(ActivitySources.Browser, "brandnew.com", 9, 11),
            Iv(ActivitySources.System, "blender.exe", 9.5, 10.5),
        };
        var adjudicated = new HashSet<HandleRef> { Ref(ActivitySources.System, "blender.exe") };

        var cluster = Assert.Single(QuestionProjection.Project(intervals, adjudicated, None));
        Assert.Single(cluster.Handles);
        Assert.Equal(Ref(ActivitySources.Browser, "brandnew.com"), cluster.Anchor);
        Assert.DoesNotContain(Ref(ActivitySources.System, "blender.exe"), cluster.Handles);
    }

    [Fact]
    public void CapsAtThree_ByUnexplainedTime()
    {
        // 5 个分离会话（各自达标），只端出时长最长的 3 个。
        var intervals = new[]
        {
            Iv(ActivitySources.System, "a.exe", 0, 0.4),   // 24m
            Iv(ActivitySources.System, "b.exe", 2, 3.5),   // 90m
            Iv(ActivitySources.System, "c.exe", 5, 5.5),   // 30m
            Iv(ActivitySources.System, "d.exe", 7, 10),    // 180m ← 最长
            Iv(ActivitySources.System, "e.exe", 12, 13),   // 60m
        };

        var clusters = QuestionProjection.Project(intervals, None, None);

        Assert.Equal(3, clusters.Count);
        var tokens = clusters.SelectMany(c => c.Handles).Select(h => h.Token).ToList();
        Assert.Contains("d.exe", tokens); // 180m
        Assert.Contains("b.exe", tokens); // 90m
        Assert.Contains("e.exe", tokens); // 60m
        Assert.DoesNotContain("a.exe", tokens); // 24m 被挤出
    }

    [Fact]
    public void SameConstellationAcrossSessions_MergesAndSumsTime()
    {
        // 同一把手集在两个分离会话各出现一次 → 合并为一簇，时长累加。
        var intervals = new[]
        {
            Iv(ActivitySources.System, "code.exe", 9, 10),
            Iv(ActivitySources.Browser, "proj.com", 9, 10),
            Iv(ActivitySources.System, "code.exe", 14, 15),
            Iv(ActivitySources.Browser, "proj.com", 14, 15),
        };

        var cluster = Assert.Single(QuestionProjection.Project(intervals, None, None));
        Assert.Equal(2, cluster.Handles.Count);
        Assert.Equal(2 * 3600, cluster.TotalSeconds, 1); // 两小时累加
    }
}
