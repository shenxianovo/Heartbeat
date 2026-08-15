using Heartbeat.Collector.System.Input;
using Heartbeat.Collection.Hub.Configuration;
using Heartbeat.Desktop.Mac.Configuration;
using Heartbeat.Desktop.Mac.Native;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Heartbeat.Desktop.Mac.Input;

public sealed class MacInputEventCollector :
    IHostedService,
    IMacInputMonitoringEvents,
    IInputEventRecordingPolicy,
    IDisposable
{
    private static readonly TimeSpan PermissionPollInterval = TimeSpan.FromSeconds(1);
    private const string InputMonitoringSettingsUrl =
        "x-apple.systempreferences:com.apple.preference.security?Privacy_ListenEvent";

    private readonly MacConfigManager _config;
    private readonly IMacInputMonitoringNative _native;
    private readonly IMacCommandRunner _commandRunner;
    private readonly IInputActivitySignal _inputActivity;
    private readonly InputEventBuffer _buffer;
    private readonly object _gate = new();
    private MacInputMonitoringCapabilityState _capabilityState;
    private bool _started;
    private bool _hookRunning;
    private bool _lastRecordingEnabled;
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private event Action<bool>? RecordingChanged;

    public MacInputEventCollector(
        MacConfigManager config,
        IMacInputMonitoringNative native,
        IMacCommandRunner commandRunner,
        IInputActivitySignal inputActivity,
        InputEventBuffer buffer)
    {
        _config = config;
        _native = native;
        _commandRunner = commandRunner;
        _inputActivity = inputActivity;
        _buffer = buffer;
        _capabilityState = ComputeState();
        _lastRecordingEnabled = config.Current.InputEventRecordingEnabled;
    }

    public event Action<MacInputMonitoringCapabilityState>? CapabilityChanged;

    public MacInputMonitoringCapabilityState CapabilityState
    {
        get
        {
            lock (_gate)
                return _capabilityState;
        }
    }

    public bool Enabled => _config.Current.InputEventRecordingEnabled;

    event Action<bool>? IInputEventRecordingPolicy.Changed
    {
        add => RecordingChanged += value;
        remove => RecordingChanged -= value;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_started) return Task.CompletedTask;
            _started = true;
        }

        _config.ConfigChanged += OnConfigChanged;
        _native.Observation += OnNativeObservation;
        RefreshCapability();
        _pollCts = new CancellationTokenSource();
        _pollTask = Task.Run(() => PollPermissionAsync(_pollCts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
            _started = false;
        _pollCts?.Cancel();
        if (_pollTask != null && _pollTask.Id != Task.CurrentId)
        {
            try
            {
                _pollTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException exception) when (
                exception.InnerExceptions.All(inner => inner is OperationCanceledException))
            {
                // normal shutdown
            }
        }
        _pollTask = null;
        _pollCts?.Dispose();
        _pollCts = null;
        _config.ConfigChanged -= OnConfigChanged;
        _native.Observation -= OnNativeObservation;
        StopHook();
        return Task.CompletedTask;
    }

    public void SetInteractionSignalEnabledFromUser(bool enabled)
    {
        _config.Update(value => value.InteractionSignalEnabled = enabled);
        if (enabled && !_native.IsAuthorized)
            _native.RequestAuthorization();
        RefreshCapability();
    }

    public void SetInputEventRecordingEnabledFromUser(bool enabled)
    {
        _config.Update(value => value.InputEventRecordingEnabled = enabled);
        if (enabled && !_native.IsAuthorized)
            _native.RequestAuthorization();
        RefreshCapability();
    }

    public void OpenPermissionSettingsFromUser()
    {
        var result = _commandRunner.Run("/usr/bin/open", [InputMonitoringSettingsUrl]);
        if (result.ExitCode != 0)
            Log.Warning("打开 macOS Input Monitoring 设置失败: {Error}", result.StandardError);
        RefreshCapability();
    }

    public void RefreshPermission() => RefreshCapability();

    private async Task PollPermissionAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PermissionPollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
                RefreshCapability();
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    private void OnConfigChanged(MacAgentConfig value)
    {
        if (_lastRecordingEnabled != value.InputEventRecordingEnabled)
        {
            _lastRecordingEnabled = value.InputEventRecordingEnabled;
            if (!value.InputEventRecordingEnabled)
                _buffer.ResetTransientState();
            RecordingChanged?.Invoke(value.InputEventRecordingEnabled);
        }
        RefreshCapability();
    }

    private void RefreshCapability()
    {
        var current = ComputeState();
        MacInputMonitoringCapabilityState previous;
        lock (_gate)
        {
            previous = _capabilityState;
            _capabilityState = current;
        }

        ReconcileHook(current);
        if (previous != current)
            CapabilityChanged?.Invoke(current);
    }

    private MacInputMonitoringCapabilityState ComputeState()
    {
        var config = _config.Current;
        if (!config.InteractionSignalEnabled && !config.InputEventRecordingEnabled)
            return MacInputMonitoringCapabilityState.Disabled;
        if (!_native.IsAvailable)
            return MacInputMonitoringCapabilityState.Unavailable;
        return _native.IsAuthorized
            ? MacInputMonitoringCapabilityState.Available
            : MacInputMonitoringCapabilityState.PermissionRequired;
    }

    private void ReconcileHook(MacInputMonitoringCapabilityState state)
    {
        var config = _config.Current;
        bool started;
        lock (_gate)
            started = _started;
        var shouldRun = started
            && state == MacInputMonitoringCapabilityState.Available
            && (config.InputEventRecordingEnabled
                || (config.InteractionSignalEnabled && config.WindowTitleObservationEnabled));

        bool startHook = false;
        bool stopHook = false;
        lock (_gate)
        {
            if (shouldRun && !_hookRunning)
            {
                _hookRunning = true;
                startHook = true;
            }
            else if (!shouldRun && _hookRunning)
            {
                _hookRunning = false;
                stopHook = true;
            }
        }

        if (startHook) _native.StartListening();
        if (stopHook) _native.StopListening();
    }

    private void StopHook()
    {
        lock (_gate)
        {
            if (!_hookRunning) return;
            _hookRunning = false;
        }
        _native.StopListening();
    }

    private void OnNativeObservation(MacInputObservation observation)
    {
        var config = _config.Current;
        switch (observation.Kind)
        {
            case MacInputObservationKind.KeyDown when config.InputEventRecordingEnabled:
                if (MacKeyPositionMapper.TryMap((ushort)observation.Value, out var downPosition))
                    _buffer.OnKeyDown(downPosition);
                break;
            case MacInputObservationKind.KeyUp:
                if (MacKeyPositionMapper.TryMap((ushort)observation.Value, out var upPosition))
                    _buffer.OnKeyUp(upPosition);
                break;
            case MacInputObservationKind.MouseButton:
                if (config.InteractionSignalEnabled && config.WindowTitleObservationEnabled)
                    _inputActivity.MarkClick();
                if (config.InputEventRecordingEnabled)
                    _buffer.OnMouseButton((short)observation.Value);
                break;
            case MacInputObservationKind.Scroll when config.InputEventRecordingEnabled:
                _buffer.OnScroll(observation.Value);
                break;
        }
    }

    public void Dispose()
    {
        _ = StopAsync(CancellationToken.None);
        GC.SuppressFinalize(this);
    }
}
