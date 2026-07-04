using Heartbeat.Agent.Http;
using Heartbeat.Agent.Storage;
using Heartbeat.Core;
using Heartbeat.Core.DTOs.Usage;
using Serilog;

namespace Heartbeat.Agent.Services
{
    public class UsageUploadService(HeartbeatApiClient apiClient, IUsageCache cache)
    {
        private static UsageUploadRequest MapToDto(List<AppUsageItem> items)
        {
            return new UsageUploadRequest
            {
                Usages = items.ConvertAll(i => new AppUsageItem
                {
                    Id = i.Id,
                    AppName = i.AppName,
                    Title = i.Title,
                    StartTime = i.StartTime,
                    EndTime = i.EndTime
                })
            };
        }

        public async Task UploadAsync(List<AppUsageItem> usages)
        {
            if (usages.Count == 0) return;

            // 出网前压缩快照：同 Id 只留最新（ADR-018）。缓存层不压缩。
            var compacted = SnapshotCompaction.KeepLatest(usages);
            var dto = MapToDto(compacted);

            Log.Information("正在上传 {Count} 条使用记录...", compacted.Count);
            var result = await apiClient.UploadUsageAsync(dto);
            if (!result.Success)
            {
                cache.Add(compacted);
                Log.Information("{Count} 条记录已缓存到本地", compacted.Count);
                return;
            }
            Log.Information("上传成功，共 {Count} 条记录", compacted.Count);
        }

        public async Task UploadCachedAsync()
        {
            var cached = cache.Load();
            if (cached.Count == 0) return;

            // 缓存为纯追加，离线期间会积累同 Id 的多个快照，出网前统一压缩。
            var compacted = SnapshotCompaction.KeepLatest(cached);

            Log.Information("发现 {Count} 条缓存记录，尝试上传...", compacted.Count);
            var dto = MapToDto(compacted);

            var result = await apiClient.UploadUsageAsync(dto);
            if (!result.Success) return;

            cache.Clear();
            Log.Information("缓存记录上传成功，已清除本地缓存");
        }
    }
}
