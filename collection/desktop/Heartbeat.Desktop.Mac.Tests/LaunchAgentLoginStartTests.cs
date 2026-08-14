using Heartbeat.Desktop.Mac;

namespace Heartbeat.Desktop.Mac.Tests;

public sealed class LaunchAgentLoginStartTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"heartbeat-launch-{Guid.NewGuid()}");

    [Fact]
    public void EnableAndDisable_ManagePerUserLaunchAgentWithoutAdminRights()
    {
        var path = Path.Combine(_root, "com.shenxianovo.heartbeat.plist");
        var login = new LaunchAgentLoginStart(path);
        const string executable = "/Users/me/Applications/Heartbeat.app/Contents/MacOS/Heartbeat";

        login.Enable(executable);

        Assert.True(login.IsEnabled);
        var plist = File.ReadAllText(path);
        Assert.Contains(executable, plist);
        Assert.Contains("RunAtLoad", plist);

        login.Disable();

        Assert.False(login.IsEnabled);
        Assert.False(File.Exists(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
