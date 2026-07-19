using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Heartbeat.Server.Services
{
    /// <summary>一次性提案（ADR-028 §5）：AI 对一簇共现把手猜的名字 + 释义。空 = 猜不出/降级，用户纯手填。</summary>
    public readonly record struct ProposalDraft(string Name, string Gloss)
    {
        public static readonly ProposalDraft Empty = new(string.Empty, string.Empty);
    }

    /// <summary>
    /// Strand 提案生成器（ADR-028 §5）：生成层，薄、不可测。给一簇把手的文字上下文，一发 LLM 出 {name,gloss}。
    /// 刻意"失败降级为空提案"而非抛——提问是可选增强，不该因 LLM 不可用而阻塞用户纯手填。
    /// </summary>
    public interface IProposalGenerator
    {
        Task<ProposalDraft> DraftAsync(string clusterContext, CancellationToken ct = default);
    }

    /// <summary>OpenAI 兼容实现，复用 Recap 的 LLM 配置（ADR-023 §1 同一供应商纯配置）。</summary>
    public class OpenAiCompatibleProposalGenerator(HttpClient http, IOptions<RecapOptions> options) : IProposalGenerator
    {
        private const string PromptTemplate =
            """
            你在帮用户识别他电脑活动里的"脉络"——一个有名字的持续项目/爱好/活动。
            下面是今天出现的一个可观测标识（网站域名或应用名）与总时长，可能附带同时段出现的其它标识作参考。
            请猜这个标识对应什么。

            只输出一个 JSON 对象：{"name": "简短名字", "gloss": "一句话释义"}。
            - name 尽量短：就是这个东西本身的名字（站名/软件名/项目名），不要概括用户的一天。
            - gloss 一句话，说明这个标识具体是什么。
            - 只谈被问的那一个标识；参考标识仅用于消歧，不要并进答案。
            - 宁可空着（name/gloss 给空串）也不要瞎编事实。不要输出 JSON 以外的任何内容。
            """;

        private readonly RecapOptions _options = options.Value;

        public async Task<ProposalDraft> DraftAsync(string clusterContext, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(_options.BaseUrl) || string.IsNullOrWhiteSpace(_options.ApiKey)
                || string.IsNullOrWhiteSpace(_options.Model))
                return ProposalDraft.Empty; // 未配置：降级，不阻塞。

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
                        new { role = "system", content = PromptTemplate },
                        new { role = "user", content = clusterContext }
                    }
                }), Encoding.UTF8, "application/json");

                using var response = await http.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode) return ProposalDraft.Empty;

                var body = await response.Content.ReadAsStringAsync(ct);
                return Parse(body);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return ProposalDraft.Empty;
            }
        }

        private static ProposalDraft Parse(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var content = doc.RootElement.GetProperty("choices")[0]
                    .GetProperty("message").GetProperty("content").GetString();
                if (string.IsNullOrWhiteSpace(content)) return ProposalDraft.Empty;

                var json = ExtractJsonObject(content);
                if (json == null) return ProposalDraft.Empty;

                using var inner = JsonDocument.Parse(json);
                var root = inner.RootElement;
                var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var gloss = root.TryGetProperty("gloss", out var g) ? g.GetString() ?? "" : "";
                return new ProposalDraft(name.Trim(), gloss.Trim());
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
            {
                return ProposalDraft.Empty; // 解析失败也降级——提案是尽力而为。
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
