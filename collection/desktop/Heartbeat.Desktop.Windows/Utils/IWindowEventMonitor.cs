using Heartbeat.Collector.System.Observations;

namespace Heartbeat.Desktop.Windows.Utils;

/// <summary>
/// Windows 窗口 adapter 的内部 seam。它已经把 WinEvent 类型与 HWND 比较翻译成
/// system Collector 的语义观察，组合 adapter 只负责再合入 away 信号。
/// </summary>
public interface IWindowEventMonitor
{
    event Action<DesktopObservation>? Observation;
    DesktopActivity CurrentActivity { get; }
    void Start();
    void Stop();
}
