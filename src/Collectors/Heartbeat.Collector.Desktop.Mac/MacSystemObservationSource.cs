using Heartbeat.Collector.Desktop.Mac.Native;

namespace Heartbeat.Collector.Desktop.Mac;

internal sealed class MacSystemObservationSource : IMacSystemObservationSource
{
    private readonly IMacWorkspaceNative _workspace;
    private readonly IMacAccessibilityNative _accessibility;
    private readonly IMacInputMonitoringNative _input;
    private readonly object _gate = new();
    private readonly Dictionary<ObservationCapability, CapabilityObservation> _states = [];
    private bool _started;
    private bool _disposed;

    public MacSystemObservationSource()
        : this(new CocoaWorkspaceNative(), new MacAccessibilityNative(), new MacInputMonitoringNative())
    {
    }

    internal MacSystemObservationSource(
        IMacWorkspaceNative workspace,
        IMacAccessibilityNative accessibility,
        IMacInputMonitoringNative input)
    {
        _workspace = workspace;
        _accessibility = accessibility;
        _input = input;
    }

    public event Action<MacSystemObservation>? Observation;

    public MacSystemSnapshot Capture()
    {
        var application = _workspace.FrontmostApplication;
        var activity = ToActivity(application);
        lock (_gate)
        {
            return new MacSystemSnapshot(activity, _states.Values.ToArray());
        }
    }

    public void StartObserving()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_started)
            {
                return;
            }
            _started = true;
        }

        _workspace.Notification += OnWorkspaceNotification;
        _accessibility.Observation += OnAccessibilityObservation;
        _accessibility.Failed += OnAccessibilityFailure;
        _input.Observation += OnInputObservation;
        _input.Failed += OnInputFailure;
        _workspace.Start(MacWorkspaceNotification.All);
        RefreshCapabilities();
    }

    public void StopObserving()
    {
        lock (_gate)
        {
            if (!_started)
            {
                return;
            }
            _started = false;
        }

        _workspace.Notification -= OnWorkspaceNotification;
        _accessibility.Observation -= OnAccessibilityObservation;
        _accessibility.Failed -= OnAccessibilityFailure;
        _input.Observation -= OnInputObservation;
        _input.Failed -= OnInputFailure;
        TryStopAccessibility();
        TryStopInput();
        try
        {
            _workspace.StopNotifications();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"macOS workspace observation did not stop cleanly: {exception.Message}");
        }
    }

    public void RefreshCapabilities()
    {
        var application = _workspace.FrontmostApplication;
        if (_accessibility.IsAvailable && _accessibility.IsProcessTrusted)
        {
            var observing = application?.ProcessIdentifier > 0
                && TryObserveApplication(application.ProcessIdentifier);
            if (observing)
            {
                _ = ReadTitle(application!);
            }
            else if (application?.ProcessIdentifier is not > 0)
            {
                PublishState(new CapabilityObservation(
                    ObservationCapability.WindowTitle, ObservationState.Unavailable, "frontmost_process_unavailable"));
            }
        }
        else
        {
            var stopped = TryStopAccessibility();
            PublishState(stopped
                ? new CapabilityObservation(
                    ObservationCapability.WindowTitle,
                    _accessibility.IsAvailable ? ObservationState.PermissionRequired : ObservationState.Unavailable,
                    _accessibility.IsAvailable ? "accessibility" : "accessibility_unavailable")
                : new CapabilityObservation(
                    ObservationCapability.WindowTitle, ObservationState.Unavailable, "observer_stop_failed"));
        }

        if (_input.IsAvailable && _input.IsAuthorized)
        {
            if (TryStartInput())
            {
                PublishState(new CapabilityObservation(ObservationCapability.Input, ObservationState.Available));
            }
        }
        else
        {
            var stopped = TryStopInput();
            PublishState(stopped
                ? new CapabilityObservation(
                    ObservationCapability.Input,
                    _input.IsAvailable ? ObservationState.PermissionRequired : ObservationState.Unavailable,
                    _input.IsAvailable ? "input_monitoring" : "input_monitoring_unavailable")
                : new CapabilityObservation(
                    ObservationCapability.Input, ObservationState.Unavailable, "observer_stop_failed"));
        }
    }

    private DesktopActivitySample? ToActivity(MacApplication? application)
    {
        if (application is null)
        {
            PublishState(new CapabilityObservation(
                ObservationCapability.Application, ObservationState.Unavailable, "frontmost_application_unavailable"));
            return null;
        }

        var (kind, id) = !string.IsNullOrWhiteSpace(application.BundleIdentifier)
            ? ("bundle_id", application.BundleIdentifier.Trim())
            : !string.IsNullOrWhiteSpace(application.ExecutablePath)
                ? ("executable_path", application.ExecutablePath.Trim())
                : (string.Empty, string.Empty);
        if (id.Length == 0)
        {
            PublishState(new CapabilityObservation(
                ObservationCapability.Application, ObservationState.Unavailable, "application_identity_unavailable"));
            return null;
        }

        string? title = null;
        if (_accessibility.IsAvailable && _accessibility.IsProcessTrusted && application.ProcessIdentifier > 0)
        {
            title = ReadTitle(application);
        }

        PublishState(new CapabilityObservation(ObservationCapability.Application, ObservationState.Available));
        return new DesktopActivitySample(
            new ForegroundApplication("macos", kind, id, application.DisplayName),
            string.IsNullOrWhiteSpace(title) ? null : title);
    }

    private string? ReadTitle(MacApplication application)
    {
        try
        {
            var title = _accessibility.ReadFocusedWindowTitle(application.ProcessIdentifier);
            // Starting an asynchronous subscription is not proof it attached successfully, so a read
            // alone cannot claim recovery. While the handshake is still running the capability keeps
            // its last published state: attaching is no more evidence of failure than of success, and
            // reporting it as unavailable would announce a fresh outage on every application switch.
            if (_accessibility.IsObservingApplication(application.ProcessIdentifier))
            {
                PublishState(new CapabilityObservation(ObservationCapability.WindowTitle, ObservationState.Available));
            }

            return title;
        }
        catch (Exception exception)
        {
            OnAccessibilityFailure(exception);
            return null;
        }
    }

    private void OnWorkspaceNotification(string name)
    {
        switch (name)
        {
            case MacWorkspaceNotification.ApplicationActivated:
                Observation?.Invoke(new MacSystemObservation.Activity(
                    ToActivity(_workspace.FrontmostApplication)));
                RefreshCapabilities();
                break;
            case MacWorkspaceNotification.ScreenLocked:
                Observation?.Invoke(new MacSystemObservation.AwayEntered(MacAwayReason.ScreenLocked));
                break;
            case MacWorkspaceNotification.ScreenUnlocked:
                Resume(MacAwayReason.ScreenLocked);
                break;
            case MacWorkspaceNotification.SessionInactive:
                Observation?.Invoke(new MacSystemObservation.AwayEntered(MacAwayReason.SessionInactive));
                break;
            case MacWorkspaceNotification.SessionActive:
                Resume(MacAwayReason.SessionInactive);
                break;
            case MacWorkspaceNotification.DisplaySleep:
                Observation?.Invoke(new MacSystemObservation.AwayEntered(MacAwayReason.DisplaySleep));
                break;
            case MacWorkspaceNotification.DisplayWake:
                Resume(MacAwayReason.DisplaySleep);
                break;
            case MacWorkspaceNotification.SystemSleep:
                Observation?.Invoke(new MacSystemObservation.AwayEntered(MacAwayReason.SystemSleep));
                break;
            case MacWorkspaceNotification.SystemWake:
                Resume(MacAwayReason.SystemSleep);
                break;
        }
    }

    private void Resume(MacAwayReason reason)
    {
        RefreshCapabilities();
        Observation?.Invoke(new MacSystemObservation.AwayExited(reason, ToActivity(_workspace.FrontmostApplication)));
    }

    private void OnAccessibilityObservation(MacAccessibilityObservation observation)
    {
        var current = _workspace.FrontmostApplication;
        if (observation.ProcessIdentifier > 0 && observation.ProcessIdentifier != current?.ProcessIdentifier)
        {
            return;
        }
        var activity = ToActivity(current);
        if (activity is not null)
        {
            activity = activity with { WindowTitle = string.IsNullOrWhiteSpace(observation.Title) ? null : observation.Title };
        }
        Observation?.Invoke(new MacSystemObservation.Activity(activity));
    }

    private void OnInputObservation(MacInputObservation observation)
    {
        DesktopInputObservation? translated = observation.Kind switch
        {
            MacInputObservationKind.KeyDown when MacKeyPositionMapper.TryMap((ushort)observation.Value, out var key) =>
                new(DesktopInputKind.KeyDown, (int)key),
            MacInputObservationKind.KeyUp when MacKeyPositionMapper.TryMap((ushort)observation.Value, out var key) =>
                new(DesktopInputKind.KeyUp, (int)key),
            MacInputObservationKind.MouseButton => new(DesktopInputKind.MouseButtonDown, observation.Value),
            MacInputObservationKind.Scroll => new(
                DesktopInputKind.Scroll,
                DeltaX: observation.DeltaX,
                DeltaY: observation.DeltaY,
                ScrollUnit: observation.ScrollUnit),
            _ => null,
        };
        if (translated is not null)
        {
            Observation?.Invoke(new MacSystemObservation.Input(translated));
        }
    }

    private void OnAccessibilityFailure(Exception exception)
    {
        PublishState(new CapabilityObservation(
            ObservationCapability.WindowTitle, ObservationState.Unavailable, "observer_failed"));
        Console.Error.WriteLine($"macOS window-title observation failed; application collection continues: {exception.Message}");
    }

    private void OnInputFailure(Exception exception)
    {
        PublishState(new CapabilityObservation(
            ObservationCapability.Input, ObservationState.Unavailable, "observer_failed"));
        Console.Error.WriteLine($"macOS input observation failed; other collection continues: {exception.Message}");
    }

    private bool TryObserveApplication(int processIdentifier)
    {
        try
        {
            _accessibility.ObserveApplication(processIdentifier);
            return true;
        }
        catch (Exception exception)
        {
            OnAccessibilityFailure(exception);
            return false;
        }
    }

    private bool TryStartInput()
    {
        try
        {
            _input.StartListening();
            return true;
        }
        catch (Exception exception)
        {
            OnInputFailure(exception);
            return false;
        }
    }

    private bool TryStopAccessibility()
    {
        try
        {
            _accessibility.StopObserving();
            return true;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"macOS window-title observer did not stop cleanly: {exception.Message}");
            return false;
        }
    }

    private bool TryStopInput()
    {
        try
        {
            _input.StopListening();
            return true;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"macOS input observer did not stop cleanly: {exception.Message}");
            return false;
        }
    }

    private void PublishState(CapabilityObservation state)
    {
        bool changed;
        lock (_gate)
        {
            changed = !_states.TryGetValue(state.Capability, out var previous) || previous != state;
            _states[state.Capability] = state;
        }
        if (!changed)
        {
            return;
        }

        if (state.State == ObservationState.PermissionRequired)
        {
            Console.Error.WriteLine(state.Capability == ObservationCapability.WindowTitle
                ? "macOS Accessibility permission is required for window titles; application collection continues."
                : "macOS Input Monitoring permission is required for input events; other collection continues.");
        }
        else if (state.State == ObservationState.Available)
        {
            Console.Error.WriteLine($"macOS {state.Capability} observation is available.");
        }
        Observation?.Invoke(new MacSystemObservation.Capability(state));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        StopObserving();
        _disposed = true;
        _workspace.Dispose();
        _accessibility.Dispose();
        _input.Dispose();
    }
}
