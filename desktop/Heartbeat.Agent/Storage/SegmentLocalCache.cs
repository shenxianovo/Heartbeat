using Heartbeat.Core.DTOs.Segments;

namespace Heartbeat.Agent.Storage
{
    /// <summary>插件段的离线缓存接缝（与 <see cref="IUsageCache"/> 同构）。</summary>
    public interface ISegmentCache
    {
        void Add(List<ActivitySegmentItem> items);
        List<ActivitySegmentItem> Load();
        void Clear();
    }

    /// <summary>
    /// 插件段的离线缓存。基于 <see cref="JsonFileCache{T}"/>，
    /// 行为：紧凑 JSON、上限 20000 条、纯追加。
    /// 同 Id 快照压缩由上传前的 SnapshotCompaction 负责（ADR-018），服务端按 Id upsert 收敛。
    /// </summary>
    public class SegmentLocalCache : ISegmentCache, IDisposable
    {
        private const int MaxCacheSize = 20000;

        private readonly JsonFileCache<ActivitySegmentItem> _cache;

        public SegmentLocalCache(string filePath)
        {
            _cache = new JsonFileCache<ActivitySegmentItem>(filePath, MaxCacheSize);
        }

        public void Add(List<ActivitySegmentItem> items) => _cache.Add(items);

        public List<ActivitySegmentItem> Load() => _cache.Load();

        public void Clear() => _cache.Clear();

        public void Dispose() => _cache.Dispose();
    }
}
