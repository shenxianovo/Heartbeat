using Heartbeat.Agent.Configuration;
using Heartbeat.Agent.Http;
using Heartbeat.Core;
using Serilog;
using System.Text;
using System.Text.Json;

namespace Heartbeat.Agent.Services
{
    /// <summary>
    /// 采集器声明上行（ADR-030 §3）：把 system 的进程内声明 + registry 里各采集器上报的声明
    /// 推给服务端。挂在 UploadWorker 的既有节律上——每轮 drain 顺带尝试未确认的声明,
    /// 失败不阻塞段上传、下轮自然重试；acked 集只在内存（重启后重报一次,服务端同版幂等覆盖,无害）。
    /// hub 对插件声明不解析语义,原文转发;仅 system 声明因内置采集器无 loopback 通道而由 hub 持有。
    /// </summary>
    public class DeclarationUplinkService(HeartbeatApiClient apiClient, ConfigManager configManager)
    {
        /// <summary>
        /// system 采集器的观测深度表（ADR-030 §1）:内置采集器的契约声明,与服务端种子 v1 同形。
        /// 深度表变更(加层/挪层)才递增 version。
        /// </summary>
        public const string SystemDeclarationJson =
            """{"source":"system","version":1,"layers":[{"readings":[{"name":"app","from":"appName","label":"应用"},{"name":"title","from":"title","label":"窗口标题"}]}]}""";

        private readonly HashSet<(string Source, int Version)> _acked = [];
        private readonly object _lock = new();

        /// <summary>本轮待上行的声明（原文 JSON）:system 常量 + registry 未确认项。</summary>
        public IReadOnlyList<string> PendingDeclarations()
        {
            var pending = new List<string>();
            lock (_lock)
            {
                if (!_acked.Contains((ActivitySources.System, 1)))
                    pending.Add(SystemDeclarationJson);

                foreach (var (source, entry) in configManager.Current.Collectors)
                {
                    if (entry.DeclarationJson == null || entry.DeclarationVersion is not { } version)
                        continue;
                    if (!_acked.Contains((source, version)))
                        pending.Add(entry.DeclarationJson);
                }
            }
            return pending;
        }

        /// <summary>推送一轮:全部成功才记 acked（服务端批量原子,坏批 400 时整批留待下轮）。</summary>
        public async Task PushOnceAsync(CancellationToken ct = default)
        {
            var pending = PendingDeclarations();
            if (pending.Count == 0) return;

            var batch = new StringBuilder("[");
            batch.Append(string.Join(',', pending));
            batch.Append(']');

            var result = await apiClient.UploadCollectorDeclarationsAsync(batch.ToString(), ct);
            if (!result.Success)
            {
                Log.Debug("采集器声明上行未成功（{Count} 份），下轮重试", pending.Count);
                return;
            }

            lock (_lock)
            {
                foreach (var raw in pending)
                {
                    if (TryReadIdentity(raw, out var identity))
                        _acked.Add(identity);
                }
            }
            Log.Information("采集器声明已上行 {Count} 份", pending.Count);
        }

        private static bool TryReadIdentity(string declarationJson, out (string Source, int Version) identity)
        {
            identity = default;
            try
            {
                using var doc = JsonDocument.Parse(declarationJson);
                if (!doc.RootElement.TryGetProperty("source", out var s) || s.ValueKind != JsonValueKind.String)
                    return false;
                if (!doc.RootElement.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.Number)
                    return false;
                identity = (s.GetString()!, v.GetInt32());
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }
}
