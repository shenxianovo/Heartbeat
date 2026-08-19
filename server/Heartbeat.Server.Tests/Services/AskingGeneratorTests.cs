using Heartbeat.Core;
using Heartbeat.Core.DTOs.Knowledge;
using Heartbeat.Server.Services;

namespace Heartbeat.Server.Tests.Services;

/// <summary>非流式 choices 提取与发问判官的纯函数半：prompt 构建、宽容解析。</summary>
public class AskingGeneratorTests
{
    // ---- ChatCompletionClient.ExtractContent ----

    [Fact]
    public void ExtractContent_WellFormed_ReturnsContent()
    {
        var body = """{"choices":[{"message":{"role":"assistant","content":"你好"}}]}""";

        Assert.Equal("你好", ChatCompletionClient.ExtractContent(body));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("""{"choices":[]}""")]
    [InlineData("""{"choices":[{"message":{}}]}""")]
    public void ExtractContent_Malformed_ReturnsNull(string body)
    {
        Assert.Null(ChatCompletionClient.ExtractContent(body));
    }

    // 流式分块的 delta 提取（ExtractChunk）与思考参数迁去了 ChatCompletionClientTests：
    // 那边有 fake HttpMessageHandler，能把传输层的时限与分型一起钉住。

    // ---- OpenAiCompatibleAskingGenerator.BuildUserPrompt ----

    [Fact]
    public void BuildUserPrompt_DigestFirst_ThenAdjudicationLog()
    {
        var prompt = OpenAiCompatibleAskingGenerator.BuildUserPrompt(
            "DIGEST-BODY",
            new AskingContext(
                ["HyperFrames（动效框架）← system: L1 app equals \"blender.exe\""],
                ["browser: L1 url contains \"news.example.com\""]));

        Assert.StartsWith("DIGEST-BODY", prompt); // digest 是共享前缀
        Assert.Contains("已绑定：HyperFrames", prompt);
        Assert.Contains("已静音：browser", prompt);
    }

    [Fact]
    public void BuildUserPrompt_ColdStart_SaysSo()
    {
        var prompt = OpenAiCompatibleAskingGenerator.BuildUserPrompt(
            "D", new AskingContext([], []));

        Assert.Contains("暂无", prompt);
    }

    // ---- OpenAiCompatibleAskingGenerator.BuildSystemPrompt（ADR-030 §7 声明驱动词汇）----

    [Fact]
    public void BuildSystemPrompt_InjectsDeclaredVocabulary()
    {
        var prompt = OpenAiCompatibleAskingGenerator.BuildSystemPrompt(
            "  - vscode：\"repo\"（仓库） → \"file\"（文件）");

        Assert.Contains("vscode：\"repo\"（仓库） → \"file\"（文件）", prompt);
        Assert.DoesNotContain("{{VOCAB}}", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_EmptyVocabulary_FallsBackToSeeds()
    {
        var prompt = OpenAiCompatibleAskingGenerator.BuildSystemPrompt("");

        Assert.Contains("system：\"app\"（应用） → \"title\"（窗口标题）", prompt);
        Assert.DoesNotContain("{{VOCAB}}", prompt);
    }

    // ---- OpenAiCompatibleAskingGenerator.Parse ----

    [Fact]
    public void Parse_FencedArray_StripsAndParses()
    {
        var content = """
            好的，以下是问题：
            ```json
            [{"question":"这是在直播吗？",
              "matcher":{"source":"system","steps":[{"reading":"app","op":"EQUALS","value":" livehime "}]}}]
            ```
            """;

        var result = OpenAiCompatibleAskingGenerator.Parse(content);

        Assert.NotNull(result);
        var q = Assert.Single(result);
        Assert.Equal("这是在直播吗？", q.Question);
        // matcher 已规范化：op 小写、值 trim
        var step = Assert.Single(q.Matcher.Steps);
        Assert.Equal(("equals", "livehime"), (step.Op, step.Value));
    }

    [Fact]
    public void Parse_LegacySingleStageFields_Ignored()
    {
        // 判官若仍回旧单阶段字段（evidence / proposedName / proposedGloss），照常解析、字段丢弃——
        // 证据由服务端物化，名字/释义归用户（ADR-031 §6）。
        var content = """
            [{"question":"这是什么？","evidence":"整段下午","proposedName":"猜的","proposedGloss":"猜的释义",
              "matcher":{"source":"system","steps":[{"reading":"app","op":"equals","value":"x"}]}}]
            """;

        var result = OpenAiCompatibleAskingGenerator.Parse(content);

        Assert.NotNull(result);
        Assert.Equal("这是什么？", Assert.Single(result).Question);
    }

    [Fact]
    public void Parse_EmptyArray_IsValidQuietDay()
    {
        var result = OpenAiCompatibleAskingGenerator.Parse("[]");

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_InvalidItems_DroppedNotFatal()
    {
        var content = """
            [
              {"question":"","matcher":{"source":"system","steps":[{"reading":"app","op":"equals","value":"x"}]}},
              {"question":"没有 matcher 的问题"},
              {"question":"matcher 无效","matcher":{"source":"system","steps":[]}},
              {"question":"合法的","matcher":{"source":"system","steps":[{"reading":"app","op":"equals","value":"x"}]}}
            ]
            """;

        var result = OpenAiCompatibleAskingGenerator.Parse(content);

        Assert.NotNull(result);
        Assert.Equal("合法的", Assert.Single(result).Question);
    }

    [Theory]
    [InlineData("完全不是 JSON")]
    [InlineData("{\"question\":\"不是数组\"}")]
    [InlineData("[{broken")]
    public void Parse_Unparseable_ReturnsNull(string content)
    {
        Assert.Null(OpenAiCompatibleAskingGenerator.Parse(content));
    }
}
