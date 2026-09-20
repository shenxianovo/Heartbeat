using System.Runtime.Versioning;
using System.Runtime.InteropServices;

namespace Heartbeat.Collector.Desktop.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsSystemObservationSource : IDesktopObservationSource
{
    private readonly object _gate = new();
    private readonly Dictionary<ObservationCapability, CapabilityObservation> _states = [];
    private WindowsObservationPump? _pump;
    private volatile bool _failed;
    public event Action<DesktopObservation>? Observation;

    public DesktopSnapshot Capture()
    {
        var activity = _failed ? null : ReadForeground();
        lock (_gate) return new DesktopSnapshot(activity, _states.Values.ToArray());
    }

    public void StartObserving()
    {
        if (_pump is not null) return;
        _failed = false;
        _pump = new WindowsObservationPump(Publish, () => Publish(new DesktopObservation.Activity(Capture().Activity)), Failed);
        _pump.Start();
    }

    public void RefreshCapabilities() { }

    private DesktopActivitySample? ReadForeground()
    {
        var window = WindowsNative.GetForegroundWindow();
        _ = WindowsNative.GetWindowThreadProcessId(window, out var processId);
        var process = WindowsNative.OpenProcess(0x1000, false, processId);
        try
        {
            var path = new char[32768];
            uint length = (uint)path.Length;
            var available = process != 0 && WindowsNative.QueryFullProcessImageName(process, 0, path, ref length);
            State(ObservationCapability.Application, available);
            if (!available)
            {
                State(ObservationCapability.WindowTitle, false);
                return null;
            }
            var title = new char[4096];
            Marshal.SetLastPInvokeError(0);
            var titleLength = WindowsNative.GetWindowText(window, title, title.Length);
            var titleAvailable = titleLength > 0 || Marshal.GetLastPInvokeError() == 0;
            State(ObservationCapability.WindowTitle, titleAvailable);
            var executable = new string(path, 0, (int)length);
            return new DesktopActivitySample(new ForegroundApplication("windows", "executable_path",
                executable.ToLowerInvariant(), Path.GetFileNameWithoutExtension(executable)), titleAvailable ? new string(title, 0, titleLength) : null);
        }
        finally { if (process != 0) WindowsNative.CloseHandle(process); }
    }

    private void State(ObservationCapability capability, bool available) => Publish(new DesktopObservation.Capability(
        new CapabilityObservation(capability, available ? ObservationState.Available : ObservationState.Unavailable,
            available ? null : "foreground_unavailable")));

    private void Publish(DesktopObservation observation)
    {
        if (_failed && observation is DesktopObservation.Input) return;
        if (observation is DesktopObservation.Capability capability)
        {
            lock (_gate)
            {
                if (_states.TryGetValue(capability.Value.Capability, out var previous) && previous == capability.Value) return;
                _states[capability.Value.Capability] = capability.Value;
            }
        }
        Observation?.Invoke(observation);
    }

    private void Failed()
    {
        _failed = true;
        foreach (var capability in Enum.GetValues<ObservationCapability>())
            Publish(new DesktopObservation.Capability(new CapabilityObservation(capability, ObservationState.Unavailable, "observer_failed")));
        Observation?.Invoke(new DesktopObservation.Activity(null));
    }

    public void StopObserving()
    {
        _pump?.Dispose();
        _pump = null;
    }
    public void Dispose() => StopObserving();
}
