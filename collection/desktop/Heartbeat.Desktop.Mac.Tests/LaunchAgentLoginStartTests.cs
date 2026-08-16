using Heartbeat.Desktop.Mac;
using Heartbeat.Desktop.Mac.Native;

namespace Heartbeat.Desktop.Mac.Tests;

public sealed class LaunchAgentLoginStartTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"heartbeat-launch-{Guid.NewGuid()}");

    [Fact]
    public void EnableAndDisable_ManagePerUserLaunchAgentWithoutAdminRights()
    {
        var path = Path.Combine(_root, "com.shenxianovo.heartbeat.plist");
        var commands = new FakeCommandRunner();
        var login = new LaunchAgentLoginStart(commands, path, () => 501);
        const string executable = "/Users/me/Applications/Heartbeat.app/Contents/MacOS/Heartbeat";

        login.Enable(executable);

        Assert.True(login.IsEnabled);
        var plist = File.ReadAllText(path);
        Assert.Contains(executable, plist);
        Assert.Contains("RunAtLoad", plist);
        Assert.Collection(
            commands.Commands,
            command => Assert.Equal(
                ["print", "gui/501/com.shenxianovo.heartbeat"],
                command.Arguments),
            command => Assert.Equal(
                ["bootstrap", "gui/501", path],
                command.Arguments));

        login.Disable();

        Assert.False(login.IsEnabled);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Enable_ReloadsAJobWhoseLoadedExecutableIsStale()
    {
        var path = Path.Combine(_root, "com.shenxianovo.heartbeat.plist");
        var commands = new FakeCommandRunner
        {
            Handler = (_, arguments) => arguments[0] == "print"
                ? new MacCommandResult(
                    0,
                    "program = /old/Heartbeat.Desktop.Mac\n",
                    string.Empty)
                : new MacCommandResult(0, string.Empty, string.Empty)
        };
        var login = new LaunchAgentLoginStart(commands, path, () => 501);

        login.Enable("/Applications/Heartbeat.app/Contents/MacOS/Heartbeat.Desktop.Mac");

        Assert.Collection(
            commands.Commands,
            command => Assert.Equal(
                ["print", "gui/501/com.shenxianovo.heartbeat"],
                command.Arguments),
            command => Assert.Equal(
                ["bootout", "gui/501/com.shenxianovo.heartbeat"],
                command.Arguments),
            command => Assert.Equal(
                ["bootstrap", "gui/501", path],
                command.Arguments));
    }

    [Fact]
    public void Enable_DoesNotUnloadAJobAlreadyRunningTheCurrentExecutable()
    {
        var path = Path.Combine(_root, "com.shenxianovo.heartbeat.plist");
        const string executable =
            "/Applications/Heartbeat.app/Contents/MacOS/Heartbeat.Desktop.Mac";
        var commands = new FakeCommandRunner
        {
            Handler = (_, arguments) => arguments[0] == "print"
                ? new MacCommandResult(0, $"program = {executable}\n", string.Empty)
                : new MacCommandResult(0, string.Empty, string.Empty)
        };
        var login = new LaunchAgentLoginStart(commands, path, () => 501);

        login.Enable(executable);

        var command = Assert.Single(commands.Commands);
        Assert.Equal(
            ["print", "gui/501/com.shenxianovo.heartbeat"],
            command.Arguments);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeCommandRunner : IMacCommandRunner
    {
        public List<(string FileName, IReadOnlyList<string> Arguments)> Commands { get; } = [];
        public Func<string, IReadOnlyList<string>, MacCommandResult>? Handler { get; init; }

        public MacCommandResult Run(string fileName, IReadOnlyList<string> arguments)
        {
            Commands.Add((fileName, arguments));
            return Handler?.Invoke(fileName, arguments)
                ?? (arguments[0] == "print"
                    ? new MacCommandResult(113, string.Empty, "Could not find service")
                    : new MacCommandResult(0, string.Empty, string.Empty));
        }
    }
}
