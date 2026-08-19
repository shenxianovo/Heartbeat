namespace Heartbeat.Server.Services
{
    /// <summary>
    /// 同一 (Owner, 日窗口) 的生成互斥（ADR-042 §7）：进程内一把锁，撞上的请求不排队、直接 409。
    /// 单实例部署（compose 无 replicas），分布式锁是纯负债；不做 fan-out 让多个客户端跟随同一条
    /// 流——为"同时开两个标签页看同一天"付一个广播器不成比例。
    /// </summary>
    public sealed class RecapGenerationLock
    {
        private readonly HashSet<(string OwnerId, DateTimeOffset WindowStart)> _active = [];

        /// <summary>拿到租约表示这条流独占该日；已有生成在跑则返回 null，由端点映射为 409。</summary>
        public IDisposable? TryAcquire(string ownerId, DateTimeOffset windowStart)
        {
            var key = (ownerId, windowStart);
            lock (_active)
            {
                if (!_active.Add(key)) return null;
            }
            return new Lease(this, key);
        }

        private void Release((string OwnerId, DateTimeOffset WindowStart) key)
        {
            lock (_active)
            {
                _active.Remove(key);
            }
        }

        private sealed class Lease(RecapGenerationLock owner, (string OwnerId, DateTimeOffset WindowStart) key)
            : IDisposable
        {
            private bool _released;

            public void Dispose()
            {
                if (_released) return;
                _released = true;
                owner.Release(key);
            }
        }
    }
}
