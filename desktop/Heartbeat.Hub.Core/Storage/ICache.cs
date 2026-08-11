namespace Heartbeat.Hub.Core.Storage
{
    /// <summary>
    /// 离线缓存 seam（ADR-020）：上传通道的持久化侧。
    /// 生产 adapter 是 <see cref="JsonFileCache{T}"/>；测试用 fake 驱动通道契约。
    /// </summary>
    public interface ICache<T>
    {
        /// <summary>追加一批。写盘失败时抛出——调用方（上传通道）负责把批退回。</summary>
        void Add(List<T> items);
        List<T> Load();
        void Clear();
    }
}
