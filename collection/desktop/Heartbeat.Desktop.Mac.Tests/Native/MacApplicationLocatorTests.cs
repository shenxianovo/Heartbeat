using Heartbeat.Desktop.Mac.Native;

namespace Heartbeat.Desktop.Mac.Tests.Native;

public sealed class MacApplicationLocatorTests
{
    [Theory]
    [InlineData(
        "/Applications/Heartbeat.app/Contents/MacOS/Heartbeat",
        "/Applications/Heartbeat.app")]
    [InlineData(
        "/work/Heartbeat/bin/Debug/net10.0/osx-arm64/Heartbeat.Desktop.Mac",
        "/work/Heartbeat/bin/Debug/net10.0/osx-arm64/Heartbeat.Desktop.Mac")]
    public void ResolveRevealTarget_PrefersTheAppBundleButSupportsDevelopmentExecutables(
        string processPath,
        string expected)
    {
        Assert.Equal(expected, MacApplicationLocator.ResolveRevealTarget(processPath));
    }

    [Fact]
    public void RevealFromUser_OpensFinderAtTheResolvedHeartbeatLocation()
    {
        var runner = new FakeCommandRunner();
        var locator = new MacApplicationLocator(
            runner,
            () => "/Applications/Heartbeat.app/Contents/MacOS/Heartbeat");

        locator.RevealFromUser();

        var command = Assert.Single(runner.Commands);
        Assert.Equal("/usr/bin/open", command.FileName);
        Assert.Equal(["-R", "/Applications/Heartbeat.app"], command.Arguments);
    }

    private sealed class FakeCommandRunner : IMacCommandRunner
    {
        public List<(string FileName, IReadOnlyList<string> Arguments)> Commands { get; } = [];

        public MacCommandResult Run(string fileName, IReadOnlyList<string> arguments)
        {
            Commands.Add((fileName, arguments));
            return new MacCommandResult(0, string.Empty, string.Empty);
        }
    }
}
