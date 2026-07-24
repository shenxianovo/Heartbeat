using Heartbeat.Agent.Configuration;
using Heartbeat.Core;
using Heartbeat.Core.DTOs.Segments;
using System.Text.Json;
using System.Web;

namespace Heartbeat.Agent.Services
{
    /// <summary>
    /// loopback ingest 的协议层（ADR-020/026）：路由、JSON 解析、冒充守卫、状态码映射，
    /// 以及采集器发现（GET config 自动注册）与强制层停用（disabled source → 403）。
    /// 与 HttpListener 解耦——插件作者的 HTTP 契约（状态码/错误体/accepted 计数）在此可测。
    /// 冒充守卫（拒收 'system'）与停用守卫（403）放在这一层而非缓冲模块：它们防的是"谁在调"
    /// （传输信任问题）；缓冲模块对 source 无关，内置采集器进程内直调不经此层。
    /// </summary>
    public class SegmentIngestRequestHandler(SegmentIngestService ingestService, ConfigManager configManager)
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public sealed record Response(int StatusCode, string Body, bool IsJson);

        /// <summary>
        /// 身份应答：采集器发现 hub 端口时以 GET /v1/hub 探测，凭此确认对端是 heartbeat
        /// 而非恰好占用该端口的陌生服务（否则陌生 4xx 会被误判为"hub 拒收"而丢队列）。
        /// proto 为 ingest 协议版本，语义变更时递增。
        /// </summary>
        public const string HubIdentityJson = """{"app":"heartbeat","proto":1}""";

        /// <param name="query">URL 查询串（不含 '?'）；GET config 的 flushPeriodMs 由此读。</param>
        public async Task<Response> HandleAsync(string httpMethod, string? path, Stream body, string? query = null)
        {
            if (httpMethod == "GET" && path == "/v1/hub")
                return new Response(200, HubIdentityJson, true);

            // 采集器配置下行（ADR-026）：GET /v1/collectors/{source}/config
            if (httpMethod == "GET" && TryParseCollectorPath(path, "/config", out var source))
                return HandleCollectorConfig(source, query);

            // 采集器声明上报（ADR-030 §3）：POST /v1/collectors/{source}/declaration
            if (httpMethod == "POST" && TryParseCollectorPath(path, "/declaration", out var declSource))
                return await HandleCollectorDeclarationAsync(declSource, body);

            if (httpMethod != "POST" || path != "/v1/segments")
                return new Response(404, "not found; POST /v1/segments | GET /v1/hub | GET /v1/collectors/{source}/config | POST /v1/collectors/{source}/declaration", false);

            SegmentUploadRequest? dto;
            try
            {
                dto = await JsonSerializer.DeserializeAsync<SegmentUploadRequest>(body, JsonOptions);
            }
            catch (JsonException)
            {
                return new Response(400, "invalid JSON", false);
            }

            if (dto?.Segments == null || dto.Segments.Count == 0)
                return new Response(400, "segments cannot be empty", false);

            if (dto.Segments.Any(s => string.Equals(s.Source, ActivitySources.System, StringComparison.OrdinalIgnoreCase)))
                return new Response(400, $"Source '{ActivitySources.System}' is reserved for the built-in collector.", false);

            // 强制层停用（ADR-026 §4）：批中任一 source 被用户停用则整批 403，段丢弃。
            // hub 够不着采集器进程，403 是无鉴权 loopback 下唯一的准入闸门；采集器可能有 bug/装死。
            var collectors = configManager.Current.Collectors;
            var sources = dto.Segments.Select(s => s.Source).Distinct().ToList();
            var disabled = sources.FirstOrDefault(src => collectors.TryGetValue(src, out var e) && !e.Enabled);
            if (disabled != null)
                return new Response(403, $"Source '{disabled}' is deactivated.", false);

            // 发现即注册（ADR-026 §1）："已安装 = 被 hub 见过"，POST 也算触达——
            // 覆盖只推段、未实现 config 拉取的采集器。已注册的不重写盘。
            var unknown = sources.Where(src => !collectors.ContainsKey(src)).ToList();
            if (unknown.Count > 0)
            {
                configManager.Update(c =>
                {
                    foreach (var src in unknown)
                        c.Collectors.TryAdd(src, new Models.CollectorEntry());
                });
            }

            var accepted = ingestService.Accept(dto.Segments);
            return new Response(200, $"{{\"accepted\":{accepted}}}", true);
        }

        /// <summary>
        /// 采集器发现即注册（ADR-026 §1）：首次见到某 source 即记入注册表（默认 enabled），
        /// 这就是"已安装"。顺带更新其自报的 flushPeriodMs（Active 窗口据此派生，§3）。
        /// </summary>
        private Response HandleCollectorConfig(string source, string? query)
        {
            int? flushPeriodMs = null;
            if (!string.IsNullOrEmpty(query))
            {
                var parsed = HttpUtility.ParseQueryString(query)["flushPeriodMs"];
                if (int.TryParse(parsed, out var ms) && ms > 0)
                    flushPeriodMs = ms;
            }

            var enabled = true;
            configManager.Update(c =>
            {
                if (!c.Collectors.TryGetValue(source, out var entry))
                {
                    entry = new Models.CollectorEntry();
                    c.Collectors[source] = entry;
                }
                if (flushPeriodMs.HasValue)
                    entry.FlushPeriodMs = flushPeriodMs;
                enabled = entry.Enabled;
            });

            return new Response(200, $"{{\"enabled\":{(enabled ? "true" : "false")}}}", true);
        }

        /// <summary>
        /// 采集器声明上报（ADR-030 §3）：hub 只做运输不做语义校验（schema 归服务端）——
        /// 但守两条传输信任线：source 一致（body 里的 source 必须等于路径 source，防冒充邻居）、
        /// 拒收 system（内置采集器进程内声明，不走 loopback）。同版本重报幂等不写盘。
        /// </summary>
        private async Task<Response> HandleCollectorDeclarationAsync(string source, Stream body)
        {
            if (string.Equals(source, ActivitySources.System, StringComparison.OrdinalIgnoreCase))
                return new Response(400, $"Source '{ActivitySources.System}' is reserved for the built-in collector.", false);

            string raw;
            using (var reader = new StreamReader(body))
                raw = await reader.ReadToEndAsync();

            string? bodySource = null;
            int version = 0;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return new Response(400, "declaration must be a JSON object", false);
                if (doc.RootElement.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String)
                    bodySource = s.GetString();
                if (doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number)
                    version = v.GetInt32();
            }
            catch (JsonException)
            {
                return new Response(400, "invalid JSON", false);
            }

            if (!string.Equals(bodySource, source, StringComparison.OrdinalIgnoreCase))
                return new Response(400, "declaration source must match path source", false);
            if (version < 1)
                return new Response(400, "declaration version must be >= 1", false);

            configManager.Update(c =>
            {
                if (!c.Collectors.TryGetValue(source, out var entry))
                {
                    entry = new Models.CollectorEntry();
                    c.Collectors[source] = entry;
                }
                if (entry.DeclarationVersion != version || entry.DeclarationJson == null)
                {
                    entry.DeclarationJson = raw;
                    entry.DeclarationVersion = version;
                }
            });

            return new Response(204, string.Empty, false);
        }

        /// <summary>解析 /v1/collectors/{source}{suffix}，提取 source。source 段非空且不含 '/'。</summary>
        private static bool TryParseCollectorPath(string? path, string suffix, out string source)
        {
            source = string.Empty;
            if (path is null) return false;
            const string prefix = "/v1/collectors/";
            if (!path.StartsWith(prefix, StringComparison.Ordinal) || !path.EndsWith(suffix, StringComparison.Ordinal))
                return false;

            var mid = path[prefix.Length..^suffix.Length];
            if (mid.Length == 0 || mid.Contains('/')) return false;

            source = Uri.UnescapeDataString(mid);
            return source.Length > 0;
        }
    }
}
