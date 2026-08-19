using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Heartbeat.Server.Services
{
    /// <summary>LLM 调用失败（未配置 / 上游错误 / 响应不可解析）。各出口自定失败语义：Recap 映射 502，发问返回空、不写缓存。</summary>
    public class ChatCompletionException(string message, Exception? inner = null) : Exception(message, inner);

    /// <summary>流式分块的分型：正文增量与推理增量走同一条流，语义不同。</summary>
    public enum LlmChunkKind
    {
        Content,
        Reasoning
    }

    /// <summary>流式分块：推理模型的思考期只吐 reasoning，正文一个字都还没有——它不是静默（ADR-042 §9）。</summary>
    public readonly record struct LlmChunk(LlmChunkKind Kind, string Text)
    {
        public static LlmChunk OfContent(string text) => new(LlmChunkKind.Content, text);

        public static LlmChunk OfReasoning(string text) => new(LlmChunkKind.Reasoning, text);
    }

    /// <summary>
    /// OpenAI 兼容 chat completions 的共享传输层（ADR-029 issue 03）：URL 拼接、鉴权、
    /// choices 提取、异常收敛一处实现；叙事与发问两个 generator 退成 prompt 构建 + 解析的纯函数。
    /// 不引 SDK——单一调用点，协议形状本身就是"先云后本地可逆"的兑现（ADR-023 §1）。
    ///
    /// 时限一律由 CancellationToken 表达，不靠 HttpClient.Timeout（ADR-042 §5）：后者覆盖"拿到响应
    /// 头"与默认 ResponseContentRead 下的缓冲正文，却管不到流式那条路自己读的流（.NET 10 实测），
    /// 一个值同时充当两种语义、还只对其中一种有效。所以 Program.cs 把它设成 Infinite：
    /// 流式的时限归调用方（它才知道"静默多久算死"）；非流式在本类内部自己施加，调用点零改动。
    /// </summary>
    public class ChatCompletionClient(HttpClient http, IOptions<RecapOptions> options, TimeSpan? nonStreamingTimeout = null)
    {
        /// <summary>
        /// 非流式请求的默认时限：发问/整理是一次性问答，没有"逐块到达"可观测，只能给整体上限。
        /// 300s 对着思考模型给足余量（8/7 的 digest 实测 181s，ADR-042 §9）。
        /// </summary>
        private static readonly TimeSpan DefaultNonStreamingTimeout = TimeSpan.FromSeconds(300);

        private readonly RecapOptions _options = options.Value;

        /// <summary>可注入是为了测试能用毫秒级值；生产用 <see cref="DefaultNonStreamingTimeout"/>。</summary>
        private readonly TimeSpan _nonStreamingTimeout = nonStreamingTimeout ?? DefaultNonStreamingTimeout;

        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(_options.BaseUrl)
            && !string.IsNullOrWhiteSpace(_options.ApiKey)
            && !string.IsNullOrWhiteSpace(_options.Model);

        public string Model => _options.Model;

        /// <summary>
        /// 一次补全。任何失败（含未配置、内部时限到期）抛 ChatCompletionException；
        /// 调用方自己的 ct 取消则让 OperationCanceledException 冒泡——那是上层语义，不是 LLM 的错。
        /// </summary>
        public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
        {
            if (!IsConfigured)
                throw new ChatCompletionException("LLM 未配置：需要 Recap:BaseUrl / Recap:ApiKey / Recap:Model。");

            // 时限自带（Q4）：HttpClient.Timeout 已交给 CTS，非流式若不自己计时就等于没有上限。
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_nonStreamingTimeout);

            try
            {
                return await SendAndReadAsync(systemPrompt, userPrompt, timeout.Token);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                throw new ChatCompletionException($"LLM 请求失败：{ex.Message}", ex);
            }
            catch (OperationCanceledException ex)
            {
                // TaskCanceledException 也走这里。区分两种取消：调用方取消属于上层语义，原样冒泡。
                if (ct.IsCancellationRequested) throw;
                throw new ChatCompletionException(
                    $"LLM 请求超时：超过 {FormatSeconds(_nonStreamingTimeout)} 未返回完整响应。", ex);
            }
        }

        private async Task<string> SendAndReadAsync(string systemPrompt, string userPrompt, CancellationToken ct)
        {
            using var request = BuildRequest(systemPrompt, userPrompt, stream: false);
            using var response = await http.SendAsync(request, ct);

            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new ChatCompletionException($"LLM 上游返回 {(int)response.StatusCode}：{Truncate(body)}");

            var content = ExtractContent(body);
            if (string.IsNullOrWhiteSpace(content))
                throw new ChatCompletionException($"LLM 响应无法解析：{Truncate(body)}");
            return content.Trim();
        }

        /// <summary>
        /// 流式补全（ADR-042 §8）：逐块产出带分型的增量。思考模型在正文之前会持续吐
        /// reasoning，一并产出——判"上游静默"要看有没有帧，而不是有没有正文（ADR-042 §9）。
        ///
        /// 失败语义与 CompleteAsync 一致：未配置 / 上游非 2xx / 流内一个正文 delta 都没有，
        /// 都抛 ChatCompletionException。静默与整段的时限由调用方用 CancellationToken 施加。
        /// </summary>
        public async IAsyncEnumerable<LlmChunk> CompleteStreamAsync(
            string systemPrompt,
            string userPrompt,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (!IsConfigured)
                throw new ChatCompletionException("LLM 未配置：需要 Recap:BaseUrl / Recap:ApiKey / Recap:Model。");

            using var request = BuildRequest(systemPrompt, userPrompt, stream: true);

            HttpResponseMessage response;
            try
            {
                // ResponseHeadersRead：拿到头就返回，正文留在流里逐块读。
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                throw new ChatCompletionException($"LLM 请求失败：{ex.Message}", ex);
            }
            catch (OperationCanceledException ex)
            {
                if (ct.IsCancellationRequested) throw;
                throw new ChatCompletionException($"LLM 请求失败：{ex.Message}", ex);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(ct);
                    throw new ChatCompletionException($"LLM 上游返回 {(int)response.StatusCode}：{Truncate(body)}");
                }

                var stream = await response.Content.ReadAsStreamAsync(ct);
                var parser = SseParser.Create(stream, static (_, data) => Encoding.UTF8.GetString(data));

                var produced = false;
                var enumerator = parser.EnumerateAsync(ct).GetAsyncEnumerator(ct);
                await using (enumerator.ConfigureAwait(false))
                {
                    while (true)
                    {
                        bool moved;
                        try
                        {
                            moved = await enumerator.MoveNextAsync();
                        }
                        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
                        {
                            throw new ChatCompletionException($"LLM 流式响应中断：{ex.Message}", ex);
                        }
                        catch (OperationCanceledException ex)
                        {
                            // 代理断链、上游 RST、任何传输层自带的时限到期都长这样（TaskCanceledException）。
                            // 调用方取消 → 冒泡（那是上层语义）；其余一律收敛成"流式响应中断"——否则它会
                            // 伪装成上层的判死线，把两种病症说成同一句话（这次排障最贵的那一段）。
                            if (ct.IsCancellationRequested) throw;
                            throw new ChatCompletionException($"LLM 流式响应中断：{ex.Message}", ex);
                        }

                        if (!moved) break;

                        var data = enumerator.Current.Data;
                        if (string.IsNullOrEmpty(data)) continue;
                        if (data == "[DONE]") break;

                        var (reasoning, content) = ExtractChunk(data);

                        // 同一帧里两者都有时先思考后正文：前端的"正在思考"必须先于它解释的那段正文。
                        if (reasoning != null) yield return LlmChunk.OfReasoning(reasoning);

                        if (content == null) continue;

                        // 只有正文算"产出"：全程只思考不落笔仍然是失败，不能让空叙事被当成成功落库。
                        produced = true;
                        yield return LlmChunk.OfContent(content);
                    }
                }

                if (!produced)
                    throw new ChatCompletionException("LLM 流式响应无内容。");
            }
        }

        private HttpRequestMessage BuildRequest(string systemPrompt, string userPrompt, bool stream)
        {
            var request = new HttpRequestMessage(
                HttpMethod.Post, $"{_options.BaseUrl.TrimEnd('/')}/chat/completions");
            request.Headers.Authorization = new("Bearer", _options.ApiKey);

            var payload = new Dictionary<string, object>
            {
                ["model"] = _options.Model,
                ["stream"] = stream,
                ["messages"] = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                }
            };

            // 发问/整理同样吃思考成本，所以流式与非流式都带上。
            var thinking = ThinkingParameter(_options.ReasoningEffort);
            if (thinking != null) payload[thinking.Value.Key] = thinking.Value.Value;

            request.Content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            return request;
        }

        /// <summary>
        /// 配置值 → 思考控制参数（DeepSeek 官方 OpenAI 格式，纯函数可测）：
        /// low/high/max → reasoning_effort；none/disabled → thinking.type=disabled；
        /// 空 / "default" / 任何未知值 → null（不传，用上游默认）。配置写错不该让功能死。
        /// </summary>
        public static KeyValuePair<string, object>? ThinkingParameter(string? reasoningEffort)
        {
            var value = reasoningEffort?.Trim().ToLowerInvariant();
            return value switch
            {
                "low" or "high" or "max" => new KeyValuePair<string, object>("reasoning_effort", value),
                "none" or "disabled" => new KeyValuePair<string, object>("thinking", new { type = "disabled" }),
                _ => null
            };
        }

        /// <summary>choices[0].message.content 提取（纯函数，可测）。形状不符或内容为空返回 null。</summary>
        public static string? ExtractContent(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                return doc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString();
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
            {
                return null;
            }
        }

        /// <summary>
        /// 流式分块的 delta 提取（纯函数，可测）：正文与推理各自可能为空。形状不符、坏 JSON、
        /// choices 为空、只带 role 的首块 → (null, null)；空串一律归一成 null，调用方只需判 null。
        /// </summary>
        public static (string? Reasoning, string? Content) ExtractChunk(string data)
        {
            try
            {
                using var doc = JsonDocument.Parse(data);
                var choices = doc.RootElement.GetProperty("choices");
                if (choices.GetArrayLength() == 0) return (null, null);
                if (!choices[0].TryGetProperty("delta", out var delta)) return (null, null);

                // DeepSeek 用 reasoning_content；部分兼容网关只给 reasoning，两者都认。
                var reasoning = DeltaText(delta, "reasoning_content") ?? DeltaText(delta, "reasoning");
                return (reasoning, DeltaText(delta, "content"));
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
            {
                return (null, null);
            }
        }

        private static string? DeltaText(JsonElement delta, string name)
        {
            if (!delta.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String)
                return null;
            var text = field.GetString();
            return string.IsNullOrEmpty(text) ? null : text;
        }

        private static string FormatSeconds(TimeSpan span) => $"{span.TotalSeconds:0.###} 秒";

        private static string Truncate(string s) => s.Length <= 500 ? s : s[..500];
    }
}
