using Heartbeat.Collection.Hub.Http;
using Serilog;
using System.Text;
using System.Text.Json;

namespace Heartbeat.Collection.Hub.Collectors
{
    /// <summary>
    /// 采集器声明上行（ADR-030 §3）：把已验证 Package 注册的声明推给服务端。
    /// 挂在 UploadWorker 的既有节律上——每轮 drain 顺带尝试未确认的声明,
    /// 失败不阻塞段上传、下轮自然重试；acked 集只在内存（重启后重报一次,服务端同版幂等覆盖,无害）。
    /// hub 不解析声明语义，只转发 Package 验证过的原文。
    /// </summary>
    public class DeclarationUplinkService(HeartbeatApiClient apiClient, ICollectorDeclarationStore declarations)
    {
        private readonly HashSet<(string Source, int Version)> _acked = [];
        private readonly object _lock = new();

        /// <summary>本轮待上行的未确认 Package 声明（原文 JSON）。</summary>
        public IReadOnlyList<string> PendingDeclarations()
        {
            var pending = new List<string>();
            lock (_lock)
            {
                foreach (var (source, entry) in declarations.Snapshot)
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
