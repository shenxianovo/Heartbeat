using Heartbeat.Collector.System.Observations;

namespace Heartbeat.Desktop.Windows.Utils;

/// <summary>
/// Windows composition adapter：把 WinEvent 窗口观察与电源/显示硬信号合成 system Collector
/// 的单一语义事件流。system Collector 因此不依赖 Win32 回调形状或电源通知名称。
/// </summary>
public sealed class WindowsDesktopObservationSource(
    IWindowEventMonitor windows,
    IPowerMonitor power) : IDesktopObservationSource
{
    public event Action<DesktopObservation>? Observation;

    public DesktopActivity CurrentActivity => windows.CurrentActivity;

    public void Start()
    {
        windows.Observation += OnWindowObservation;
        power.DisplayOff += OnEnteredAway;
        power.Suspend += OnEnteredAway;
        power.DisplayOn += OnExitedAway;
        power.Resume += OnExitedAway;
        windows.Start();
        power.Start();
    }

    public void Stop()
    {
        windows.Observation -= OnWindowObservation;
        power.DisplayOff -= OnEnteredAway;
        power.Suspend -= OnEnteredAway;
        power.DisplayOn -= OnExitedAway;
        power.Resume -= OnExitedAway;
        windows.Stop();
        power.Stop();
    }

    private void OnWindowObservation(DesktopObservation observation)
        => Observation?.Invoke(observation);

    private void OnEnteredAway()
        => Observation?.Invoke(DesktopObservation.EnteredAway());

    private void OnExitedAway()
        => Observation?.Invoke(DesktopObservation.ExitedAway(windows.CurrentActivity));
}
