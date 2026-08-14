using Heartbeat.Desktop.Mac.Observations;

namespace Heartbeat.Desktop.Mac.Tests.Observations;

public sealed class MacWorkspaceEventsTests
{
    private sealed class FakeWorkspace : IMacWorkspaceNative
    {
        public event Action<string>? Notification;
        public MacApplication? FrontmostApplication { get; set; }
        public IReadOnlyCollection<string>? RegisteredNotifications { get; private set; }
        public int StopCount { get; private set; }

        public void Start(IReadOnlyCollection<string> notificationNames) =>
            RegisteredNotifications = notificationNames;

        public void Stop() => StopCount++;
        public void Raise(string notificationName) => Notification?.Invoke(notificationName);
    }

    [Fact]
    public void WorkspaceNotifications_AreTranslatedToAppAndHardAwaySignals()
    {
        var workspace = new FakeWorkspace();
        var events = new MacWorkspaceEvents(workspace);
        var activations = 0;
        var entered = new List<MacAwayReason>();
        var exited = new List<MacAwayReason>();
        events.ApplicationActivated += () => activations++;
        events.AwayEntered += entered.Add;
        events.AwayExited += exited.Add;

        events.Start();
        workspace.Raise(MacWorkspaceNotification.ApplicationActivated);
        workspace.Raise(MacWorkspaceNotification.ScreenLocked);
        workspace.Raise(MacWorkspaceNotification.SessionInactive);
        workspace.Raise(MacWorkspaceNotification.DisplaySleep);
        workspace.Raise(MacWorkspaceNotification.SystemSleep);
        workspace.Raise(MacWorkspaceNotification.SystemWake);
        workspace.Raise(MacWorkspaceNotification.DisplayWake);
        workspace.Raise(MacWorkspaceNotification.SessionActive);
        workspace.Raise(MacWorkspaceNotification.ScreenUnlocked);
        events.Stop();

        Assert.Equal(1, activations);
        Assert.Equal(
            [MacAwayReason.ScreenLocked, MacAwayReason.SessionInactive, MacAwayReason.DisplaySleep, MacAwayReason.SystemSleep],
            entered);
        Assert.Equal(
            [MacAwayReason.SystemSleep, MacAwayReason.DisplaySleep, MacAwayReason.SessionInactive, MacAwayReason.ScreenLocked],
            exited);
        Assert.Contains(MacWorkspaceNotification.ApplicationActivated, workspace.RegisteredNotifications!);
        Assert.Equal(1, workspace.StopCount);
    }
}
