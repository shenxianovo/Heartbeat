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
                    AppName = i.AppName,
                    StartTime = i.StartTime,
                    EndTime = i.EndTime
                })
            };
        }

        public async Task UploadAsync(List<AppUsageItem> usages)
        {
            usages = UsageMerger.Merge(usages);
            var dto = MapToDto(usages);

            Log.Information("正在上传 {Count} 条使用记录...", usages.Count);
            var result = await apiClient.UploadUsageAsync(dto);
            if (!result.Success)
            {
                Log.Information("{Count} 条记录已缓存到本地", usages.Count);
                cache.Add(usages);
                return;
            }
            Log.Information("上传成功，共 {Count} 条记录", usages.Count);
        }

        public async Task UploadCachedAsync()
        {
            var cached = cache.Load();
            if (cached.Count == 0) return;

            cached = UsageMerger.Merge(cached);
            Log.Information("发现 {Count} 条缓存记录（合并后），尝试上传...", cached.Count);
            var dto = MapToDto(cached);

            var result = await apiClient.UploadUsageAsync(dto);
            if (!result.Success) return;

            cache.Clear();
            Log.Information("缓存记录上传成功，已清除本地缓存");
        }
    }
}
