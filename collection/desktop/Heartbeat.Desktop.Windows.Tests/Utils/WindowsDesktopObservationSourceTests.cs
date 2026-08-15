using Heartbeat.Desktop.Windows.Utils;
using Heartbeat.Collector.System.Observations;
using Heartbeat.Desktop.Windows.Configuration;

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

    private sealed class MutableWindowActivityPolicy(bool enabled) : IWindowActivityCollectionPolicy
    {
        public bool Enabled { get; private set; } = enabled;
        public event Action<bool>? Changed;
        public void Set(bool value)
        {
            Enabled = value;
            Changed?.Invoke(value);
        }
    }

    [Fact]
    public void WindowObservation_IsForwardedWithoutLosingSemanticKind()
    {
        var windows = new FakeWindows();
        var source = new WindowsDesktopObservationSource(
            windows,
            new FakePower(),
            new MutableWindowActivityPolicy(true));
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
        var source = new WindowsDesktopObservationSource(
            windows,
            power,
            new MutableWindowActivityPolicy(true));
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

    [Fact]
    public void WindowActivityDisabled_KeepsAppBaselineAndResumesCurrentWindowWhenEnabled()
    {
        var windows = new FakeWindows
        {
            CurrentActivity = new DesktopActivity("win:code", "README.md")
        };
        var policy = new MutableWindowActivityPolicy(false);
        var source = new WindowsDesktopObservationSource(windows, new FakePower(), policy);
        var received = new List<DesktopObservation>();
        source.Observation += received.Add;
        source.Start();

        Assert.Null(source.CurrentActivity.Title);
        windows.Raise(DesktopObservation.AppActivated(
            new DesktopActivity("win:chrome", "Private title")));
        windows.Raise(DesktopObservation.FocusedWindowChanged(
            new DesktopActivity("win:chrome", "Another window")));
        windows.Raise(DesktopObservation.TitleChanged(
            new DesktopActivity("win:chrome", "Animated title")));

        var app = Assert.Single(received);
        Assert.Equal(DesktopObservationKind.AppActivated, app.Kind);
        Assert.Null(app.Activity.Title);

        windows.CurrentActivity = new DesktopActivity("win:chrome", "Current tab");
        policy.Set(true);

        Assert.Equal(2, received.Count);
        Assert.Equal(DesktopObservationKind.FocusedWindowChanged, received[1].Kind);
        Assert.Equal("Current tab", received[1].Activity.Title);
    }
}
