using Heartbeat.Core;
using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Collector.System.Collection;
using Heartbeat.Collector.System.Configuration;
using Heartbeat.Collector.System.Input;
using Heartbeat.Collector.System.Observations;
using Heartbeat.Collection.Hub.Presence;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Collection.Hub.Time;

namespace Heartbeat.Collector.System.Tests.Collection;

/// <summary>
/// system Collector 的语义场景契约。测试只喂平台观察、只读输出事实，不知道 Win32/macOS adapter。
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

        public void Activate(string? appIdentityKey, string? title = null)
            => Raise(DesktopObservation.AppActivated(new DesktopActivity(appIdentityKey, title)));

        public void Focus(string? appIdentityKey, string? title = null)
            => Raise(DesktopObservation.FocusedWindowChanged(new DesktopActivity(appIdentityKey, title)));

        public void ChangeTitle(string? appIdentityKey, string? title)
            => Raise(DesktopObservation.TitleChanged(new DesktopActivity(appIdentityKey, title)));

        public void EnterAway() => Raise(DesktopObservation.EnteredAway());

        public void ExitAway(string? appIdentityKey, string? title = null)
            => Raise(DesktopObservation.ExitedAway(new DesktopActivity(appIdentityKey, title)));

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
        public List<CurrentActivity?> Values { get; } = [];
        public void Report(CurrentActivity? activity) => Values.Add(activity);
    }

    private static (
        AppMonitorService Service,
        FakeClock Clock,
        FakeObservations Observations,
        FakeInteractionSignal Interaction,
        FakeSettings Settings,
        CapturingSink Segments,
        CapturingActivity Activity) Build(string? initialAppIdentityKey = null, string? initialTitle = null)
    {
        var clock = new FakeClock();
        var observations = new FakeObservations
        {
            CurrentActivity = new DesktopActivity(initialAppIdentityKey, initialTitle)
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
        var x = Build("win:code", "main.cs");
        x.Clock.Advance(TimeSpan.FromSeconds(60));

        x.Observations.Activate("win:chrome", "Docs");

        var segment = Assert.Single(x.Segments.Drain());
        Assert.Equal("win:code", segment.AppIdentityKey);
        Assert.Null(segment.AppName);
        Assert.Equal("main.cs", segment.Title);
        Assert.Equal(60, (segment.EndTime - segment.StartTime).TotalSeconds);
        Assert.Equal("win:chrome", x.Activity.Values[^1]!.AppIdentityKey);
    }

    [Fact]
    public void FocusedWindowChange_AlwaysSplits_EvenWhenTextIsIdentical()
    {
        var x = Build("win:code", "README.md");
        x.Clock.Advance(TimeSpan.FromSeconds(30));

        x.Observations.Focus("win:code", "README.md");

        var segment = Assert.Single(x.Segments.Drain());
        Assert.Equal("win:code", segment.AppIdentityKey);
        Assert.Equal("README.md", segment.Title);
        Assert.Equal(30, (segment.EndTime - segment.StartTime).TotalSeconds);
    }

    [Fact]
    public void LegacyWindowsFocusPolicy_PreservesOldSameTextOutput()
    {
        var x = Build("win:code", "README.md");
        x.Settings.SplitFocusedWindowChangesUnconditionally = false;
        x.Clock.Advance(TimeSpan.FromSeconds(30));

        x.Observations.Focus("win:code", "README.md");

        Assert.Empty(x.Segments.Drain());
    }

    [Fact]
    public void SameWindowTitleNoise_WithoutInteraction_DoesNotSplit()
    {
        var x = Build("win:windowsterminal", "✳ Claude Code");
        x.Clock.Advance(TimeSpan.FromSeconds(30));
        x.Interaction.RecentClick = false;
        x.Observations.ChangeTitle("win:windowsterminal", "⠐ Claude Code");
        x.Clock.Advance(TimeSpan.FromSeconds(30));

        var segment = Assert.Single(Flush(x.Service, x.Segments));
        Assert.Equal("✳ Claude Code", segment.Title);
        Assert.Equal(60, (segment.EndTime - segment.StartTime).TotalSeconds);
    }

    [Fact]
    public void SameWindowTitleChange_WithInteraction_Splits()
    {
        var x = Build("win:msedge", "YouTube");
        x.Clock.Advance(TimeSpan.FromSeconds(30));
        x.Interaction.RecentClick = true;

        x.Observations.ChangeTitle("win:msedge", "GitHub");

        var segment = Assert.Single(x.Segments.Drain());
        Assert.Equal("YouTube", segment.Title);
    }

    [Fact]
    public void AwayTransition_ClosesActivity_EmitsAway_AndReopensForeground()
    {
        var x = Build("win:code");
        x.Clock.Advance(TimeSpan.FromSeconds(20));
        x.Observations.EnterAway();
        x.Clock.Advance(TimeSpan.FromMinutes(5));
        x.Observations.ExitAway("win:chrome", "Docs");

        var segments = x.Segments.Drain();
        Assert.Equal(2, segments.Count);
        Assert.Equal("win:code", segments[0].AppIdentityKey);
        Assert.Equal("sys:away", segments[1].AppIdentityKey);
        Assert.All(segments, segment => Assert.Null(segment.AppName));
        Assert.Equal(300, (segments[1].EndTime - segments[1].StartTime).TotalSeconds);
        Assert.Equal(
            ["win:code", "sys:away", "win:chrome"],
            x.Activity.Values.Select(value => value?.AppIdentityKey));
    }

    [Fact]
    public void CurrentActivity_IsPublishedAtEverySemanticTransition()
    {
        var x = Build("win:code");

        x.Observations.Activate("win:chrome");
        x.Observations.EnterAway();
        x.Observations.ExitAway("win:terminal");

        Assert.Equal(
            ["win:code", "win:chrome", "sys:away", "win:terminal"],
            x.Activity.Values.Select(value => value?.AppIdentityKey));
    }

    [Fact]
    public async Task StopAsync_PushesFinalSnapshot()
    {
        var x = Build("win:code", "main.cs");
        x.Clock.Advance(TimeSpan.FromSeconds(45));

        await x.Service.StopAsync(CancellationToken.None);

        var segment = Assert.Single(x.Segments.Drain());
        Assert.Equal("win:code", segment.AppIdentityKey);
        Assert.Equal(45, (segment.EndTime - segment.StartTime).TotalSeconds);
    }

    [Fact]
    public void AwayProcessNormalization_HotReloadsThroughSettingsSeam()
    {
        var x = Build("win:code");
        x.Settings.SetAwayProcessNames("ScreenLock");
        x.Clock.Advance(TimeSpan.FromSeconds(10));

        x.Observations.Activate("win:screenlock");

        Assert.Equal("sys:away", x.Activity.Values[^1]!.AppIdentityKey);
    }
}
