namespace Heartbeat.Agent.Mac.Observations;

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

public interface IMacWorkspaceNative
{
    event Action<string>? Notification;
    MacApplication? FrontmostApplication { get; }
    void Start(IReadOnlyCollection<string> notificationNames);
    void Stop();
}

/// <summary>把 NSWorkspace notification 名称收敛为 App-only 所需的少量原生事实。</summary>
public sealed class MacWorkspaceEvents(IMacWorkspaceNative workspace) : IMacDesktopEvents
{
    private bool _started;

    public event Action? ApplicationActivated;
    public event Action<MacAwayReason>? AwayEntered;
    public event Action<MacAwayReason>? AwayExited;

    public MacApplication? FrontmostApplication => workspace.FrontmostApplication;

    public void Start()
    {
        if (_started) return;
        _started = true;
        workspace.Notification += OnNotification;
        workspace.Start(MacWorkspaceNotification.All);
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        workspace.Notification -= OnNotification;
        workspace.Stop();
    }

    private void OnNotification(string name)
    {
        switch (name)
        {
            case MacWorkspaceNotification.ApplicationActivated:
                ApplicationActivated?.Invoke();
                break;
            case MacWorkspaceNotification.ScreenLocked:
                AwayEntered?.Invoke(MacAwayReason.ScreenLocked);
                break;
            case MacWorkspaceNotification.ScreenUnlocked:
                AwayExited?.Invoke(MacAwayReason.ScreenLocked);
                break;
            case MacWorkspaceNotification.SessionInactive:
                AwayEntered?.Invoke(MacAwayReason.SessionInactive);
                break;
            case MacWorkspaceNotification.SessionActive:
                AwayExited?.Invoke(MacAwayReason.SessionInactive);
                break;
            case MacWorkspaceNotification.DisplaySleep:
                AwayEntered?.Invoke(MacAwayReason.DisplaySleep);
                break;
            case MacWorkspaceNotification.DisplayWake:
                AwayExited?.Invoke(MacAwayReason.DisplaySleep);
                break;
            case MacWorkspaceNotification.SystemSleep:
                AwayEntered?.Invoke(MacAwayReason.SystemSleep);
                break;
            case MacWorkspaceNotification.SystemWake:
                AwayExited?.Invoke(MacAwayReason.SystemSleep);
                break;
        }
    }
}
