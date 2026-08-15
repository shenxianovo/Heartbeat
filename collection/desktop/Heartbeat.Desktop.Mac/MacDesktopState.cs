using Heartbeat.Desktop.Mac.Configuration;
using Heartbeat.Core;
using Heartbeat.Desktop.UI.Presentation;
using Heartbeat.Collection.Hub.Http;
using Heartbeat.Collection.Hub.Presence;
using Heartbeat.Collection.Hub.Upload;
using Heartbeat.Desktop.Mac.Observations;

namespace Heartbeat.Desktop.Mac;

public sealed class MacDesktopState : IDesktopState, IDisposable
{
    private readonly MacConfigManager _config;
    private readonly ICollectionStatus _collection;
    private readonly IMacLoginStart _loginStart;
    private readonly IClientCompatibilityStatus _compatibility;
    private readonly IUploadStatus _uploads;
    private readonly IMacAccessibilityEvents _accessibility;

    public MacDesktopState(
        MacConfigManager config,
        ICollectionStatus collection,
        IMacLoginStart loginStart,
        IClientCompatibilityStatus compatibility,
        IUploadStatus uploads,
        IMacAccessibilityEvents accessibility)
    {
        _config = config;
        _collection = collection;
        _loginStart = loginStart;
        _compatibility = compatibility;
        _uploads = uploads;
        _accessibility = accessibility;
        _config.ConfigChanged += OnConfigChanged;
        _collection.CurrentActivityChanged += OnCurrentActivityChanged;
        _compatibility.Changed += OnCompatibilityChanged;
        _uploads.Changed += Publish;
        _accessibility.CapabilityChanged += OnAccessibilityCapabilityChanged;
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

    public void SetCollectorEnabled(string source, bool enabled) =>
        _config.Update(config =>
        {
            if (config.Collectors.TryGetValue(source, out var collector))
                collector.Enabled = enabled;
        });

    // Issue 09 intentionally has no Input Monitoring. Ticket 12 owns enabling this setting.
    public void SetInputEventRecordingEnabled(bool enabled) { }

    public void SetWindowTitleObservationEnabled(bool enabled) =>
        _accessibility.SetEnabledFromUser(enabled);

    public void OpenWindowTitlePermissionSettings() =>
        _accessibility.OpenPermissionSettingsFromUser();

    public void SetThemeMode(DesktopThemeMode mode) =>
        _config.Update(config => config.ThemeMode = mode.ToString());

    private DesktopStateSnapshot BuildSnapshot()
    {
        var config = _config.Current;
        return new DesktopStateSnapshot(
            _collection.CurrentActivity,
            new DesktopSettingsSnapshot(
                config.ApiKey,
                config.DeviceName,
                config.UploadIntervalMinutes,
                false,
                ParseThemeMode(config.ThemeMode),
                config.WindowTitleObservationEnabled),
            _loginStart.IsEnabled,
            config.Collectors.ToDictionary(
                pair => pair.Key,
                pair => new CollectorRegistrationState(pair.Value.Enabled, pair.Value.FlushPeriodMs),
                StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, DateTimeOffset>(_collection.SourceLastSeen, StringComparer.OrdinalIgnoreCase),
            _compatibility.Current,
            _uploads.Snapshot,
            BuildCapabilities());
    }

    private DesktopCapabilitySnapshot BuildCapabilities() => _accessibility.CapabilityState switch
    {
        MacAccessibilityCapabilityState.Available => new DesktopCapabilitySnapshot(
            CapabilityAvailability.Available,
            CapabilityAvailability.Available,
            CapabilityAvailability.Unavailable,
            CapabilityAvailability.Unavailable,
            "Accessibility 已授权：记录 focused-window 切换与原始窗口标题；同窗标题变化会在交互信号可用前忽略。",
            WindowTitleObservationConfigurable: true),
        MacAccessibilityCapabilityState.PermissionRequired => new DesktopCapabilitySnapshot(
            CapabilityAvailability.Available,
            CapabilityAvailability.PermissionRequired,
            CapabilityAvailability.Unavailable,
            CapabilityAvailability.Unavailable,
            "窗口标题采集已启用，但 Accessibility 尚未授权；Heartbeat 正继续使用 App-only 模式。",
            WindowTitleObservationConfigurable: true,
            WindowTitlePermissionActionAvailable: true),
        MacAccessibilityCapabilityState.Unavailable => new DesktopCapabilitySnapshot(
            CapabilityAvailability.Available,
            CapabilityAvailability.Unavailable,
            CapabilityAvailability.Unavailable,
            CapabilityAvailability.Unavailable,
            "当前系统无法提供 Accessibility 窗口观察；Heartbeat 正继续使用 App-only 模式。",
            WindowTitleObservationConfigurable: true),
        _ => new DesktopCapabilitySnapshot(
            CapabilityAvailability.Available,
            CapabilityAvailability.Unavailable,
            CapabilityAvailability.Unavailable,
            CapabilityAvailability.Unavailable,
            "App-only 模式无需 Accessibility；可按需启用窗口标题采集。",
            WindowTitleObservationConfigurable: true),
    };

    private static DesktopThemeMode ParseThemeMode(string? value) =>
        Enum.TryParse<DesktopThemeMode>(value, true, out var mode)
            ? mode
            : DesktopThemeMode.System;

    private void OnConfigChanged(MacAgentConfig _) => Publish();
    private void OnCurrentActivityChanged(CurrentActivity? _) => Publish();
    private void OnCompatibilityChanged(ClientCompatibilitySnapshot _) => Publish();
    private void OnAccessibilityCapabilityChanged(MacAccessibilityCapabilityState _) => Publish();
    private void Publish() => Changed?.Invoke(BuildSnapshot());

    public void Dispose()
    {
        _config.ConfigChanged -= OnConfigChanged;
        _collection.CurrentActivityChanged -= OnCurrentActivityChanged;
        _compatibility.Changed -= OnCompatibilityChanged;
        _uploads.Changed -= Publish;
        _accessibility.CapabilityChanged -= OnAccessibilityCapabilityChanged;
        GC.SuppressFinalize(this);
    }
}
