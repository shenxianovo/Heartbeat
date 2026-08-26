using Heartbeat.Desktop.Windows.Configuration;
using Heartbeat.Desktop.Windows.Models;
using Heartbeat.Desktop.Windows.Services;
using Heartbeat.Core;
using Heartbeat.Desktop.UI.Presentation;
using Heartbeat.Collection.Hub.Http;
using Heartbeat.Collection.Hub.Presence;
using Heartbeat.Collection.Hub.Upload;
using Heartbeat.Collection.Hub.Collectors.Runtime;

namespace Heartbeat.Desktop.Windows;

public sealed class WindowsDesktopState : IDesktopState, IDisposable
{
    private readonly ConfigManager _config;
    private readonly ICollectionStatus _collection;
    private readonly IAutoStartService _loginStart;
    private readonly IClientCompatibilityStatus _compatibility;
    private readonly IUploadStatus _uploads;
    private readonly BrowserCollectorRuntime? _browserRuntime;

    public WindowsDesktopState(
        ConfigManager config,
        ICollectionStatus collection,
        IAutoStartService loginStart,
        IClientCompatibilityStatus compatibility,
        IUploadStatus uploads,
        BrowserCollectorRuntime? browserRuntime = null)
    {
        _config = config;
        _collection = collection;
        _loginStart = loginStart;
        _compatibility = compatibility;
        _uploads = uploads;
        _browserRuntime = browserRuntime;

        ReconcileLoginStartRegistration();
        _config.ConfigChanged += HandleConfigChanged;
        _collection.CurrentActivityChanged += HandleCurrentActivityChanged;
        _compatibility.Changed += HandleCompatibilityChanged;
        _uploads.Changed += HandleUploadChanged;
        if (_browserRuntime is not null)
            _browserRuntime.Changed += HandleBrowserRuntimeChanged;
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
        if (enabled)
        {
            var executablePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(executablePath))
                _loginStart.Enable(executablePath);
        }
        else
        {
            _loginStart.Disable();
        }
        Publish();
    }

    public void SetCollectorEnabled(string source, bool enabled)
    {
        if (string.Equals(source, ActivitySources.Browser, StringComparison.OrdinalIgnoreCase))
            _browserRuntime?.SetDesiredEnabled(enabled);
        _config.Update(config =>
        {
            if (config.Collectors.TryGetValue(source, out var collector))
                collector.Enabled = enabled;
        });
    }

    public void OpenBrowserCollectorSetup(BrowserKind browser)
    {
        var directory = _browserRuntime?.Current.SideloadDirectory;
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("浏览器采集器目录尚未准备好。");
        WindowsBrowserSetupLauncher.Open(browser, directory);
    }

    public void SetSystemCapabilityEnabled(SystemCapability capability, bool enabled) =>
        _config.Update(config =>
        {
            switch (capability)
            {
                case SystemCapability.WindowActivity:
                    config.WindowActivityCollectionEnabled = enabled;
                    break;
                case SystemCapability.InteractionSignal:
                    config.InteractionSignalEnabled = enabled;
                    break;
                case SystemCapability.InputEventRecording:
                    config.InputEventRecordingEnabled = enabled;
                    break;
            }
        });

    public void RecoverSystemCapability(SystemCapability capability) { }

    public void RevealSystemCapabilityApplication(SystemCapability capability) { }

    public void SetThemeMode(DesktopThemeMode mode) =>
        _config.Update(config => config.ThemeMode = mode.ToString());

    private void ReconcileLoginStartRegistration()
    {
        if (!_loginStart.IsEnabled)
            return;

        var executablePath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executablePath))
            _loginStart.Enable(executablePath);
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
            new DesktopCapabilitySnapshot(new Dictionary<SystemCapability, SystemCapabilityState>
            {
                [SystemCapability.ForegroundApp] = new(null, CapabilityAvailability.Available),
                [SystemCapability.WindowActivity] = new(
                    config.WindowActivityCollectionEnabled,
                    CapabilityAvailability.Available),
                [SystemCapability.InteractionSignal] = new(
                    config.InteractionSignalEnabled,
                    config.WindowActivityCollectionEnabled
                        ? CapabilityAvailability.Available
                        : CapabilityAvailability.Paused),
                [SystemCapability.InputEventRecording] = new(
                    config.InputEventRecordingEnabled,
                    CapabilityAvailability.Available),
            }),
            MapBrowserRuntime(_browserRuntime?.Current));
    }

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
            snapshot.PreviousKnownGoodVersion);

    private void HandleConfigChanged(AgentConfig _) => Publish();
    private void HandleCurrentActivityChanged(CurrentActivity? _) => Publish();
    private void HandleCompatibilityChanged(ClientCompatibilitySnapshot _) => Publish();
    private void HandleUploadChanged() => Publish();
    private void HandleBrowserRuntimeChanged(BrowserCollectorRuntimeSnapshot _) => Publish();
    private void Publish() => Changed?.Invoke(BuildSnapshot());

    public void Dispose()
    {
        _config.ConfigChanged -= HandleConfigChanged;
        _collection.CurrentActivityChanged -= HandleCurrentActivityChanged;
        _compatibility.Changed -= HandleCompatibilityChanged;
        _uploads.Changed -= HandleUploadChanged;
        if (_browserRuntime is not null)
            _browserRuntime.Changed -= HandleBrowserRuntimeChanged;
    }
}
