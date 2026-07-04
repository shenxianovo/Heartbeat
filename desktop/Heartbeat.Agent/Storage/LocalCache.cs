using Heartbeat.Core.DTOs.Usage;

namespace Heartbeat.Agent.Storage
{
    /// <summary>
    /// AppUsage 的离线缓存。基于 <see cref="JsonFileCache{T}"/>，
    /// 行为：缩进 JSON、上限 10000 条、纯追加。
    /// 同 Id 快照压缩由上传前的 SnapshotCompaction 负责（ADR-018），缓存不碰业务语义。
    /// </summary>
    public class LocalCache : IUsageCache, IDisposable
    {
        private const int MaxCacheSize = 10000;

        private readonly JsonFileCache<AppUsageItem> _cache;

        public LocalCache(string filePath)
        {
            _cache = new JsonFileCache<AppUsageItem>(
                filePath,
                MaxCacheSize,
                indented: true);
        }

        public void Add(List<AppUsageItem> items) => _cache.Add(items);

        public List<AppUsageItem> Load() => _cache.Load();

        public void Clear() => _cache.Clear();

        public void Dispose() => _cache.Dispose();
    }
}
