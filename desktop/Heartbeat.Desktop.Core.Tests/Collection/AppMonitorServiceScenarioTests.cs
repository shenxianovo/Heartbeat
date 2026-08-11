using Heartbeat.Core;
using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Desktop.Core.Collection;
using Heartbeat.Desktop.Core.Configuration;
using Heartbeat.Desktop.Core.Input;
using Heartbeat.Desktop.Core.Observations;
using Heartbeat.Hub.Core.Presence;
using Heartbeat.Hub.Core.Segments;
using Heartbeat.Hub.Core.Time;

namespace Heartbeat.Desktop.Core.Tests.Collection;

/// <summary>
/// Desktop.Core 的语义场景契约。测试只喂平台观察、只读输出事实，不知道 Win32/macOS adapter。
/// </summary>
public class AppMonitorServiceScenarioTests
{
    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UnixEpoch;
        public void Advance(TimeSpan duration) => UtcNow += duration;
    }

    private sealed class FakeObservations : IDesktopObservationSource
    {
        public event Action<DesktopObservation>? Observation;
        public DesktopActivity CurrentActivity { get; set; } = DesktopActivity.None;
        public void Start() { }
        public void Stop() { }

        public void Activate(string? app, string? title = null)
            => Raise(DesktopObservation.AppActivated(new DesktopActivity(app, title)));

        public void Focus(string? app, string? title = null)
            => Raise(DesktopObservation.FocusedWindowChanged(new DesktopActivity(app, title)));

        public void ChangeTitle(string? app, string? title)
            => Raise(DesktopObservation.TitleChanged(new DesktopActivity(app, title)));

        public void EnterAway() => Raise(DesktopObservation.EnteredAway());

        public void ExitAway(string? app, string? title = null)
            => Raise(DesktopObservation.ExitedAway(new DesktopActivity(app, title)));

        private void Raise(DesktopObservation observation)
        {
            if (observation.Kind is DesktopObservationKind.AppActivated
                or DesktopObservationKind.FocusedWindowChanged
                or DesktopObservationKind.TitleChanged
                or DesktopObservationKind.ExitedAway)
            {
                CurrentActivity = observation.Activity;
            }
            Observation?.Invoke(observation);
        }
    }

    private sealed class FakeInteractionSignal : IInputActivitySignal
    {
        public bool RecentClick { get; set; }
        public void MarkClick() => RecentClick = true;
        public bool ClickedWithin(TimeSpan window) => RecentClick;
    }

    private sealed class FakeSettings : IDesktopSettings
    {
        public IReadOnlyList<string> AwayProcessNames { get; private set; } = ["LockApp"];
        public bool SplitFocusedWindowChangesUnconditionally { get; set; } = true;
        public event Action<IReadOnlyList<string>>? AwayProcessNamesChanged;

        public void SetAwayProcessNames(params string[] names)
        {
            AwayProcessNames = names;
            AwayProcessNamesChanged?.Invoke(AwayProcessNames);
        }
    }

    private sealed class CapturingSink : ISegmentSink
    {
        public List<ActivitySegmentItem> Items { get; } = [];
        public void Push(List<ActivitySegmentItem> snapshots) => Items.AddRange(snapshots);
        public List<ActivitySegmentItem> Drain()
        {
            var result = Items.ToList();
            Items.Clear();
            return result;
        }
    }

    private sealed class CapturingActivity : ICurrentActivitySink
    {
        public List<string?> Values { get; } = [];
        public void Report(string? app) => Values.Add(app);
    }

    private static (
        AppMonitorService Service,
        FakeClock Clock,
        FakeObservations Observations,
        FakeInteractionSignal Interaction,
        FakeSettings Settings,
        CapturingSink Segments,
        CapturingActivity Activity) Build(string? initialApp = null, string? initialTitle = null)
    {
        var clock = new FakeClock();
        var observations = new FakeObservations
        {
            CurrentActivity = new DesktopActivity(initialApp, initialTitle)
        };
        var interaction = new FakeInteractionSignal();
        var settings = new FakeSettings();
        var segments = new CapturingSink();
        var activity = new CapturingActivity();
        var service = new AppMonitorService(clock, observations, interaction, segments, activity, settings);
        service.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        return (service, clock, observations, interaction, settings, segments, activity);
    }

    private static List<ActivitySegmentItem> Flush(AppMonitorService service, CapturingSink sink)
    {
        service.PushCurrentSnapshot();
        return sink.Drain();
    }

    [Fact]
    public void AppActivation_ClosesPreviousSegment_AndRefreshesCurrentActivity()
    {
        var x = Build("vscode", "main.cs");
        x.Clock.Advance(TimeSpan.FromSeconds(60));

        x.Observations.Activate("chrome", "Docs");

        var segment = Assert.Single(x.Segments.Drain());
        Assert.Equal("vscode", segment.AppName);
        Assert.Equal("main.cs", segment.Title);
        Assert.Equal(60, (segment.EndTime - segment.StartTime).TotalSeconds);
        Assert.Equal("chrome", x.Activity.Values[^1]);
    }

    [Fact]
    public void FocusedWindowChange_AlwaysSplits_EvenWhenTextIsIdentical()
    {
        var x = Build("vscode", "README.md");
        x.Clock.Advance(TimeSpan.FromSeconds(30));

        x.Observations.Focus("vscode", "README.md");

        var segment = Assert.Single(x.Segments.Drain());
        Assert.Equal("vscode", segment.AppName);
        Assert.Equal("README.md", segment.Title);
        Assert.Equal(30, (segment.EndTime - segment.StartTime).TotalSeconds);
    }

    [Fact]
    public void LegacyWindowsFocusPolicy_PreservesOldSameTextOutput()
    {
        var x = Build("vscode", "README.md");
        x.Settings.SplitFocusedWindowChangesUnconditionally = false;
        x.Clock.Advance(TimeSpan.FromSeconds(30));

        x.Observations.Focus("vscode", "README.md");

        Assert.Empty(x.Segments.Drain());
    }

    [Fact]
    public void SameWindowTitleNoise_WithoutInteraction_DoesNotSplit()
    {
        var x = Build("WindowsTerminal", "✳ Claude Code");
        x.Clock.Advance(TimeSpan.FromSeconds(30));
        x.Interaction.RecentClick = false;
        x.Observations.ChangeTitle("WindowsTerminal", "⠐ Claude Code");
        x.Clock.Advance(TimeSpan.FromSeconds(30));

        var segment = Assert.Single(Flush(x.Service, x.Segments));
        Assert.Equal("✳ Claude Code", segment.Title);
        Assert.Equal(60, (segment.EndTime - segment.StartTime).TotalSeconds);
    }

    [Fact]
    public void SameWindowTitleChange_WithInteraction_Splits()
    {
        var x = Build("msedge", "YouTube");
        x.Clock.Advance(TimeSpan.FromSeconds(30));
        x.Interaction.RecentClick = true;

        x.Observations.ChangeTitle("msedge", "GitHub");

        var segment = Assert.Single(x.Segments.Drain());
        Assert.Equal("YouTube", segment.Title);
    }

    [Fact]
    public void AwayTransition_ClosesActivity_EmitsAway_AndReopensForeground()
    {
        var x = Build("vscode");
        x.Clock.Advance(TimeSpan.FromSeconds(20));
        x.Observations.EnterAway();
        x.Clock.Advance(TimeSpan.FromMinutes(5));
        x.Observations.ExitAway("chrome", "Docs");

        var segments = x.Segments.Drain();
        Assert.Equal(2, segments.Count);
        Assert.Equal("vscode", segments[0].AppName);
        Assert.Equal(SyntheticApps.Away, segments[1].AppName);
        Assert.Equal(300, (segments[1].EndTime - segments[1].StartTime).TotalSeconds);
        Assert.Equal(["vscode", SyntheticApps.Away, "chrome"], x.Activity.Values);
    }

    [Fact]
    public void CurrentActivity_IsPublishedAtEverySemanticTransition()
    {
        var x = Build("vscode");

        x.Observations.Activate("chrome");
        x.Observations.EnterAway();
        x.Observations.ExitAway("terminal");

        Assert.Equal(["vscode", "chrome", SyntheticApps.Away, "terminal"], x.Activity.Values);
    }

    [Fact]
    public async Task StopAsync_PushesFinalSnapshot()
    {
        var x = Build("vscode", "main.cs");
        x.Clock.Advance(TimeSpan.FromSeconds(45));

        await x.Service.StopAsync(CancellationToken.None);

        var segment = Assert.Single(x.Segments.Drain());
        Assert.Equal("vscode", segment.AppName);
        Assert.Equal(45, (segment.EndTime - segment.StartTime).TotalSeconds);
    }

    [Fact]
    public void AwayProcessNormalization_HotReloadsThroughSettingsSeam()
    {
        var x = Build("vscode");
        x.Settings.SetAwayProcessNames("ScreenLock");
        x.Clock.Advance(TimeSpan.FromSeconds(10));

        x.Observations.Activate("ScreenLock");

        Assert.Equal(SyntheticApps.Away, x.Activity.Values[^1]);
    }
}
