using Heartbeat.Collector.System.Observations;
using Heartbeat.Desktop.Windows.Configuration;

namespace Heartbeat.Desktop.Windows.Utils;

/// <summary>
/// Windows composition adapter：把 WinEvent 窗口观察与电源/显示硬信号合成 system Collector
/// 的单一语义事件流。system Collector 因此不依赖 Win32 回调形状或电源通知名称。
/// </summary>
public sealed class WindowsDesktopObservationSource(
    IWindowEventMonitor windows,
    IPowerMonitor power,
    IWindowActivityCollectionPolicy windowActivity) : IDesktopObservationSource
{
    private bool _started;
    public event Action<DesktopObservation>? Observation;

    public DesktopActivity CurrentActivity => Present(windows.CurrentActivity);

    public void Start()
    {
        windows.Observation += OnWindowObservation;
        windowActivity.Changed += OnWindowActivityChanged;
        power.DisplayOff += OnEnteredAway;
        power.Suspend += OnEnteredAway;
        power.DisplayOn += OnExitedAway;
        power.Resume += OnExitedAway;
        _started = true;
        windows.Start();
        power.Start();
    }

    public void Stop()
    {
        windows.Observation -= OnWindowObservation;
        windowActivity.Changed -= OnWindowActivityChanged;
        power.DisplayOff -= OnEnteredAway;
        power.Suspend -= OnEnteredAway;
        power.DisplayOn -= OnExitedAway;
        power.Resume -= OnExitedAway;
        _started = false;
        windows.Stop();
        power.Stop();
    }

    private void OnWindowObservation(DesktopObservation observation)
    {
        if (windowActivity.Enabled)
        {
            Observation?.Invoke(observation);
            return;
        }

        if (observation.Kind == DesktopObservationKind.AppActivated)
            Observation?.Invoke(observation with { Activity = Present(observation.Activity) });
    }

    private void OnWindowActivityChanged(bool enabled)
    {
        if (_started)
            Observation?.Invoke(DesktopObservation.FocusedWindowChanged(CurrentActivity));
    }

    private void OnEnteredAway()
        => Observation?.Invoke(DesktopObservation.EnteredAway());

    private void OnExitedAway()
        => Observation?.Invoke(DesktopObservation.ExitedAway(CurrentActivity));

    private DesktopActivity Present(DesktopActivity activity) =>
        windowActivity.Enabled ? activity : activity with { Title = null };
}
