using Heartbeat.Collector.System.Observations;

namespace Heartbeat.Collector.System.Tests.Observations;

public class SystemActivityModelTests
{
    [Fact]
    public void RepeatedAppAndWindowReadingsCanStillRepresentTransitions()
    {
        var model = new SystemActivityModel();
        var activity = new DesktopActivity("win:code", "README.md");
        model.Start(activity, []);

        Assert.True(model.Observe(DesktopObservation.AppActivated(activity), false, true, []));
        Assert.True(model.Observe(DesktopObservation.FocusedWindowChanged(activity), false, true, []));
        Assert.False(model.Observe(DesktopObservation.TitleChanged(activity), true, true, []));
        Assert.Equal(activity, model.Current);
    }

    [Fact]
    public void SuppressedTitleIsRememberedWithoutReplacingAcceptedActivity()
    {
        var model = new SystemActivityModel();
        var initial = new DesktopActivity("win:terminal", "Original");
        var noise = DesktopObservation.TitleChanged(initial with { Title = "Spinner" });
        model.Start(initial, []);

        Assert.False(model.Observe(noise, false, true, []));
        // A later click does not turn the same sampled title into a new transition.
        Assert.False(model.Observe(noise, true, true, []));
        Assert.Equal(initial, model.Current);
        Assert.True(model.Observe(
            DesktopObservation.TitleChanged(initial with { Title = "New tab" }), true, true, []));
        Assert.Equal("New tab", model.Current.Title);
    }

    [Fact]
    public void AwayIgnoresForegroundCallbacksAndCanResumeWithoutAnApp()
    {
        var model = new SystemActivityModel();
        var activity = new DesktopActivity("win:code", "README.md");
        model.Start(activity, []);

        Assert.True(model.Observe(DesktopObservation.EnteredAway(), false, true, []));
        Assert.False(model.Observe(DesktopObservation.EnteredAway(), false, true, []));
        Assert.False(model.Observe(DesktopObservation.AppActivated(activity), true, true, []));
        Assert.Equal(new DesktopActivity("sys:away", "离开", null), model.Current);
        Assert.True(model.Observe(DesktopObservation.ExitedAway(DesktopActivity.None), false, true, []));
        Assert.Equal(DesktopActivity.None, model.Current);
        Assert.False(model.Observe(DesktopObservation.ExitedAway(activity), false, true, []));
        Assert.True(model.Observe(DesktopObservation.AppActivated(activity), false, true, []));
        Assert.Equal(activity, model.Current);
    }
}
