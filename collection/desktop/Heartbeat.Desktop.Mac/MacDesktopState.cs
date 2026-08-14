using Heartbeat.Desktop.Mac.Configuration;
using Heartbeat.Core;
using Heartbeat.Desktop.UI.Presentation;
using Heartbeat.Collection.Hub.Http;
using Heartbeat.Collection.Hub.Presence;
using Heartbeat.Collection.Hub.Upload;

namespace Heartbeat.Desktop.Mac;

public sealed class MacDesktopState : IDesktopState, IDisposable
{
    private readonly MacConfigManager _config;
    private readonly ICollectionStatus _collection;
    private readonly IMacLoginStart _loginStart;
    private readonly IClientCompatibilityStatus _compatibility;
    private readonly IUploadStatus _uploads;

    public MacDesktopState(
        MacConfigManager config,
        ICollectionStatus collection,
        IMacLoginStart loginStart,
        IClientCompatibilityStatus compatibility,
        IUploadStatus uploads)
    {
        _config = config;
        _collection = collection;
        _loginStart = loginStart;
        _compatibility = compatibility;
        _uploads = uploads;
        _config.ConfigChanged += OnConfigChanged;
        _collection.CurrentActivityChanged += OnCurrentActivityChanged;
        _compatibility.Changed += OnCompatibilityChanged;
        _uploads.Changed += Publish;
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
                ParseThemeMode(config.ThemeMode)),
            _loginStart.IsEnabled,
            config.Collectors.ToDictionary(
                pair => pair.Key,
                pair => new CollectorRegistrationState(pair.Value.Enabled, pair.Value.FlushPeriodMs),
                StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, DateTimeOffset>(_collection.SourceLastSeen, StringComparer.OrdinalIgnoreCase),
            _compatibility.Current,
            _uploads.Snapshot,
            DesktopCapabilitySnapshot.MacAppOnly);
    }

    private static DesktopThemeMode ParseThemeMode(string? value) =>
        Enum.TryParse<DesktopThemeMode>(value, true, out var mode)
            ? mode
            : DesktopThemeMode.System;

    private void OnConfigChanged(MacAgentConfig _) => Publish();
    private void OnCurrentActivityChanged(CurrentActivity? _) => Publish();
    private void OnCompatibilityChanged(ClientCompatibilitySnapshot _) => Publish();
    private void Publish() => Changed?.Invoke(BuildSnapshot());

    public void Dispose()
    {
        _config.ConfigChanged -= OnConfigChanged;
        _collection.CurrentActivityChanged -= OnCurrentActivityChanged;
        _compatibility.Changed -= OnCompatibilityChanged;
        _uploads.Changed -= Publish;
        GC.SuppressFinalize(this);
    }
}
