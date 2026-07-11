using Heartbeat.Agent.Http;
using Heartbeat.Agent.Storage;
using Serilog;

namespace Heartbeat.Agent.Services
{
    /// <summary>
    /// 上传流（Upload Stream，ADR-020/022）：绑定出网源的泛化出网流。
    /// drain 一轮 = 先重传离线缓存，再取 fresh 出网——送达，或落离线缓存，
    /// 否则重注入源。"drain 后的批不静默蒸发"是流自持的不变量（ADR-022）。
    /// compact 为按流策略，只作用于出缓存的批：缓存纯追加，离线期间积累同 Id 快照；
    /// fresh 批来自按 Id 键控的 buffer，天然无重复。
    /// </summary>
    public class UploadStream<T>(
        string label,
        IUploadSource<T> source,
        Func<List<T>, Task<ApiResult>> send,
        ICache<T> cache,
        Func<List<T>, List<T>>? compactCached = null)
    {
        /// <summary>
        /// drain 一轮。返回本轮从源取走的 fresh 批（无论送达/缓存/重注入，调用方只读——
        /// 图标挂点从中提 AppName，与 ADR-020 §6 行为一致）。
        /// </summary>
        public async Task<List<T>> DrainAsync()
        {
            await UploadCachedAsync();

            var items = source.Drain();
            if (items.Count == 0) return items;

            Log.Information("正在上传 {Count} 条{Label}...", items.Count, label);
            var result = await send(items);
            if (result.Success)
            {
                Log.Information("{Label}上传成功，共 {Count} 条", label, items.Count);
                return items;
            }

            try
            {
                cache.Add(items);
                Log.Information("{Count} 条{Label}已缓存到本地", items.Count, label);
            }
            catch (Exception ex)
            {
                // 缓存写盘失败（磁盘满等）：不吞数据，重注入源，下轮重试。
                Log.Warning(ex, "{Label}缓存写入失败，{Count} 条重注入源 buffer", label, items.Count);
                source.Reinject(items);
            }
            return items;
        }

        /// <summary>重传离线缓存。成功清空缓存，失败原样保留（下轮再试，ADR-008）。</summary>
        private async Task UploadCachedAsync()
        {
            var cached = cache.Load();
            if (cached.Count == 0) return;

            var toSend = compactCached?.Invoke(cached) ?? cached;

            Log.Information("发现 {Count} 条缓存{Label}，尝试上传...", toSend.Count, label);
            var result = await send(toSend);
            if (!result.Success) return;

            cache.Clear();
            Log.Information("缓存{Label}上传成功，已清除本地缓存", label);
        }
    }
}
