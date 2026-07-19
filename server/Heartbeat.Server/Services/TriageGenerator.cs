using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Heartbeat.Server.Services
{
    /// <summary>分诊裁定（ADR-028 §4 定稿）：一个把手该不该问。</summary>
    public enum TriageVerdict
    {
        /// <summary>不认识 / 像私有冷门：进提问队列，请用户命名。</summary>
        Ask,
        /// <summary>认识且有把握：不问，gloss 直接进 recap 的"已知脉络"块。</summary>
        Known,
        /// <summary>不认识、也没把握：既不问也不 gloss（偏安静：宁可不问）。recap 留原始把手。</summary>
        Silent,
    }

    /// <summary>分诊结果。Known 时带世界知识 gloss；Ask 时带一次性提案（猜名/释义，可空）。</summary>
    public readonly record struct TriageResult(TriageVerdict Verdict, string Name, string Gloss)
    {
        /// <summary>LLM 不可用/降级：不假装认识（那会错误压掉私有站），退回 Ask 让用户自己命名。</summary>
        public static readonly TriageResult FallbackAsk = new(TriageVerdict.Ask, string.Empty, string.Empty);
    }

    /// <summary>分诊输入：被判的把手 + 时长 + 贴邻共现（消歧上下文）。</summary>
    public readonly record struct TriageInput(string Source, string Token, int Minutes, IReadOnlyList<string> CoOccurring);

    /// <summary>用户历史裁决锚（few-shot）：bind 过的（值得问的样子）/ mute 过的（别问的样子）。</summary>
    public readonly record struct TriageAnchors(IReadOnlyList<string> Bound, IReadOnlyList<string> Muted);

    /// <summary>
    /// 分诊器（ADR-028 §4 定稿）：生成层，薄、不可测。对一个把手判 {Ask, Known, Silent}——
    /// 只问 AI 解释不了的（私有冷门），认识的自动 gloss 进 recap，拿不准就闭嘴（偏安静）。
    /// bind/mute 当 few-shot 锚：日 1 空锚 → 纯世界知识裸判（冷启动粗跑）；裁决攒起来越贴用户口味。
    /// 失败一律退回 Ask（不假装认识）——不该因 LLM 不可用而错误压掉一个私有站。
    /// </summary>
    public interface ITriageGenerator
    {
        Task<TriageResult> TriageAsync(TriageInput input, TriageAnchors anchors, CancellationToken ct = default);
    }

    /// <summary>OpenAI 兼容实现，复用 Recap 的 LLM 配置（ADR-023 §1 同一供应商纯配置）。</summary>
    public class OpenAiCompatibleTriageGenerator(HttpClient http, IOptions<RecapOptions> options) : ITriageGenerator
    {
        private const string SystemPrompt =
            """
            你在帮用户识别他电脑活动里的可观测标识（网站域名或应用名）。对给定的一个标识判定三选一：

            - "known"：这是**全世界都认识**的名站/知名软件（如 bilibili、github、youtube、Photoshop）。
              你能自信说出它是什么。此时给出简短 name 和一句 gloss——它会直接用于叙事，不打扰用户。
            - "ask"：这看起来是**私有的、冷门的、或你说不准**的东西（个人域名、小众站、陌生 .exe）。
              交给用户命名。可给一个试探性 name/gloss，用户会纠正。
            - "silent"：你既不认识、也没把握它是不是值得记的东西。既不猜也不问，保持沉默。

            宁可 "ask" 或 "silent"，也不要把一个私有/冷门标识错判成 "known" 乱编释义。
            只输出一个 JSON 对象：{"verdict":"known|ask|silent","name":"","gloss":""}。不要输出别的。
            """;

        private readonly RecapOptions _options = options.Value;

        public async Task<TriageResult> TriageAsync(TriageInput input, TriageAnchors anchors, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(_options.BaseUrl) || string.IsNullOrWhiteSpace(_options.ApiKey)
                || string.IsNullOrWhiteSpace(_options.Model))
                return TriageResult.FallbackAsk; // 未配置：不假装认识，退回 Ask。

            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Post, $"{_options.BaseUrl.TrimEnd('/')}/chat/completions");
                request.Headers.Authorization = new("Bearer", _options.ApiKey);
                request.Content = new StringContent(JsonSerializer.Serialize(new
                {
                    model = _options.Model,
                    messages = new object[]
                    {
                        new { role = "system", content = SystemPrompt },
                        new { role = "user", content = BuildUserPrompt(input, anchors) }
                    }
                }), Encoding.UTF8, "application/json");

                using var response = await http.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode) return TriageResult.FallbackAsk;

                return Parse(await response.Content.ReadAsStringAsync(ct));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return TriageResult.FallbackAsk;
            }
        }

        private static string BuildUserPrompt(TriageInput input, TriageAnchors anchors)
        {
            var sb = new StringBuilder();
            if (anchors.Bound.Count > 0)
                sb.AppendLine($"用户过去认为**值得命名**的标识（倾向 ask）：{string.Join("、", anchors.Bound)}");
            if (anchors.Muted.Count > 0)
                sb.AppendLine($"用户过去认为**不值得记**的标识（倾向 silent）：{string.Join("、", anchors.Muted)}");
            sb.AppendLine($"待判标识：{input.Source}/{input.Token}");
            sb.AppendLine($"今日累计：约 {input.Minutes} 分钟");
            if (input.CoOccurring.Count > 0)
                sb.AppendLine($"同时段还出现：{string.Join("、", input.CoOccurring)}");
            return sb.ToString();
        }

        private static TriageResult Parse(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var content = doc.RootElement.GetProperty("choices")[0]
                    .GetProperty("message").GetProperty("content").GetString();
                if (string.IsNullOrWhiteSpace(content)) return TriageResult.FallbackAsk;

                var json = ExtractJsonObject(content);
                if (json == null) return TriageResult.FallbackAsk;

                using var inner = JsonDocument.Parse(json);
                var root = inner.RootElement;
                var verdict = (root.TryGetProperty("verdict", out var v) ? v.GetString() : null)?.ToLowerInvariant() switch
                {
                    "known" => TriageVerdict.Known,
                    "silent" => TriageVerdict.Silent,
                    _ => TriageVerdict.Ask, // 缺省/未知一律 Ask（不假装认识）。
                };
                var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var gloss = root.TryGetProperty("gloss", out var g) ? g.GetString() ?? "" : "";
                return new TriageResult(verdict, name.Trim(), gloss.Trim());
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
            {
                return TriageResult.FallbackAsk;
            }
        }

        /// <summary>宽容提取：模型可能包了 ```json 围栏或前后废话，取第一个 {…} 块。</summary>
        private static string? ExtractJsonObject(string content)
        {
            var start = content.IndexOf('{');
            var end = content.LastIndexOf('}');
            return start >= 0 && end > start ? content[start..(end + 1)] : null;
        }
    }
}
