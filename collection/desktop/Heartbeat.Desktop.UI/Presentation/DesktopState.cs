using Heartbeat.Collection.Hub.Http;
using Heartbeat.Collection.Hub.Presence;
using Heartbeat.Collection.Hub.Upload;

namespace Heartbeat.Desktop.UI.Presentation;

public sealed record CollectorRegistrationState(bool Enabled, int? FlushPeriodMs);

public enum DesktopThemeMode
{
    System,
    Light,
    Dark
}

public sealed record DesktopSettingsSnapshot(
    string ApiKey,
    string DeviceName,
    int UploadIntervalMinutes,
    bool InputEventRecordingEnabled,
    DesktopThemeMode ThemeMode = DesktopThemeMode.System)
{
    public static DesktopSettingsSnapshot Default { get; } = new("", "", 1, true);
}

public sealed record DesktopSettingsInput(
    string ApiKey,
    string DeviceName,
    int UploadIntervalMinutes);

public enum CapabilityAvailability
{
    Available,
    Unavailable,
    PermissionRequired
}

public sealed record DesktopCapabilitySnapshot(
    CapabilityAvailability AppObservation,
    CapabilityAvailability FocusedWindowObservation,
    CapabilityAvailability InteractionSignal,
    CapabilityAvailability InputEventRecording,
    string? Message = null)
{
    public static DesktopCapabilitySnapshot WindowsFull { get; } = new(
        CapabilityAvailability.Available,
        CapabilityAvailability.Available,
        CapabilityAvailability.Available,
        CapabilityAvailability.Available);

    public static DesktopCapabilitySnapshot MacAppOnly { get; } = new(
        CapabilityAvailability.Available,
        CapabilityAvailability.Unavailable,
        CapabilityAvailability.Unavailable,
        CapabilityAvailability.Unavailable,
        "App-only 模式无需 Accessibility 或 Input Monitoring；更深采集能力当前未启用。");
}

public sealed record DesktopStateSnapshot(
    CurrentActivity? CurrentActivity,
    DesktopSettingsSnapshot Settings,
    bool LoginStartEnabled,
    IReadOnlyDictionary<string, CollectorRegistrationState> Collectors,
    IReadOnlyDictionary<string, DateTimeOffset> SourceLastSeen,
    ClientCompatibilitySnapshot Compatibility,
    IReadOnlyDictionary<string, UploadStreamStatus> UploadStreams,
    DesktopCapabilitySnapshot Capabilities)
{
    public static DesktopStateSnapshot Empty { get; } = new(
        null,
        DesktopSettingsSnapshot.Default,
        false,
        new Dictionary<string, CollectorRegistrationState>(),
        new Dictionary<string, DateTimeOffset>(),
        new ClientCompatibilitySnapshot(false),
        new Dictionary<string, UploadStreamStatus>(),
        DesktopCapabilitySnapshot.WindowsFull);
}

/// <summary>
/// Platform-head seam consumed by the shared desktop presentation module.
/// It combines portable hub state with platform configuration without exposing
/// Windows or macOS persistence details to Avalonia ViewModels.
/// </summary>
public interface IDesktopState
{
    DesktopStateSnapshot Current { get; }
    event Action<DesktopStateSnapshot>? Changed;

    void SaveSettings(DesktopSettingsInput settings);
    void SetLoginStartEnabled(bool enabled);
    void SetCollectorEnabled(string source, bool enabled);
    void SetInputEventRecordingEnabled(bool enabled);
    void SetThemeMode(DesktopThemeMode mode);
}
