using Heartbeat.Core;
using Heartbeat.Core.DTOs.Knowledge;
using Heartbeat.Server.Services;

namespace Heartbeat.Server.Tests.Services;

/// <summary>
/// 日期知识投影的纯函数测试（ADR-031 §7）：日期有效性、祖先链注入、父命中不展开后代、
/// 祖先去重、当日/非当日 Episode、canonical hash 稳定性与叙事变化检测。
/// </summary>
public class KnowledgeProjectionTests
{
    private static readonly DateOnly Date = new(2026, 7, 12);

    private static MatcherDto AppMatcher(string app) => new()
    {
        Source = ActivitySources.System,
        Steps = [new() { Reading = "app", Op = MatcherOps.Equal, Value = app }]
    };

    private static SourceObservation AppObservation(string app)
        => new(ActivitySources.System, [new DepthReading(1, "app", app)]);

    private static StrandKnowledgeInput Strand(
        Guid id, string name, Guid? parent = null, string gloss = "",
        DateOnly? from = null, DateOnly? to = null, params MatcherDto[] matchers)
        => new(id, parent, name, gloss, from, to, matchers);

    private static DateKnowledge Resolve(
        IReadOnlyList<StrandKnowledgeInput> strands,
        IReadOnlyList<EpisodeKnowledgeInput>? episodes = null,
        IReadOnlyList<SourceObservation>? observations = null,
        DateOnly? date = null)
        => KnowledgeProjection.Resolve(date ?? Date, strands, episodes ?? [], observations ?? []);

    // ---- 命中与祖先链 ----

    [Fact]
    public void LeafHit_InjectsFullAncestorChain_RootToLeaf()
    {
        var (root, mid, leaf) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var strands = new List<StrandKnowledgeInput>
        {
            Strand(root, "哔哩哔哩实习"),
            Strand(mid, "Hyperframes", parent: root),
            Strand(leaf, "产品调研与可行性分析", parent: mid, matchers: [AppMatcher("code.exe")]),
        };

        var result = Resolve(strands, observations: [AppObservation("code.exe")]);

        var node = Assert.Single(result.Strands, s => s.Id == leaf);
        Assert.Equal(["哔哩哔哩实习", "Hyperframes", "产品调研与可行性分析"], node.Path);
        Assert.Equal(3, result.Strands.Count); // 祖先各自成行（零 Matcher 的纯语境容器也注入）
    }

    [Fact]
    public void ParentHit_DoesNotActivateDescendants()
    {
        var (root, child) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var strands = new List<StrandKnowledgeInput>
        {
            Strand(root, "哔哩哔哩实习", matchers: [AppMatcher("feishu.exe")]),
            Strand(child, "Hyperframes", parent: root, matchers: [AppMatcher("code.exe")]),
        };

        var result = Resolve(strands, observations: [AppObservation("feishu.exe")]);

        var node = Assert.Single(result.Strands);
        Assert.Equal(root, node.Id);
    }

    [Fact]
    public void SharedAncestor_MultipleLeafHits_InjectedOnce()
    {
        var (root, leafA, leafB) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var strands = new List<StrandKnowledgeInput>
        {
            Strand(root, "哔哩哔哩实习"),
            Strand(leafA, "Hyperframes", parent: root, matchers: [AppMatcher("code.exe")]),
            Strand(leafB, "花生", parent: root, matchers: [AppMatcher("chrome.exe")]),
        };

        var result = Resolve(strands,
            observations: [AppObservation("code.exe"), AppObservation("chrome.exe")]);

        Assert.Equal(3, result.Strands.Count);
        Assert.Single(result.Strands, s => s.Id == root); // 共同祖先只注入一次
    }

    [Fact]
    public void NoHit_NothingInjected()
    {
        var strands = new List<StrandKnowledgeInput>
        {
            Strand(Guid.CreateVersion7(), "缺席项目", matchers: [AppMatcher("never.exe")]),
        };

        var result = Resolve(strands, observations: [AppObservation("code.exe")]);

        Assert.Empty(result.Strands);
    }

    // ---- 日期有效性 ----

    [Fact]
    public void StrandOutsideValidRange_NotConsidered_EvenIfMatcherHits()
    {
        var strands = new List<StrandKnowledgeInput>
        {
            Strand(Guid.CreateVersion7(), "已结束", to: Date.AddDays(-1), matchers: [AppMatcher("code.exe")]),
            Strand(Guid.CreateVersion7(), "还没开始", from: Date.AddDays(1), matchers: [AppMatcher("code.exe")]),
        };

        var result = Resolve(strands, observations: [AppObservation("code.exe")]);

        Assert.Empty(result.Strands);
    }

    [Fact]
    public void DateBoundaries_InclusiveBothEnds_UnknownEndpointsUnbounded()
    {
        var onStart = Strand(Guid.CreateVersion7(), "起点当天", from: Date, matchers: [AppMatcher("a.exe")]);
        var onEnd = Strand(Guid.CreateVersion7(), "终点当天", to: Date, matchers: [AppMatcher("b.exe")]);
        var openEnded = Strand(Guid.CreateVersion7(), "全开放", matchers: [AppMatcher("c.exe")]);

        var result = Resolve([onStart, onEnd, openEnded],
            observations: [AppObservation("a.exe"), AppObservation("b.exe"), AppObservation("c.exe")]);

        Assert.Equal(3, result.Strands.Count);
    }

    // ---- Episode ----

    [Fact]
    public void OnlySameLocalDateEpisodes_Injected()
    {
        var episodes = new List<EpisodeKnowledgeInput>
        {
            new(Guid.CreateVersion7(), Date, "今天调研了竞品", null, null, null),
            new(Guid.CreateVersion7(), Date.AddDays(-1), "昨天的事", null, null, null),
        };

        var result = Resolve([], episodes);

        var episode = Assert.Single(result.Episodes);
        Assert.Equal("今天调研了竞品", episode.Text);
    }

    [Fact]
    public void RelatedEpisode_BringsStrandAncestorContext_AndInjectsChain()
    {
        var (root, leaf) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var strands = new List<StrandKnowledgeInput>
        {
            Strand(root, "哔哩哔哩实习"),
            Strand(leaf, "Hyperframes", parent: root), // 零 Matcher，今天无观测命中
        };
        var episodes = new List<EpisodeKnowledgeInput>
        {
            new(Guid.CreateVersion7(), Date, "和 mentor 对齐了方案", null, null, leaf),
        };

        var result = Resolve(strands, episodes);

        var episode = Assert.Single(result.Episodes);
        Assert.Equal(["哔哩哔哩实习", "Hyperframes"], episode.StrandPath);
        Assert.Equal(2, result.Strands.Count); // 即便 Matcher 未命中，Episode 关联也带入祖先链
    }

    [Fact]
    public void IndependentEpisode_AppearsAsFactWithoutStrandContext()
    {
        var episodes = new List<EpisodeKnowledgeInput>
        {
            new(Guid.CreateVersion7(), Date, "下午去了趟牙医", null, null, null),
        };

        var result = Resolve([], episodes);

        var episode = Assert.Single(result.Episodes);
        Assert.Empty(episode.StrandPath);
        Assert.Empty(result.Strands);
    }

    [Fact]
    public void RelatedStrandExpired_EpisodeStaysAsIndependentFact()
    {
        var expired = Guid.CreateVersion7();
        var strands = new List<StrandKnowledgeInput>
        {
            Strand(expired, "已结束的项目", to: Date.AddDays(-10)),
        };
        var episodes = new List<EpisodeKnowledgeInput>
        {
            new(Guid.CreateVersion7(), Date, "翻了翻旧项目的资料", null, null, expired),
        };

        var result = Resolve(strands, episodes);

        var episode = Assert.Single(result.Episodes);
        Assert.Empty(episode.StrandPath); // 目标日期无效的 Strand 不注入语境
        Assert.Empty(result.Strands);
    }

    // ---- canonical hash ----

    [Fact]
    public void Hash_StableAcrossInputOrdering()
    {
        var (root, leafA, leafB) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var strands = new List<StrandKnowledgeInput>
        {
            Strand(root, "实习"),
            Strand(leafA, "项目A", parent: root, matchers: [AppMatcher("a.exe"), AppMatcher("a2.exe")]),
            Strand(leafB, "项目B", parent: root, matchers: [AppMatcher("b.exe")]),
        };
        var episodes = new List<EpisodeKnowledgeInput>
        {
            new(Guid.CreateVersion7(), Date, "事实一", null, null, leafA),
            new(Guid.CreateVersion7(), Date, "事实二", null, null, null),
        };
        var observations = new List<SourceObservation> { AppObservation("a.exe"), AppObservation("b.exe"), AppObservation("a2.exe") };

        var forward = KnowledgeProjection.Resolve(Date, strands, episodes, observations);
        var reversed = KnowledgeProjection.Resolve(
            Date,
            strands.AsEnumerable().Reverse().ToList(),
            episodes.AsEnumerable().Reverse().ToList(),
            observations.AsEnumerable().Reverse().ToList());

        Assert.Equal(forward.Hash, reversed.Hash); // 同一逻辑知识，查询顺序不同 → 同一标识
    }

    [Fact]
    public void Hash_ChangesOnNarrativeRelevantEdits()
    {
        var id = Guid.CreateVersion7();
        List<StrandKnowledgeInput> With(string gloss) => [Strand(id, "项目", gloss: gloss, matchers: [AppMatcher("a.exe")])];
        var observations = new List<SourceObservation> { AppObservation("a.exe") };

        var before = KnowledgeProjection.Resolve(Date, With("旧释义"), [], observations);
        var glossEdited = KnowledgeProjection.Resolve(Date, With("新释义"), [], observations);
        var episodeAdded = KnowledgeProjection.Resolve(Date, With("旧释义"),
            [new(Guid.CreateVersion7(), Date, "新事实", null, null, null)], observations);

        Assert.NotEqual(before.Hash, glossEdited.Hash);
        Assert.NotEqual(before.Hash, episodeAdded.Hash);
    }

    [Fact]
    public void Hash_ChangesWhenNewMatcherMakesOldDateRelevant()
    {
        var id = Guid.CreateVersion7();
        var observations = new List<SourceObservation> { AppObservation("a.exe") };

        var before = KnowledgeProjection.Resolve(Date,
            [Strand(id, "项目", matchers: [AppMatcher("other.exe")])], [], observations);
        var after = KnowledgeProjection.Resolve(Date,
            [Strand(id, "项目", matchers: [AppMatcher("other.exe"), AppMatcher("a.exe")])], [], observations);

        Assert.NotEqual(before.Hash, after.Hash); // 新 Matcher 使该日首次相关
    }

    [Fact]
    public void Hash_UnrelatedKnowledgeChange_DoesNotChangeIdentity()
    {
        var hitId = Guid.CreateVersion7();
        var hit = Strand(hitId, "命中的项目", matchers: [AppMatcher("a.exe")]);
        var observations = new List<SourceObservation> { AppObservation("a.exe") };

        var before = KnowledgeProjection.Resolve(Date, [hit], [], observations);
        var after = KnowledgeProjection.Resolve(Date,
            [hit, Strand(Guid.CreateVersion7(), "无关项目", gloss: "今天没出现", matchers: [AppMatcher("never.exe")])],
            [], observations);

        Assert.Equal(before.Hash, after.Hash); // 无关知识变化不使该日判脏（精确到相关知识）
    }
}
