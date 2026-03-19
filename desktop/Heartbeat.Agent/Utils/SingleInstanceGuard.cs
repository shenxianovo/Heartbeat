using Serilog;

namespace Heartbeat.Agent.Utils
{
    /// <summary>
    /// 单实例守卫：确保同一时刻只有一个 Heartbeat 客户端在运行。
    /// WPF 和 Console Runner 都通过 Agent 注册服务，因此在 Agent 层统一处理。
    /// </summary>
    public sealed class SingleInstanceGuard : IDisposable
    {
        private const string MutexName = @"Global\Heartbeat.Agent.SingleInstance";
        private Mutex? _mutex;

        /// <summary>
        /// 是否成功获取单实例锁（即当前为第一个实例）
        /// </summary>
        public bool IsFirstInstance { get; }

        public SingleInstanceGuard()
        {
            try
            {
                _mutex = new Mutex(true, MutexName, out bool createdNew);
                IsFirstInstance = createdNew;

                if (!createdNew)
                {
                    Log.Warning("检测到另一个 Heartbeat 实例正在运行");
                    _mutex.Dispose();
                    _mutex = null;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "创建单实例互斥锁失败，允许继续运行");
                IsFirstInstance = true;
            }
        }

        public void Dispose()
        {
            if (_mutex != null)
            {
                try { _mutex.ReleaseMutex(); } catch { }
                _mutex.Dispose();
                _mutex = null;
            }
        }
    }
}
