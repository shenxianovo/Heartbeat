using Heartbeat.Core;
using Heartbeat.Core.DTOs.Knowledge;
using Heartbeat.Server.Services;

namespace Heartbeat.Server.Tests.Services;

/// <summary>
/// Matcher 规范化与求值（ADR-029 §3，步随 ADR-030 §6 去层号）：路径谓词合取、三种谓词、
/// 幂等收敛的纯函数基础。命中等价类 = 裁决身份等价类，一把尺子。
/// </summary>
public class MatcherEvalTests
{
    private static MatcherStepDto Step(string reading, string op, string value)
        => new() { Reading = reading, Op = op, Value = value };

    private static MatcherDto Matcher(string source, params MatcherStepDto[] steps)
        => new() { Source = source, Steps = [.. steps] };

    private static IReadOnlyList<DepthReading> SysReadings(string app, string? title = null)
        => DepthTables.Seeds.ReadingsFor(ActivitySources.System, app, title, $"{app}|{title}");

    // ---- 求值 ----

    [Fact]
    public void Equal_MatchesCaseInsensitive()
    {
        var m = Matcher(ActivitySources.System, Step("app", MatcherOps.Equal, "code"));

        Assert.True(MatcherEval.Hits(ActivitySources.System, SysReadings("Code"), m));
        Assert.False(MatcherEval.Hits(ActivitySources.System, SysReadings("Codex"), m));
    }

    [Fact]
    public void Prefix_And_Contains()
    {
        var readings = DepthTables.Seeds.ReadingsFor(
            ActivitySources.Browser, null, "花生看板", "https://huasheng.com/dashboard");

        Assert.True(MatcherEval.Hits(ActivitySources.Browser, readings,
            Matcher(ActivitySources.Browser, Step("url", MatcherOps.Prefix, "https://huasheng.com"))));
        Assert.False(MatcherEval.Hits(ActivitySources.Browser, readings,
            Matcher(ActivitySources.Browser, Step("url", MatcherOps.Prefix, "huasheng.com"))));
        Assert.True(MatcherEval.Hits(ActivitySources.Browser, readings,
            Matcher(ActivitySources.Browser, Step("tab_title", MatcherOps.Contains, "花生"))));
    }

    [Fact]
    public void PathPredicate_AllStepsMustMatch()
    {
        var m = Matcher(ActivitySources.System,
            Step("app", MatcherOps.Equal, "Code"),
            Step("title", MatcherOps.Contains, "hyperframes"));

        Assert.True(MatcherEval.Hits(ActivitySources.System, SysReadings("Code", "hyperframes — a.ts"), m));
        Assert.False(MatcherEval.Hits(ActivitySources.System, SysReadings("Code", "heartbeat — b.cs"), m)); // title 不中
        Assert.False(MatcherEval.Hits(ActivitySources.System, SysReadings("Code"), m));                     // title 读数缺席
    }

    [Fact]
    public void CrossSource_NeverHits()
    {
        var m = Matcher(ActivitySources.Browser, Step("url", MatcherOps.Contains, "Code"));

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
            Step("title", "CONTAINS", " hyperframes "),
            Step("app", "Equals", "Code"),
            Step("app", "equals", "Code"))); // 重复步
        var b = MatcherNormalizer.Normalize(Matcher("system",
            Step("app", MatcherOps.Equal, "Code"),
            Step("title", MatcherOps.Contains, "hyperframes")));

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
        Assert.Null(MatcherNormalizer.Normalize(Matcher("", Step("app", MatcherOps.Equal, "Code"))));
        Assert.Null(MatcherNormalizer.Normalize(Matcher("system", Step("app", MatcherOps.Equal, " "))));
        Assert.Null(MatcherNormalizer.Normalize(Matcher("system", Step(" ", MatcherOps.Equal, "Code"))));
        Assert.Null(MatcherNormalizer.Normalize(Matcher("system", Step("app", "regex", "Code"))));
        Assert.Null(MatcherNormalizer.Normalize(new MatcherDto { Source = "system", Steps = [] }));
    }

    [Fact]
    public void Normalize_CanonicalIdentity_IsCaseInsensitive()
    {
        // 一把尺子：裁决身份判等 = 命中等价类。大小写变体必须收敛到同一 canonical 形，
        // 否则 Code.exe / code.exe 双身份 → 已裁决的问题复活（"别再问"食言）。
        var upper = MatcherNormalizer.Normalize(Matcher("SYSTEM", Step("App", "EQUALS", "Code.EXE")));
        var lower = MatcherNormalizer.Normalize(Matcher("system", Step("app", MatcherOps.Equal, "code.exe")));

        Assert.NotNull(upper);
        Assert.NotNull(lower);
        Assert.Equal(lower.Source, upper.Source);
        Assert.Equal(MatcherCodec.Serialize(lower.Steps), MatcherCodec.Serialize(upper.Steps));
    }

    [Fact]
    public void Hits_SourceAndReading_CaseInsensitive()
    {
        // 判官提案 "App" 之类的大小写变体不产生永不命中的死 Matcher。
        var m = Matcher("System", Step("APP", MatcherOps.Equal, "code"));

        Assert.True(MatcherEval.Hits(ActivitySources.System, SysReadings("Code"), m));
    }

    [Fact]
    public void Normalize_IgnoresLegacyLayerField()
    {
        // ADR-030 §6：老 wire/缓存里的 layer 字段反序列化时被忽略（DTO 无此属性），
        // 深度重排永不失效存量 Matcher。
        var legacy = System.Text.Json.JsonSerializer.Deserialize<MatcherDto>(
            """{"Source":"system","Steps":[{"Layer":2,"Reading":"title","Op":"contains","Value":"X"}]}""");

        Assert.NotNull(legacy);
        var normalized = MatcherNormalizer.Normalize(legacy!);
        Assert.NotNull(normalized);
        Assert.True(MatcherEval.Hits(ActivitySources.System, SysReadings("Code", "topic X here"), normalized!));
    }

    [Fact]
    public void Codec_RoundTrips()
    {
        var steps = MatcherNormalizer.Normalize(
            Matcher("system", Step("app", MatcherOps.Equal, "Code")))!.Steps;

        var roundTripped = MatcherCodec.Deserialize(MatcherCodec.Serialize(steps));

        var step = Assert.Single(roundTripped);
        Assert.Equal(("app", "equals", "code"), (step.Reading, step.Op, step.Value));
    }

    [Fact]
    public void Codec_MalformedJson_ReturnsEmpty()
    {
        Assert.Empty(MatcherCodec.Deserialize("not-json"));
    }
}
