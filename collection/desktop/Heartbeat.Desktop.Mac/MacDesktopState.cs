using Heartbeat.Desktop.Mac.Configuration;
using Heartbeat.Core;
using Heartbeat.Desktop.UI.Presentation;
using Heartbeat.Collection.Hub.Http;
using Heartbeat.Collection.Hub.Presence;
using Heartbeat.Collection.Hub.Upload;
using Heartbeat.Desktop.Mac.Observations;
using Heartbeat.Desktop.Mac.Input;
using Heartbeat.Desktop.Mac.Native;
using Heartbeat.Collection.Hub.Collectors.Runtime;

namespace Heartbeat.Desktop.Mac;

public sealed class MacDesktopState : IDesktopState, IDisposable
{
    private readonly MacConfigManager _config;
    private readonly ICollectionStatus _collection;
    private readonly IMacLoginStart _loginStart;
    private readonly IClientCompatibilityStatus _compatibility;
    private readonly IUploadStatus _uploads;
    private readonly IMacAccessibilityEvents _accessibility;
    private readonly IMacInputMonitoringEvents _inputMonitoring;
    private readonly IMacApplicationLocator _applicationLocator;
    private readonly IMacBrowserSetupLauncher _browserSetupLauncher;
    private readonly BrowserCollectorRuntime? _browserRuntime;

    public MacDesktopState(
        MacConfigManager config,
        ICollectionStatus collection,
        IMacLoginStart loginStart,
        IClientCompatibilityStatus compatibility,
        IUploadStatus uploads,
        IMacAccessibilityEvents accessibility,
        IMacInputMonitoringEvents inputMonitoring,
        IMacApplicationLocator applicationLocator,
        IMacBrowserSetupLauncher browserSetupLauncher,
        BrowserCollectorRuntime? browserRuntime = null)
    {
        _config = config;
        _collection = collection;
        _loginStart = loginStart;
        _compatibility = compatibility;
        _uploads = uploads;
        _accessibility = accessibility;
        _inputMonitoring = inputMonitoring;
        _applicationLocator = applicationLocator;
        _browserSetupLauncher = browserSetupLauncher;
        _browserRuntime = browserRuntime;
        ReconcileLoginStartRegistration();
        _config.ConfigChanged += OnConfigChanged;
        _collection.CurrentActivityChanged += OnCurrentActivityChanged;
        _compatibility.Changed += OnCompatibilityChanged;
        _uploads.Changed += Publish;
        _accessibility.CapabilityChanged += OnAccessibilityCapabilityChanged;
        _inputMonitoring.CapabilityChanged += OnInputMonitoringCapabilityChanged;
        if (_browserRuntime is not null)
            _browserRuntime.Changed += OnBrowserRuntimeChanged;
    }

    public DesktopStateSnapshot Current => BuildSnapshot();
    public event Action<DesktopStateSnapshot>? Changed;

    public void SaveSettings(DesktopSettingsInput settings) =>
        _config.Update(config =>
        {
            config.ApiKey = settings.ApiKey;
            config.DeviceName = settings.DeviceName;
            config.UploadIntervalMinutes = settings.UploadIntervalMinutes;
        });

    public void SetLoginStartEnabled(bool enabled)
    {
        if (enabled && Environment.ProcessPath is { Length: > 0 } executable)
            _loginStart.Enable(executable);
        else if (!enabled)
            _loginStart.Disable();
        Publish();
    }

    public void SetCollectorEnabled(string source, bool enabled)
    {
        if (string.Equals(source, ActivitySources.Browser, StringComparison.OrdinalIgnoreCase))
        {
            _browserRuntime?.SetAllAppsDesiredEnabled(enabled);
            return;
        }
        _config.Update(config =>
        {
            if (config.Collectors.TryGetValue(source, out var collector))
                collector.Enabled = enabled;
        });
    }

    public void SetBrowserCollectorAppEnabled(string appHint, bool enabled) =>
        _browserRuntime?.SetAppDesiredEnabled(appHint, enabled);

    public void OpenBrowserCollectorSetup(BrowserKind browser)
    {
        var directory = _browserRuntime?.Current.SideloadDirectory;
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("浏览器采集器目录尚未准备好。");
        _browserSetupLauncher.Open(browser, directory);
    }

    public void SetSystemCapabilityEnabled(SystemCapability capability, bool enabled)
    {
        switch (capability)
        {
            case SystemCapability.WindowActivity:
                _accessibility.SetEnabledFromUser(enabled);
                break;
            case SystemCapability.InteractionSignal:
                _inputMonitoring.SetInteractionSignalEnabledFromUser(enabled);
                break;
            case SystemCapability.InputEventRecording:
                _inputMonitoring.SetInputEventRecordingEnabledFromUser(enabled);
                break;
        }
    }

    public void RecoverSystemCapability(SystemCapability capability)
    {
        if (capability is SystemCapability.WindowActivity
            or SystemCapability.InteractionSignal
            or SystemCapability.InputEventRecording)
        {
            // Prepare the exact running app/binary in Finder before System Settings
            // becomes frontmost, so the user can add or drag it into the privacy list.
            _applicationLocator.RevealFromUser();
        }

        if (capability == SystemCapability.WindowActivity)
            _accessibility.OpenPermissionSettingsFromUser();
        else if (capability is SystemCapability.InteractionSignal or SystemCapability.InputEventRecording)
            _inputMonitoring.OpenPermissionSettingsFromUser();
    }

    public void RevealSystemCapabilityApplication(SystemCapability capability)
    {
        if (capability != SystemCapability.ForegroundApp)
            _applicationLocator.RevealFromUser();
    }

    public void SetThemeMode(DesktopThemeMode mode) =>
        _config.Update(config => config.ThemeMode = mode.ToString());

    private void ReconcileLoginStartRegistration()
    {
        if (_loginStart.IsEnabled && Environment.ProcessPath is { Length: > 0 } executable)
            _loginStart.Enable(executable);
    }

    private DesktopStateSnapshot BuildSnapshot()
    {
        var config = _config.Current;
        return new DesktopStateSnapshot(
            _collection.CurrentActivity,
            new DesktopSettingsSnapshot(
                config.ApiKey,
                config.DeviceName,
                config.UploadIntervalMinutes,
                ParseThemeMode(config.ThemeMode)),
            _loginStart.IsEnabled,
            config.Collectors.ToDictionary(
                pair => pair.Key,
                pair => new CollectorRegistrationState(pair.Value.Enabled, pair.Value.FlushPeriodMs),
                StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, DateTimeOffset>(_collection.SourceLastSeen, StringComparer.OrdinalIgnoreCase),
            _compatibility.Current,
            _uploads.Snapshot,
            BuildCapabilities(config),
            MapBrowserRuntime(_browserRuntime?.Current));
    }

    private DesktopCapabilitySnapshot BuildCapabilities(MacAgentConfig config) => new(
        new Dictionary<SystemCapability, SystemCapabilityState>
        {
            [SystemCapability.ForegroundApp] = new(null, CapabilityAvailability.Available),
            [SystemCapability.WindowActivity] = WindowActivityState(config.WindowTitleObservationEnabled),
            [SystemCapability.InteractionSignal] = InteractionSignalState(config),
            [SystemCapability.InputEventRecording] = InputEventRecordingState(config.InputEventRecordingEnabled),
        });

    private SystemCapabilityState WindowActivityState(bool requested) => new(
        requested,
        !requested ? CapabilityAvailability.Available : _accessibility.CapabilityState switch
        {
            MacAccessibilityCapabilityState.Available => CapabilityAvailability.Available,
            MacAccessibilityCapabilityState.PermissionRequired => CapabilityAvailability.PermissionRequired,
            _ => CapabilityAvailability.Unavailable,
        },
        requested && _accessibility.CapabilityState == MacAccessibilityCapabilityState.PermissionRequired,
        requested && _accessibility.CapabilityState == MacAccessibilityCapabilityState.PermissionRequired);

    private SystemCapabilityState InteractionSignalState(MacAgentConfig config) => new(
        config.InteractionSignalEnabled,
        !config.InteractionSignalEnabled
            ? CapabilityAvailability.Available
            : !config.WindowTitleObservationEnabled
                ? CapabilityAvailability.Paused
                : InputMonitoringAvailability(),
        config.InteractionSignalEnabled
            && config.WindowTitleObservationEnabled
            && _inputMonitoring.CapabilityState == MacInputMonitoringCapabilityState.PermissionRequired,
        config.InteractionSignalEnabled
            && config.WindowTitleObservationEnabled
            && _inputMonitoring.CapabilityState == MacInputMonitoringCapabilityState.PermissionRequired);

    private SystemCapabilityState InputEventRecordingState(bool requested) => new(
        requested,
        !requested ? CapabilityAvailability.Available : InputMonitoringAvailability(),
        requested && _inputMonitoring.CapabilityState == MacInputMonitoringCapabilityState.PermissionRequired,
        requested && _inputMonitoring.CapabilityState == MacInputMonitoringCapabilityState.PermissionRequired);

    private CapabilityAvailability InputMonitoringAvailability() => _inputMonitoring.CapabilityState switch
    {
        MacInputMonitoringCapabilityState.Available => CapabilityAvailability.Available,
        MacInputMonitoringCapabilityState.PermissionRequired => CapabilityAvailability.PermissionRequired,
        _ => CapabilityAvailability.Unavailable,
    };

    private static DesktopThemeMode ParseThemeMode(string? value) =>
        Enum.TryParse<DesktopThemeMode>(value, true, out var mode)
            ? mode
            : DesktopThemeMode.System;

    private static BrowserCollectorState? MapBrowserRuntime(BrowserCollectorRuntimeSnapshot? snapshot) =>
        snapshot is null ? null : new BrowserCollectorState(
            snapshot.IsInstalled,
            snapshot.PackageVersion,
            snapshot.PackageContentHash,
            snapshot.InstallDirectory,
            snapshot.SideloadDirectory,
            snapshot.DesiredEnabled,
            snapshot.RuntimeStatus switch
            {
                BrowserCollectorRuntimeStatus.Ready => ExternalHostRuntimeStatus.Ready,
                BrowserCollectorRuntimeStatus.Degraded => ExternalHostRuntimeStatus.Degraded,
                _ => ExternalHostRuntimeStatus.Waiting
            },
            snapshot.RuntimeStatusDetail,
            snapshot.ReloadRequired,
            snapshot.PreviousKnownGoodVersion,
            snapshot.Apps.Select(app => new BrowserCollectorAppState(
                app.AppHint,
                app.DesiredEnabled,
                app.RuntimeStatus switch
                {
                    BrowserCollectorRuntimeStatus.Ready => ExternalHostRuntimeStatus.Ready,
                    BrowserCollectorRuntimeStatus.Degraded => ExternalHostRuntimeStatus.Degraded,
                    _ => ExternalHostRuntimeStatus.Waiting
                },
                app.RuntimeStatusDetail,
                app.PackageVersion)).ToArray());

    private void OnConfigChanged(MacAgentConfig _) => Publish();
    private void OnCurrentActivityChanged(CurrentActivity? _) => Publish();
    private void OnCompatibilityChanged(ClientCompatibilitySnapshot _) => Publish();
    private void OnAccessibilityCapabilityChanged(MacAccessibilityCapabilityState _) => Publish();
    private void OnInputMonitoringCapabilityChanged(MacInputMonitoringCapabilityState _) => Publish();
    private void OnBrowserRuntimeChanged(BrowserCollectorRuntimeSnapshot _) => Publish();
    private void Publish() => Changed?.Invoke(BuildSnapshot());

    public void Dispose()
    {
        _config.ConfigChanged -= OnConfigChanged;
        _collection.CurrentActivityChanged -= OnCurrentActivityChanged;
        _compatibility.Changed -= OnCompatibilityChanged;
        _uploads.Changed -= Publish;
        _accessibility.CapabilityChanged -= OnAccessibilityCapabilityChanged;
        _inputMonitoring.CapabilityChanged -= OnInputMonitoringCapabilityChanged;
        if (_browserRuntime is not null)
            _browserRuntime.Changed -= OnBrowserRuntimeChanged;
        GC.SuppressFinalize(this);
    }
}
