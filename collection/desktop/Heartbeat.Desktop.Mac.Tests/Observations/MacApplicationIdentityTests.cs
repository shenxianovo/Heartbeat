using Heartbeat.Desktop.Mac.Observations;

namespace Heartbeat.Desktop.Mac.Tests.Observations;

public sealed class MacApplicationIdentityTests
{
    [Fact]
    public void BundleIdentifier_BecomesNormalizedMacIdentity()
    {
        var activity = MacApplicationIdentity.ToActivity(new MacApplication(
            " COM.MICROSOFT.VSCode ",
            "/Applications/Visual Studio Code.app/Contents/MacOS/Electron",
            "Visual Studio Code"));

        Assert.Equal("mac:com.microsoft.vscode", activity.AppIdentityKey);
        Assert.Equal("Visual Studio Code", activity.AppDisplayName);
        Assert.Null(activity.Title);
    }

    [Fact]
    public void MissingBundleIdentifier_UsesExecutableIdentityFallback()
    {
        var activity = MacApplicationIdentity.ToActivity(new MacApplication(
            null,
            "/opt/homebrew/bin/wezterm-gui",
            "WezTerm"));

        Assert.Equal("mac:exe.wezterm-gui", activity.AppIdentityKey);
        Assert.Equal("WezTerm", activity.AppDisplayName);
    }

    [Fact]
    public void MissingBundleAndExecutable_CannotInventAnIdentity()
    {
        Assert.Equal(
            global::Heartbeat.Collector.System.Observations.DesktopActivity.None,
            MacApplicationIdentity.ToActivity(new MacApplication(null, null, "Unknown")));
    }
}
