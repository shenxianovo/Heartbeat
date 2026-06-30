using System.Text.Json;

namespace Heartbeat.Agent.Storage
{
    /// <summary>
    /// 线程安全、原子写盘、容量受限的 JSON 列表持久化。
    /// 捕获两个离线缓存（usage / input event）的全部公共机制：
    /// ReaderWriterLockSlim 并发控制、temp-swap 原子写、容量裁剪（丢最旧）、
    /// 失败回滚、加载容错。不认识任何业务语义。
    /// </summary>
    public class JsonFileCache<T> : IDisposable
    {
        private readonly string _filePath;
        private readonly int _maxItems;
        private readonly bool _indented;
        private readonly ReaderWriterLockSlim _lock = new();
        private List<T> _cache;

        public JsonFileCache(
            string filePath,
            int maxItems,
            bool indented = false)
        {
            _filePath = filePath;
            _maxItems = maxItems;
            _indented = indented;
            _cache = LoadInternal();
        }

        public void Add(List<T> items)
        {
            if (items == null || items.Count == 0) return;

            _lock.EnterWriteLock();
            try
            {
                var snapshot = new List<T>(_cache);
                _cache.AddRange(items);

                if (_cache.Count > _maxItems)
                    _cache = _cache.GetRange(_cache.Count - _maxItems, _maxItems);

                try
                {
                    SaveInternal();
                }
                catch
                {
                    _cache = snapshot;
                    throw;
                }
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        public List<T> Load()
        {
            _lock.EnterReadLock();
            try
            {
                return new List<T>(_cache);
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
                var snapshot = new List<T>(_cache);
                _cache.Clear();

                try
                {
                    SaveInternal();
                }
                catch
                {
                    _cache = snapshot;
                    throw;
                }
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        private void SaveInternal()
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = _indented
                ? JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true })
                : JsonSerializer.Serialize(_cache);

            var tempPath = _filePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _filePath, overwrite: true);
        }

        private List<T> LoadInternal()
        {
            if (!File.Exists(_filePath)) return [];
            try
            {
                var json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<List<T>>(json) ?? [];
            }
            catch
            {
                return [];
            }
        }

        public void Dispose()
        {
            _lock.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
