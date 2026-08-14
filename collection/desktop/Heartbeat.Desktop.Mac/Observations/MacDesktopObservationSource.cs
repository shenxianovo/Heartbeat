using Heartbeat.Collector.System.Observations;
using Heartbeat.Desktop.Mac.Icons;

namespace Heartbeat.Desktop.Mac.Observations;

public enum MacAwayReason
{
    ScreenLocked,
    SessionInactive,
    DisplaySleep,
    SystemSleep,
}

public interface IMacDesktopEvents
{
    event Action? ApplicationActivated;
    event Action<MacAwayReason>? AwayEntered;
    event Action<MacAwayReason>? AwayExited;

    MacApplication? FrontmostApplication { get; }

    void Start();
    void Stop();
}

/// <summary>
/// macOS adapter：把 workspace 的 App 激活、session/display/system 硬信号翻译为
/// system Collector 的 App-only 语义。重叠硬信号合并为一个 away span。
/// </summary>
public sealed class MacDesktopObservationSource : IDesktopObservationSource
{
    private readonly IMacDesktopEvents _events;
    private readonly MacApplicationCatalog _catalog;
    private readonly object _gate = new();
    private readonly HashSet<MacAwayReason> _awayReasons = [];
    private bool _started;

    public MacDesktopObservationSource(IMacDesktopEvents events)
        : this(events, new MacApplicationCatalog()) { }

    public MacDesktopObservationSource(IMacDesktopEvents events, MacApplicationCatalog catalog)
    {
        _events = events;
        _catalog = catalog;
    }

    public event Action<DesktopObservation>? Observation;

    public DesktopActivity CurrentActivity
    {
        get
        {
            var application = _events.FrontmostApplication;
            var activity = MacApplicationIdentity.ToActivity(application);
            _catalog.Observe(activity.AppIdentityKey, application);
            return activity;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;
        }

        _events.ApplicationActivated += OnApplicationActivated;
        _events.AwayEntered += OnAwayEntered;
        _events.AwayExited += OnAwayExited;
        _events.Start();
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_started) return;
            _started = false;
            _awayReasons.Clear();
        }

        _events.ApplicationActivated -= OnApplicationActivated;
        _events.AwayEntered -= OnAwayEntered;
        _events.AwayExited -= OnAwayExited;
        _events.Stop();
    }

    private void OnApplicationActivated()
    {
        lock (_gate)
        {
            if (_awayReasons.Count != 0) return;
        }
        Observation?.Invoke(DesktopObservation.AppActivated(CurrentActivity));
    }

    private void OnAwayEntered(MacAwayReason reason)
    {
        bool entered;
        lock (_gate)
        {
            entered = _awayReasons.Count == 0;
            _awayReasons.Add(reason);
        }
        if (entered)
            Observation?.Invoke(DesktopObservation.EnteredAway());
    }

    private void OnAwayExited(MacAwayReason reason)
    {
        bool exited;
        lock (_gate)
        {
            var removed = _awayReasons.Remove(reason);
            exited = removed && _awayReasons.Count == 0;
        }
        if (exited)
            Observation?.Invoke(DesktopObservation.ExitedAway(CurrentActivity));
    }
}
