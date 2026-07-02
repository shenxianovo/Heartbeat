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

            // 出网前合并相邻碎片（落点甲）。缓存层不再 merge。
            var merged = UsageMerger.Merge(usages);
            var dto = MapToDto(merged);

            Log.Information("正在上传 {Count} 条使用记录...", merged.Count);
            var result = await apiClient.UploadUsageAsync(dto);
            if (!result.Success)
            {
                cache.Add(merged);
                Log.Information("{Count} 条记录已缓存到本地", merged.Count);
                return;
            }
            Log.Information("上传成功，共 {Count} 条记录", merged.Count);
        }

        public async Task UploadCachedAsync()
        {
            var cached = cache.Load();
            if (cached.Count == 0) return;

            // 缓存为纯追加，可能含跨批次碎片，出网前统一 merge。
            var merged = UsageMerger.Merge(cached);

            Log.Information("发现 {Count} 条缓存记录，尝试上传...", merged.Count);
            var dto = MapToDto(merged);

            var result = await apiClient.UploadUsageAsync(dto);
            if (!result.Success) return;

            cache.Clear();
            Log.Information("缓存记录上传成功，已清除本地缓存");
        }
    }
}
