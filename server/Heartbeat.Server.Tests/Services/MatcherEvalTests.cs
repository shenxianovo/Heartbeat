using Heartbeat.Core;
using Heartbeat.Core.DTOs.Knowledge;
using Heartbeat.Server.Services;

namespace Heartbeat.Server.Tests.Services;

/// <summary>Matcher 规范化与求值（ADR-029 §3）：路径谓词合取、三种谓词、幂等收敛的纯函数基础。</summary>
public class MatcherEvalTests
{
    private static MatcherStepDto Step(int layer, string reading, string op, string value)
        => new() { Layer = layer, Reading = reading, Op = op, Value = value };

    private static MatcherDto Matcher(string source, params MatcherStepDto[] steps)
        => new() { Source = source, Steps = [.. steps] };

    private static IReadOnlyList<DepthReading> SysReadings(string app, string? title = null)
        => DepthReadings.For(ActivitySources.System, app, title, $"{app}|{title}");

    // ---- 求值 ----

    [Fact]
    public void Equal_MatchesCaseInsensitive()
    {
        var m = Matcher(ActivitySources.System, Step(1, "app", MatcherOps.Equal, "code"));

        Assert.True(MatcherEval.Hits(ActivitySources.System, SysReadings("Code"), m));
        Assert.False(MatcherEval.Hits(ActivitySources.System, SysReadings("Codex"), m));
    }

    [Fact]
    public void Prefix_And_Contains()
    {
        var readings = DepthReadings.For(ActivitySources.Browser, null, "花生看板", "https://huasheng.com/dashboard");

        Assert.True(MatcherEval.Hits(ActivitySources.Browser, readings,
            Matcher(ActivitySources.Browser, Step(1, "url", MatcherOps.Prefix, "https://huasheng.com"))));
        Assert.False(MatcherEval.Hits(ActivitySources.Browser, readings,
            Matcher(ActivitySources.Browser, Step(1, "url", MatcherOps.Prefix, "huasheng.com"))));
        Assert.True(MatcherEval.Hits(ActivitySources.Browser, readings,
            Matcher(ActivitySources.Browser, Step(1, "tab_title", MatcherOps.Contains, "花生"))));
    }

    [Fact]
    public void PathPredicate_AllStepsMustMatch()
    {
        var m = Matcher(ActivitySources.System,
            Step(1, "app", MatcherOps.Equal, "Code"),
            Step(2, "title", MatcherOps.Contains, "hyperframes"));

        Assert.True(MatcherEval.Hits(ActivitySources.System, SysReadings("Code", "hyperframes — a.ts"), m));
        Assert.False(MatcherEval.Hits(ActivitySources.System, SysReadings("Code", "heartbeat — b.cs"), m)); // L2 不中
        Assert.False(MatcherEval.Hits(ActivitySources.System, SysReadings("Code"), m));                     // L2 读数缺席
    }

    [Fact]
    public void CrossSource_NeverHits()
    {
        var m = Matcher(ActivitySources.Browser, Step(1, "url", MatcherOps.Contains, "Code"));

        Assert.False(MatcherEval.Hits(ActivitySources.System, SysReadings("Code"), m));
    }

    [Fact]
    public void EmptySteps_NeverHits()
    {
        Assert.False(MatcherEval.Hits(ActivitySources.System, SysReadings("Code"),
            new MatcherDto { Source = ActivitySources.System, Steps = [] }));
    }

    // ---- 规范化 ----

    [Fact]
    public void Normalize_TrimsLowercasesOp_OrdersAndDedupesSteps()
    {
        var a = MatcherNormalizer.Normalize(Matcher(" system ",
            Step(2, "title", "CONTAINS", " hyperframes "),
            Step(1, "app", "Equals", "Code"),
            Step(1, "app", "equals", "Code"))); // 重复步
        var b = MatcherNormalizer.Normalize(Matcher("system",
            Step(1, "app", MatcherOps.Equal, "Code"),
            Step(2, "title", MatcherOps.Contains, "hyperframes")));

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal("system", a.Source);
        Assert.Equal(2, a.Steps.Count);
        // 步骤顺序无关：规范化后 canonical JSON 相等 → 幂等收敛的基础
        Assert.Equal(MatcherCodec.Serialize(b.Steps), MatcherCodec.Serialize(a.Steps));
    }

    [Fact]
    public void Normalize_InvalidMatcher_ReturnsNull()
    {
        Assert.Null(MatcherNormalizer.Normalize(Matcher("", Step(1, "app", MatcherOps.Equal, "Code"))));
        Assert.Null(MatcherNormalizer.Normalize(Matcher("system", Step(1, "app", MatcherOps.Equal, " "))));
        Assert.Null(MatcherNormalizer.Normalize(Matcher("system", Step(0, "app", MatcherOps.Equal, "Code"))));
        Assert.Null(MatcherNormalizer.Normalize(Matcher("system", Step(1, "app", "regex", "Code"))));
        Assert.Null(MatcherNormalizer.Normalize(new MatcherDto { Source = "system", Steps = [] }));
    }

    [Fact]
    public void Normalize_CanonicalIdentity_IsCaseInsensitive()
    {
        // 一把尺子：裁决身份判等 = 命中等价类。大小写变体必须收敛到同一 canonical 形，
        // 否则 Code.exe / code.exe 双身份 → 已裁决的问题复活（"别再问"食言）。
        var upper = MatcherNormalizer.Normalize(Matcher("SYSTEM", Step(1, "App", "EQUALS", "Code.EXE")));
        var lower = MatcherNormalizer.Normalize(Matcher("system", Step(1, "app", MatcherOps.Equal, "code.exe")));

        Assert.NotNull(upper);
        Assert.NotNull(lower);
        Assert.Equal(lower.Source, upper.Source);
        Assert.Equal(MatcherCodec.Serialize(lower.Steps), MatcherCodec.Serialize(upper.Steps));
    }

    [Fact]
    public void Hits_SourceAndReading_CaseInsensitive()
    {
        // 判官提案 "App" 之类的大小写变体不产生永不命中的死 Matcher。
        var m = Matcher("System", Step(1, "APP", MatcherOps.Equal, "code"));

        Assert.True(MatcherEval.Hits(ActivitySources.System, SysReadings("Code"), m));
    }

    [Fact]
    public void Codec_RoundTrips()
    {
        var steps = MatcherNormalizer.Normalize(
            Matcher("system", Step(1, "app", MatcherOps.Equal, "Code")))!.Steps;

        var roundTripped = MatcherCodec.Deserialize(MatcherCodec.Serialize(steps));

        var step = Assert.Single(roundTripped);
        Assert.Equal((1, "app", "equals", "code"), (step.Layer, step.Reading, step.Op, step.Value));
    }

    [Fact]
    public void Codec_MalformedJson_ReturnsEmpty()
    {
        Assert.Empty(MatcherCodec.Deserialize("not-json"));
    }
}
