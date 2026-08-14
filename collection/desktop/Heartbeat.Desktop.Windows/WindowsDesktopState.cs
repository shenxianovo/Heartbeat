using Heartbeat.Desktop.Windows.Configuration;
using Heartbeat.Desktop.Windows.Models;
using Heartbeat.Desktop.Windows.Services;
using Heartbeat.Core;
using Heartbeat.Desktop.UI.Presentation;
using Heartbeat.Collection.Hub.Http;
using Heartbeat.Collection.Hub.Presence;
using Heartbeat.Collection.Hub.Upload;

namespace Heartbeat.Desktop.Windows;

public sealed class WindowsDesktopState : IDesktopState, IDisposable
{
    private readonly ConfigManager _config;
    private readonly ICollectionStatus _collection;
    private readonly IAutoStartService _loginStart;
    private readonly IClientCompatibilityStatus _compatibility;
    private readonly IUploadStatus _uploads;

    public WindowsDesktopState(
        ConfigManager config,
        ICollectionStatus collection,
        IAutoStartService loginStart,
        IClientCompatibilityStatus compatibility,
        IUploadStatus uploads)
    {
        _config = config;
        _collection = collection;
        _loginStart = loginStart;
        _compatibility = compatibility;
        _uploads = uploads;

        _config.ConfigChanged += HandleConfigChanged;
        _collection.CurrentActivityChanged += HandleCurrentActivityChanged;
        _compatibility.Changed += HandleCompatibilityChanged;
        _uploads.Changed += HandleUploadChanged;
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

    public void SetCollectorEnabled(string source, bool enabled) =>
        _config.Update(config =>
        {
            if (config.Collectors.TryGetValue(source, out var collector))
                collector.Enabled = enabled;
        });

    public void SetInputEventRecordingEnabled(bool enabled) =>
        _config.Update(config => config.InputEventRecordingEnabled = enabled);

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
                config.InputEventRecordingEnabled,
                ParseThemeMode(config.ThemeMode)),
            _loginStart.IsEnabled,
            config.Collectors.ToDictionary(
                pair => pair.Key,
                pair => new CollectorRegistrationState(pair.Value.Enabled, pair.Value.FlushPeriodMs),
                StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, DateTimeOffset>(_collection.SourceLastSeen, StringComparer.OrdinalIgnoreCase),
            _compatibility.Current,
            _uploads.Snapshot,
            DesktopCapabilitySnapshot.WindowsFull);
    }

    private static DesktopThemeMode ParseThemeMode(string? value) =>
        Enum.TryParse<DesktopThemeMode>(value, true, out var mode)
            ? mode
            : DesktopThemeMode.System;

    private void HandleConfigChanged(AgentConfig _) => Publish();
    private void HandleCurrentActivityChanged(CurrentActivity? _) => Publish();
    private void HandleCompatibilityChanged(ClientCompatibilitySnapshot _) => Publish();
    private void HandleUploadChanged() => Publish();
    private void Publish() => Changed?.Invoke(BuildSnapshot());

    public void Dispose()
    {
        _config.ConfigChanged -= HandleConfigChanged;
        _collection.CurrentActivityChanged -= HandleCurrentActivityChanged;
        _compatibility.Changed -= HandleCompatibilityChanged;
        _uploads.Changed -= HandleUploadChanged;
    }
}
