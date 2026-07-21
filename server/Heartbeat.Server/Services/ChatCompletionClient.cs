using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Heartbeat.Server.Services
{
    /// <summary>LLM 调用失败（未配置 / 上游错误 / 响应不可解析）。各出口自定失败语义：Recap 映射 502，发问返回空、不写缓存。</summary>
    public class ChatCompletionException(string message, Exception? inner = null) : Exception(message, inner);

    /// <summary>
    /// OpenAI 兼容 chat completions 的共享传输层（ADR-029 issue 03）：URL 拼接、鉴权、
    /// choices 提取、异常收敛一处实现；叙事与发问两个 generator 退成 prompt 构建 + 解析的纯函数。
    /// 不引 SDK——单一调用点，协议形状本身就是"先云后本地可逆"的兑现（ADR-023 §1）。
    /// </summary>
    public class ChatCompletionClient(HttpClient http, IOptions<RecapOptions> options)
    {
        private readonly RecapOptions _options = options.Value;

        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(_options.BaseUrl)
            && !string.IsNullOrWhiteSpace(_options.ApiKey)
            && !string.IsNullOrWhiteSpace(_options.Model);

        public string Model => _options.Model;

        /// <summary>一次补全。任何失败（含未配置）抛 ChatCompletionException。</summary>
        public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
        {
            if (!IsConfigured)
                throw new ChatCompletionException("LLM 未配置：需要 Recap:BaseUrl / Recap:ApiKey / Recap:Model。");

            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"{_options.BaseUrl.TrimEnd('/')}/chat/completions");
            request.Headers.Authorization = new("Bearer", _options.ApiKey);
            request.Content = new StringContent(JsonSerializer.Serialize(new
            {
                model = _options.Model,
                messages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                }
            }), Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                throw new ChatCompletionException($"LLM 请求失败：{ex.Message}", ex);
            }

            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                    throw new ChatCompletionException($"LLM 上游返回 {(int)response.StatusCode}：{Truncate(body)}");

                var content = ExtractContent(body);
                if (string.IsNullOrWhiteSpace(content))
                    throw new ChatCompletionException($"LLM 响应无法解析：{Truncate(body)}");
                return content.Trim();
            }
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

        private static string Truncate(string s) => s.Length <= 500 ? s : s[..500];
    }
}
