using Heartbeat.Desktop.Mac.Observations;
using Heartbeat.Collector.System.Observations;

namespace Heartbeat.Desktop.Mac.Tests.Observations;

public sealed class MacDesktopObservationSourceTests
{
    private sealed class FakeEvents : IMacDesktopEvents
    {
        public event Action? ApplicationActivated;
        public event Action<MacAwayReason>? AwayEntered;
        public event Action<MacAwayReason>? AwayExited;

        public MacApplication? FrontmostApplication { get; set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }

        public void Start() => StartCount++;
        public void Stop() => StopCount++;
        public void Activate() => ApplicationActivated?.Invoke();
        public void Enter(MacAwayReason reason) => AwayEntered?.Invoke(reason);
        public void Exit(MacAwayReason reason) => AwayExited?.Invoke(reason);
    }

    [Fact]
    public void ApplicationActivation_BecomesAppOnlySemanticObservation()
    {
        var native = new FakeEvents
        {
            FrontmostApplication = new MacApplication(
                "com.microsoft.VSCode", null, "Visual Studio Code")
        };
        var source = new MacDesktopObservationSource(native);
        var received = new List<DesktopObservation>();
        source.Observation += received.Add;

        source.Start();
        native.Activate();
        source.Stop();

        var observation = Assert.Single(received);
        Assert.Equal(DesktopObservationKind.AppActivated, observation.Kind);
        Assert.Equal("mac:com.microsoft.vscode", observation.Activity.AppIdentityKey);
        Assert.Null(observation.Activity.Title);
        Assert.Equal(1, native.StartCount);
        Assert.Equal(1, native.StopCount);
    }

    [Theory]
    [InlineData(MacAwayReason.ScreenLocked)]
    [InlineData(MacAwayReason.SessionInactive)]
    [InlineData(MacAwayReason.DisplaySleep)]
    [InlineData(MacAwayReason.SystemSleep)]
    public void HardAwayReason_BecomesAwayAndResumesWithFreshFrontmostApp(MacAwayReason reason)
    {
        var native = new FakeEvents
        {
            FrontmostApplication = new MacApplication("com.apple.Safari", null, "Safari")
        };
        var source = new MacDesktopObservationSource(native);
        var received = new List<DesktopObservation>();
        source.Observation += received.Add;
        source.Start();

        native.Enter(reason);
        native.FrontmostApplication = new MacApplication("com.apple.Terminal", null, "Terminal");
        native.Exit(reason);

        Assert.Collection(
            received,
            entered => Assert.Equal(DesktopObservationKind.EnteredAway, entered.Kind),
            exited =>
            {
                Assert.Equal(DesktopObservationKind.ExitedAway, exited.Kind);
                Assert.Equal("mac:com.apple.terminal", exited.Activity.AppIdentityKey);
            });
    }

    [Fact]
    public void OverlappingHardAwayReasons_ProduceOneAwaySpan()
    {
        var native = new FakeEvents
        {
            FrontmostApplication = new MacApplication("com.apple.Safari", null, "Safari")
        };
        var source = new MacDesktopObservationSource(native);
        var received = new List<DesktopObservation>();
        source.Observation += received.Add;
        source.Start();

        native.Enter(MacAwayReason.SessionInactive);
        native.Enter(MacAwayReason.SystemSleep);
        native.Exit(MacAwayReason.SystemSleep);
        native.Exit(MacAwayReason.SessionInactive);

        Assert.Equal(
            [DesktopObservationKind.EnteredAway, DesktopObservationKind.ExitedAway],
            received.Select(observation => observation.Kind));
    }

    [Fact]
    public void WakeWithoutMatchingAway_DoesNotInventAnAwayExit()
    {
        var native = new FakeEvents
        {
            FrontmostApplication = new MacApplication("com.apple.Safari", null, "Safari")
        };
        var source = new MacDesktopObservationSource(native);
        var received = new List<DesktopObservation>();
        source.Observation += received.Add;
        source.Start();

        native.Exit(MacAwayReason.DisplaySleep);

        Assert.Empty(received);
    }
}
