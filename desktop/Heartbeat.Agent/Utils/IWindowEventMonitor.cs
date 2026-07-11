namespace Heartbeat.Agent.Utils
{
    /// <summary>
    /// 前台窗口事件监视 seam。生产实现 WindowsWindowEventMonitor 自持钩子线程：
    /// Start 立即返回，Stop 阻塞收尾（三个 Win32 消息泵组件的统一形态）。
    /// </summary>
    public interface IWindowEventMonitor
    {
        event Action<ForegroundWindow>? ForegroundWindowChanged;
        ForegroundWindow GetForegroundWindow();
        void Start();
        void Stop();
    }
}
