using Heartbeat.Core;
using Heartbeat.Core.DTOs.Knowledge;
using Heartbeat.Server.Services;

namespace Heartbeat.Server.Tests.Services;

/// <summary>
/// 提案层的纯函数半（ADR-031 §6）：schema 解析宽容性、引用消毒（虚构/越权剔除）、
/// 版本盖章与临时 OpId 纪律。数据库路径见 KnowledgeProposalServiceTests。
/// </summary>
public class ProposalSanitizerTests
{
    private static readonly Guid StrandId = Guid.CreateVersion7();
    private static readonly Guid EpisodeId = Guid.CreateVersion7();
    private static readonly Guid ProbeId = Guid.CreateVersion7();

    private static ProposalContext Context() => new(
        [new ProposalStrand(StrandId, ["哔哩哔哩实习", "Hyperframes"], "动效框架产品", null, null, Version: 5)],
        [new ProposalEpisode(EpisodeId, new DateOnly(2026, 7, 24), "做产品调研", Version: 2)],
        [new ProposalProbe(ProbeId, EpisodeId)],
        new DateOnly(2026, 7, 24));

    private static MatcherDto AppMatcher(string app) => new()
    {
        Source = ActivitySources.System,
        Steps = [new() { Reading = "app", Op = MatcherOps.Equal, Value = app }]
    };

    // ---- Parse ----

    [Fact]
    public void Parse_FencedObject_StripsAndParses()
    {
        var content = """
            好的：
            ```json
            {"explanation":"你在做产品调研","operations":[{"opId":"op1","type":"createEpisode","localDate":"2026-07-24","text":"调研"}],"suggestions":["建议 A"]}
            ```
            """;

        var raw = OpenAiCompatibleProposalGenerator.Parse(content);

        Assert.NotNull(raw);
        Assert.Equal("你在做产品调研", raw.Explanation);
        Assert.Single(raw.Operations!);
        Assert.Single(raw.Suggestions!);
    }

    [Theory]
    [InlineData("完全不是 JSON")]
    [InlineData("[1,2,3]")]
    [InlineData("{broken")]
    public void Parse_Unparseable_ReturnsNull(string content)
    {
        Assert.Null(OpenAiCompatibleProposalGenerator.Parse(content));
    }

    // ---- 引用消毒 ----

    [Fact]
    public void FabricatedStrandId_OperationDropped_WithWarning()
    {
        var raw = new RawKnowledgeProposal
        {
            Operations =
            [
                new() { OpId = "op1", Type = "updateStrand", StrandId = Guid.CreateVersion7().ToString(), Name = "劫持" },
                new() { OpId = "op2", Type = "endStrand", StrandId = "not-a-guid", EndedOn = "2026-07-24" },
                new() { OpId = "op3", Type = "createEpisode", Text = "合法的", LocalDate = "2026-07-24" },
            ],
        };

        var result = ProposalSanitizer.Sanitize(raw, Context());

        // 虚构/畸形 UUID 整条剔除并出警告；合法操作保留
        var op = Assert.Single(result.Operations);
        Assert.Equal("createEpisode", op.Type);
        Assert.Equal(2, result.Warnings.Count);
    }

    [Fact]
    public void ExistingReferences_StampedWithServerReadVersions()
    {
        var raw = new RawKnowledgeProposal
        {
            Operations =
            [
                new() { OpId = "op1", Type = "updateStrand", StrandId = StrandId.ToString(), Gloss = "新释义" },
                new() { OpId = "op2", Type = "relateEpisode", EpisodeId = EpisodeId.ToString(), RelatedStrandId = StrandId.ToString() },
            ],
        };

        var result = ProposalSanitizer.Sanitize(raw, Context());

        // 版本由服务端语境盖章（读取时快照），不信 LLM 回显
        Assert.Equal(5, result.Operations[0].UpdateStrand!.ExpectedVersion);
        Assert.Equal(2, result.Operations[1].RelateEpisode!.ExpectedVersion);
        // update 语义：LLM 没提的字段回填现状
        Assert.Equal("Hyperframes", result.Operations[0].UpdateStrand!.Name);
        Assert.Equal("新释义", result.Operations[0].UpdateStrand!.Gloss);
    }

    [Fact]
    public void OpIdReferences_OnlyResolveToEarlierOpsOfRightKind()
    {
        var raw = new RawKnowledgeProposal
        {
            Operations =
            [
                new() { OpId = "op1", Type = "createStrand", Name = "新脉络" },
                // 合法：向后引用 op1
                new() { OpId = "op2", Type = "bindMatcher", StrandOpId = "op1", Matcher = AppMatcher("tool") },
                // 非法：引用不存在的 op99
                new() { OpId = "op3", Type = "bindMatcher", StrandOpId = "op99", Matcher = AppMatcher("tool") },
                // 非法：op2 不是 createStrand，不能当 Strand 引用
                new() { OpId = "op4", Type = "createEpisode", Text = "x", LocalDate = "2026-07-24", RelatedOpId = "op2" },
            ],
        };

        var result = ProposalSanitizer.Sanitize(raw, Context());

        Assert.Equal(2, result.Operations.Count);
        Assert.Equal("op1", result.Operations[1].BindMatcher!.Strand.OpId);
        Assert.Equal(2, result.Warnings.Count);
    }

    [Fact]
    public void ForeignProbe_AndInvalidResolution_Dropped()
    {
        var raw = new RawKnowledgeProposal
        {
            Operations =
            [
                new() { OpId = "op1", Type = "resolveProbe", ProbeId = Guid.CreateVersion7().ToString(), Resolution = "denied" },
                new() { OpId = "op2", Type = "resolveProbe", ProbeId = ProbeId.ToString(), Resolution = "promoted" },
                new() { OpId = "op3", Type = "resolveProbe", ProbeId = ProbeId.ToString(), Resolution = "denied" },
            ],
        };

        var result = ProposalSanitizer.Sanitize(raw, Context());

        // promoted 只能由提升事务写；语境外的 Probe 剔除
        var op = Assert.Single(result.Operations);
        Assert.Equal("op3", op.OpId);
        Assert.Equal(ProbeStatuses.Denied, op.ResolveProbe!.Resolution);
    }

    [Fact]
    public void InvalidMatchers_AndUnknownTypes_Dropped()
    {
        var raw = new RawKnowledgeProposal
        {
            Operations =
            [
                new() { OpId = "op1", Type = "muteMatcher", Matcher = new MatcherDto { Source = "", Steps = [] } },
                new() { OpId = "op2", Type = "deleteEverything" },
                new() { OpId = "op3", Type = "muteMatcher", Matcher = AppMatcher("  Noisy-Tool  ") },
            ],
        };

        var result = ProposalSanitizer.Sanitize(raw, Context());

        var op = Assert.Single(result.Operations);
        // Matcher 走同一 canonicalization（trim + 小写）
        Assert.Equal("noisy-tool", op.MuteMatcher!.Matcher.Steps[0].Value);
        Assert.Equal(2, result.Warnings.Count);
    }

    [Fact]
    public void MissingOrDuplicateOpIds_Regenerated()
    {
        var raw = new RawKnowledgeProposal
        {
            Operations =
            [
                new() { OpId = null, Type = "createEpisode", Text = "甲", LocalDate = "2026-07-24" },
                new() { OpId = "dup", Type = "createEpisode", Text = "乙", LocalDate = "2026-07-24" },
                new() { OpId = "dup", Type = "createEpisode", Text = "丙", LocalDate = "2026-07-24" },
            ],
        };

        var result = ProposalSanitizer.Sanitize(raw, Context());

        Assert.Equal(3, result.Operations.Count);
        Assert.Equal(3, result.Operations.Select(o => o.OpId).Distinct().Count());
    }

    [Fact]
    public void CreateEpisode_MalformedDate_FallsBackToEvidenceDay()
    {
        var raw = new RawKnowledgeProposal
        {
            Operations =
            [
                new() { OpId = "op1", Type = "createEpisode", Text = "事实", LocalDate = "someday" },
            ],
        };

        var result = ProposalSanitizer.Sanitize(raw, Context());

        Assert.Equal(new DateOnly(2026, 7, 24), Assert.Single(result.Operations).CreateEpisode!.LocalDate);
    }

    [Fact]
    public void ExplanationWarningsSuggestions_KeptDistinctFromOperations()
    {
        var raw = new RawKnowledgeProposal
        {
            Explanation = "  你在调研 Hyperframes  ",
            Operations = [new() { OpId = "op1", Type = "createEpisode", Text = "调研", LocalDate = "2026-07-24" }],
            Suggestions = ["这可能是一次性的", "", null],
        };

        var result = ProposalSanitizer.Sanitize(raw, Context());

        Assert.Equal("你在调研 Hyperframes", result.Explanation);
        Assert.Single(result.Operations);
        Assert.Equal(["这可能是一次性的"], result.Suggestions);
        Assert.Empty(result.Warnings);
    }
}
