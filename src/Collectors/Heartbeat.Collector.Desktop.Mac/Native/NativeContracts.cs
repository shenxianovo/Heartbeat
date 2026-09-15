namespace Heartbeat.Collector.Desktop.Mac.Native;

public sealed record MacApplication(
    string? BundleIdentifier,
    string? ExecutablePath,
    string? DisplayName,
    int ProcessIdentifier);

public static class MacWorkspaceNotification
{
    public const string ApplicationActivated = "NSWorkspaceDidActivateApplicationNotification";
    public const string ScreenLocked = "com.apple.screenIsLocked";
    public const string ScreenUnlocked = "com.apple.screenIsUnlocked";
    public const string SessionInactive = "NSWorkspaceSessionDidResignActiveNotification";
    public const string SessionActive = "NSWorkspaceSessionDidBecomeActiveNotification";
    public const string DisplaySleep = "NSWorkspaceScreensDidSleepNotification";
    public const string DisplayWake = "NSWorkspaceScreensDidWakeNotification";
    public const string SystemSleep = "NSWorkspaceWillSleepNotification";
    public const string SystemWake = "NSWorkspaceDidWakeNotification";

    public static IReadOnlyCollection<string> All { get; } =
    [
        ApplicationActivated,
        ScreenLocked,
        ScreenUnlocked,
        SessionInactive,
        SessionActive,
        DisplaySleep,
        DisplayWake,
        SystemSleep,
        SystemWake,
    ];
}

public interface IMacWorkspaceNative : IDisposable
{
    event Action<string>? Notification;
    MacApplication? FrontmostApplication { get; }
    void Start(IReadOnlyCollection<string> notificationNames);
    void StopNotifications();
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

public interface IMacAccessibilityNative : IDisposable
{
    event Action<Exception>? Failed;
    event Action<MacAccessibilityObservation>? Observation;
    bool IsAvailable { get; }
    bool IsProcessTrusted { get; }
    void RequestProcessTrust();
    string? ReadFocusedWindowTitle(int processIdentifier);
    void ObserveApplication(int processIdentifier);
    bool IsObservingApplication(int processIdentifier);
    void StopObserving();
}

public enum MacInputObservationKind
{
    KeyDown,
    KeyUp,
    MouseButton,
    Scroll,
}

public readonly record struct MacInputObservation(
    MacInputObservationKind Kind,
    int Value = 0,
    double DeltaX = 0,
    double DeltaY = 0,
    ScrollUnit? ScrollUnit = null);

public interface IMacInputMonitoringNative : IDisposable
{
    event Action<Exception>? Failed;
    event Action<MacInputObservation>? Observation;
    bool IsAvailable { get; }
    bool IsAuthorized { get; }
    void RequestAuthorization();
    void StartListening();
    void StopListening();
}
