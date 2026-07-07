using Heartbeat.Agent.Http;
using Heartbeat.Agent.Storage;
using Heartbeat.Core;
using Heartbeat.Core.DTOs.Segments;
using Serilog;

namespace Heartbeat.Agent.Services
{
    /// <summary>
    /// 段上传（system 与段共用，ADR-020）：失败进离线缓存，恢复后重传。
    /// 段已闭合、带 UUIDv7，重传由服务端按 Id 幂等去重——无需客户端 merge。
    /// </summary>
    public class SegmentUploadService(HeartbeatApiClient apiClient, ISegmentCache cache)
    {
        public async Task UploadAsync(List<ActivitySegmentItem> segments)
        {
            if (segments.Count == 0) return;

            var dto = new SegmentUploadRequest { Segments = segments };

            Log.Information("正在上传 {Count} 条段...", segments.Count);
            var result = await apiClient.UploadSegmentsAsync(dto);
            if (!result.Success)
            {
                cache.Add(segments);
                Log.Information("{Count} 条段已缓存到本地", segments.Count);
                return;
            }
            Log.Information("段上传成功，共 {Count} 条", segments.Count);
        }

        public async Task UploadCachedAsync()
        {
            var cached = cache.Load();
            if (cached.Count == 0) return;

            // 缓存为纯追加，离线期间会积累同 Id 的多个快照，出网前统一压缩（ADR-018）。
            var compacted = SnapshotCompaction.KeepLatest(cached);

            Log.Information("发现 {Count} 条缓存段，尝试上传...", compacted.Count);
            var result = await apiClient.UploadSegmentsAsync(new SegmentUploadRequest { Segments = compacted });
            if (!result.Success) return;

            cache.Clear();
            Log.Information("缓存段上传成功，已清除本地缓存");
        }
    }
}
