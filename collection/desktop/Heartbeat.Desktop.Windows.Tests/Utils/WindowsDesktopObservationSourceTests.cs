using Heartbeat.Desktop.Windows.Utils;
using Heartbeat.Collector.System.Observations;

namespace Heartbeat.Desktop.Windows.Tests.Utils;

public class WindowsDesktopObservationSourceTests
{
    private sealed class FakeWindows : IWindowEventMonitor
    {
        public event Action<DesktopObservation>? Observation;
        public DesktopActivity CurrentActivity { get; set; } = DesktopActivity.None;
        public void Start() { }
        public void Stop() { }
        public void Raise(DesktopObservation observation) => Observation?.Invoke(observation);
    }

    private sealed class FakePower : IPowerMonitor
    {
        public event Action? DisplayOff;
        public event Action? DisplayOn;
        public event Action? Suspend;
        public event Action? Resume;
        public void Start() { }
        public void Stop() { }
        public void RaiseDisplayOff() => DisplayOff?.Invoke();
        public void RaiseDisplayOn() => DisplayOn?.Invoke();
        public void RaiseSuspend() => Suspend?.Invoke();
        public void RaiseResume() => Resume?.Invoke();
    }

    [Fact]
    public void WindowObservation_IsForwardedWithoutLosingSemanticKind()
    {
        var windows = new FakeWindows();
        var source = new WindowsDesktopObservationSource(windows, new FakePower());
        var received = new List<DesktopObservation>();
        source.Observation += received.Add;
        source.Start();

        windows.Raise(DesktopObservation.FocusedWindowChanged(new DesktopActivity("win:code", "README")));

        var observation = Assert.Single(received);
        Assert.Equal(DesktopObservationKind.FocusedWindowChanged, observation.Kind);
        Assert.Equal("win:code", observation.Activity.AppIdentityKey);
    }

    [Fact]
    public void PowerSignals_BecomeAwayTransitions_WithFreshForegroundOnExit()
    {
        var windows = new FakeWindows { CurrentActivity = new DesktopActivity("win:code", "main.cs") };
        var power = new FakePower();
        var source = new WindowsDesktopObservationSource(windows, power);
        var received = new List<DesktopObservation>();
        source.Observation += received.Add;
        source.Start();

        power.RaiseSuspend();
        windows.CurrentActivity = new DesktopActivity("win:chrome", "Docs");
        power.RaiseResume();

        Assert.Equal(DesktopObservationKind.EnteredAway, received[0].Kind);
        Assert.Equal(DesktopObservationKind.ExitedAway, received[1].Kind);
        Assert.Equal("win:chrome", received[1].Activity.AppIdentityKey);
    }
}
