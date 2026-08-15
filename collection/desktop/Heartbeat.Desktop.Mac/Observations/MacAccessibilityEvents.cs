using Heartbeat.Desktop.Mac.Configuration;
using Heartbeat.Desktop.Mac.Native;
using Serilog;

namespace Heartbeat.Desktop.Mac.Observations;

public enum MacAccessibilityCapabilityState
{
    Disabled,
    PermissionRequired,
    Available,
    Unavailable,
}

public enum MacAccessibilityObservationKind
{
    FocusedWindowChanged,
    TitleChanged,
}

public readonly record struct MacAccessibilityObservation(
    MacAccessibilityObservationKind Kind,
    string? Title,
    int ProcessIdentifier = 0);

public interface IMacAccessibilityNative
{
    event Action<MacAccessibilityObservation>? Observation;

    bool IsAvailable { get; }
    bool IsProcessTrusted { get; }

    void RequestProcessTrust();
    string? ReadFocusedWindowTitle(int processIdentifier);
    void ObserveApplication(int processIdentifier);
    void StopObserving();
}

public interface IMacAccessibilityEvents
{
    event Action<MacAccessibilityObservation>? Observation;
    event Action<MacAccessibilityCapabilityState>? CapabilityChanged;

    bool Enabled { get; }
    MacAccessibilityCapabilityState CapabilityState { get; }
    string? CurrentTitle { get; }

    void Start();
    void Stop();
    void SetCurrentApplication(int processIdentifier);
    void SetEnabledFromUser(bool enabled);
    void OpenPermissionSettingsFromUser();
}

/// <summary>
/// Accessibility 能力的生命周期边界。配置启用与 TCC 授权分开表示；启动和普通配置更新
/// 只检查权限，只有显式用户动作才请求权限或打开系统设置。
/// </summary>
public sealed class MacAccessibilityEvents(
    MacConfigManager config,
    IMacAccessibilityNative native,
    IMacCommandRunner commandRunner) : IMacAccessibilityEvents, IDisposable
{
    private static readonly TimeSpan PermissionPollInterval = TimeSpan.FromSeconds(1);
    private const string AccessibilitySettingsUrl =
        "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility";

    private readonly object _gate = new();
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private int _currentProcessIdentifier;
    private bool _started;
    private MacAccessibilityCapabilityState _capabilityState = ComputeInitialState(config, native);

    public event Action<MacAccessibilityObservation>? Observation;
    public event Action<MacAccessibilityCapabilityState>? CapabilityChanged;

    public bool Enabled => config.Current.WindowTitleObservationEnabled;

    public MacAccessibilityCapabilityState CapabilityState
    {
        get
        {
            lock (_gate)
                return _capabilityState;
        }
    }

    public string? CurrentTitle
    {
        get
        {
            int processIdentifier;
            lock (_gate)
            {
                if (_capabilityState != MacAccessibilityCapabilityState.Available)
                    return null;
                processIdentifier = _currentProcessIdentifier;
            }

            return processIdentifier <= 0 ? null : ReadTitle(processIdentifier);
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;
        }

        native.Observation += OnNativeObservation;
        RefreshCapability();
        _pollCts?.Dispose();
        _pollCts = new CancellationTokenSource();
        _pollTask = Task.Run(() => PollPermissionAsync(_pollCts.Token), CancellationToken.None);
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_started) return;
            _started = false;
        }

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
                // 正常停止
            }
        }
        _pollTask = null;
        native.Observation -= OnNativeObservation;
        native.StopObserving();
    }

    public void SetCurrentApplication(int processIdentifier)
    {
        var shouldObserve = false;
        lock (_gate)
        {
            _currentProcessIdentifier = processIdentifier;
            shouldObserve = _started
                && processIdentifier > 0
                && _capabilityState == MacAccessibilityCapabilityState.Available;
        }

        if (shouldObserve)
            native.ObserveApplication(processIdentifier);
    }

    public void SetEnabledFromUser(bool enabled)
    {
        config.Update(value => value.WindowTitleObservationEnabled = enabled);
        if (enabled)
            native.RequestProcessTrust();
        RefreshCapability();
    }

    public void OpenPermissionSettingsFromUser()
    {
        if (!Enabled) return;
        var result = commandRunner.Run("/usr/bin/open", [AccessibilitySettingsUrl]);
        if (result.ExitCode != 0)
            Log.Warning("打开 macOS Accessibility 设置失败: {Error}", result.StandardError);
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
            // 正常停止
        }
    }

    private void RefreshCapability()
    {
        MacAccessibilityCapabilityState previous;
        MacAccessibilityCapabilityState current;
        int processIdentifier;
        bool started;

        lock (_gate)
        {
            previous = _capabilityState;
            current = ComputeState();
            _capabilityState = current;
            processIdentifier = _currentProcessIdentifier;
            started = _started;
        }

        if (started && current == MacAccessibilityCapabilityState.Available && processIdentifier > 0)
            native.ObserveApplication(processIdentifier);
        else if (current != MacAccessibilityCapabilityState.Available)
            native.StopObserving();

        if (previous != current)
        {
            Log.Information("macOS Accessibility 能力状态: {State}", current);
            CapabilityChanged?.Invoke(current);
        }
    }

    private MacAccessibilityCapabilityState ComputeState()
    {
        if (!Enabled) return MacAccessibilityCapabilityState.Disabled;
        if (!native.IsAvailable) return MacAccessibilityCapabilityState.Unavailable;
        return native.IsProcessTrusted
            ? MacAccessibilityCapabilityState.Available
            : MacAccessibilityCapabilityState.PermissionRequired;
    }

    private static MacAccessibilityCapabilityState ComputeInitialState(
        MacConfigManager config,
        IMacAccessibilityNative native)
    {
        if (!config.Current.WindowTitleObservationEnabled)
            return MacAccessibilityCapabilityState.Disabled;
        if (!native.IsAvailable)
            return MacAccessibilityCapabilityState.Unavailable;
        return native.IsProcessTrusted
            ? MacAccessibilityCapabilityState.Available
            : MacAccessibilityCapabilityState.PermissionRequired;
    }

    private string? ReadTitle(int processIdentifier)
    {
        try
        {
            return native.ReadFocusedWindowTitle(processIdentifier);
        }
        catch (Exception exception)
        {
            Log.Debug(exception, "读取 macOS focused-window 标题失败");
            return null;
        }
    }

    private void OnNativeObservation(MacAccessibilityObservation observation)
    {
        int currentProcessIdentifier;
        lock (_gate)
        {
            if (!_started || _capabilityState != MacAccessibilityCapabilityState.Available)
                return;
            currentProcessIdentifier = _currentProcessIdentifier;
        }
        if (observation.ProcessIdentifier > 0
            && observation.ProcessIdentifier != currentProcessIdentifier)
            return;
        Observation?.Invoke(observation);
    }

    public void Dispose()
    {
        Stop();
        _pollCts?.Dispose();
        GC.SuppressFinalize(this);
    }
}
