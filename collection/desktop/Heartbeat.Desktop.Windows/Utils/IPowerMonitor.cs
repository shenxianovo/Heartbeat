namespace Heartbeat.Desktop.Windows.Utils
{
    /// <summary>
    /// 电源/显示状态监视器。在专用线程上创建 message-only 窗口接收 WM_POWERBROADCAST，
    /// 将显示器开关与系统睡眠/唤醒翻译为语义事件。详见 ADR-014。
    /// 实现需保持与 ILowLevelInputHook / IWindowEventMonitor 一致的自建消息泵模式。
    /// </summary>
    public interface IPowerMonitor
    {
        /// <summary>显示器熄灭（电源超时 / 手动息屏 / 锁屏或睡眠伴随的熄屏）。</summary>
        event Action? DisplayOff;

        /// <summary>显示器点亮（人回来）。</summary>
        event Action? DisplayOn;

        /// <summary>系统即将进入睡眠/休眠（进程随后被挂起）。</summary>
        event Action? Suspend;

        /// <summary>系统从睡眠/休眠唤醒。</summary>
        event Action? Resume;

        void Start();
        void Stop();
    }
}
