using Heartbeat.Core.DTOs;
using System.Text.Json;

namespace Heartbeat.Agent.Storage
{
    public class LocalCache
    {
        private readonly string _filePath;
        private readonly ReaderWriterLockSlim _lock = new();
        private List<AppUsageItem> _cache;

        /// <summary>
        /// 缓存最大条数限制
        /// </summary>
        private const int MaxCacheSize = 10000;

        public LocalCache(string filePath)
        {
            _filePath = filePath;
            _cache = LoadInternal();
        }

        public void Add(List<AppUsageItem> items)
        {
            if (items == null || items.Count == 0) return;

            _lock.EnterWriteLock();
            try
            {
                _cache.AddRange(items);

                // 超出容量时丢弃最旧的记录
                if (_cache.Count > MaxCacheSize)
                {
                    _cache = _cache.GetRange(_cache.Count - MaxCacheSize, MaxCacheSize);
                }

                SaveInternal();
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        public List<AppUsageItem> Load()
        {
            _lock.EnterReadLock();
            try
            {
                return new List<AppUsageItem>(_cache);
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        public void Clear()
        {
            _lock.EnterWriteLock();
            try
            {
                _cache.Clear();
                SaveInternal();
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        private void SaveInternal()
        {
            try
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true });

                // 原子写入
                var tempPath = _filePath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _filePath, overwrite: true);
            }
            catch { }
        }

        private List<AppUsageItem> LoadInternal()
        {
            if (!File.Exists(_filePath)) return [];
            try
            {
                var json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<List<AppUsageItem>>(json) ?? [];
            }
            catch
            {
                return [];
            }
        }
    }
}
