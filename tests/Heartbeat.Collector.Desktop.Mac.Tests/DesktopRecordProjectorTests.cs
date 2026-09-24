using Heartbeat.Collector.Desktop.Mac;
using Heartbeat.Hub;

namespace Heartbeat.Collector.Desktop.Mac.Tests;

public sealed class DesktopRecordProjectorTests
{
    private const string ApplicationTrack = "desktop.application.foreground";
    private const string WindowTrack = "desktop.window.foreground";
    private static readonly DateTimeOffset Start = new(2026, 9, 14, 8, 0, 0, TimeSpan.Zero);
    private static readonly ForegroundApplication Application =
        new("macos", "bundle_id", "com.example.App", "Example");
    private static readonly DesktopActivitySample App = new(Application, "Document");
    private static readonly TimeSpan Dwell = TimeSpan.FromMilliseconds(1500);

    [Fact]
    public void OneApplicationStaysOneRecordWhileTitlesChange()
    {
        var staged = new List<(SubmissionRoute Route, RecordSnapshot Record)>();
        var projector = Create(staged);
        projector.Apply(new DesktopObservation.Activity(App), Start);
        projector.Apply(new DesktopObservation.Activity(App with { WindowTitle = "Other" }), Start.AddSeconds(2));
        projector.Apply(new DesktopObservation.Activity(App), Start.AddSeconds(4));
        projector.Apply(new DesktopObservation.Activity(App), Start.AddSeconds(6));

        var application = Assert.Single(Latest(staged, ApplicationTrack));
        Assert.Equal(Start, application.StartedAt);
        Assert.Equal(Start.AddSeconds(6), application.EndedAt);

        var windows = Latest(staged, WindowTrack);
        Assert.Equal(3, windows.Length);
        Assert.Equal(Start.AddSeconds(2), windows[0].EndedAt);
        Assert.Equal(Start.AddSeconds(2), windows[1].StartedAt);
        Assert.Equal(Start.AddSeconds(4), windows[2].StartedAt);
    }

    [Fact]
    public void AnimatedTitleStaysOneWindowRecord()
    {
        var staged = new List<(SubmissionRoute Route, RecordSnapshot Record)>();
        var projector = Create(staged);
        // 实测的终端 spinner：三帧一循环，每帧约 0.96 秒，没有一帧活得过静置时间。
        string[] frames = ["\u2802 build", "\u2810 build", "\u2800 build"];
        for (var index = 0; index < 12; index++)
        {
            projector.Apply(
                new DesktopObservation.Activity(App with { WindowTitle = frames[index % frames.Length] }),
                Start.AddSeconds(0.96 * index));
        }

        var window = Assert.Single(Latest(staged, WindowTrack));
        Assert.Equal(Start, window.StartedAt);
        Assert.Equal(Start.AddSeconds(0.96 * 11), window.EndedAt);
        Assert.Equal(frames[0], window.Value.GetProperty("window").GetProperty("title").GetString());
    }

    [Fact]
    public void SettledTitleStartsWhenItFirstAppearedNotWhenItWasConfirmed()
    {
        var staged = new List<(SubmissionRoute Route, RecordSnapshot Record)>();
        var projector = Create(staged);
        projector.Apply(new DesktopObservation.Activity(App), Start);
        // 导航中间态：地址先出现，页面标题随后覆盖它。
        projector.Apply(new DesktopObservation.Activity(App with { WindowTitle = "https://example.test" }),
            Start.AddSeconds(0.3));
        projector.Apply(new DesktopObservation.Activity(App with { WindowTitle = "Report" }), Start.AddSeconds(0.6));
        projector.Confirm(new DesktopSnapshot(App with { WindowTitle = "Report" }, []), Start.AddSeconds(5));

        var windows = Latest(staged, WindowTrack);
        Assert.Equal(2, windows.Length);
        Assert.Equal("Document", windows[0].Value.GetProperty("window").GetProperty("title").GetString());
        Assert.Equal(Start.AddSeconds(0.6), windows[0].EndedAt);
        // 静置只推迟写入：区间从标题第一次出现算起，不是从确认那一刻算起。
        Assert.Equal("Report", windows[1].Value.GetProperty("window").GetProperty("title").GetString());
        Assert.Equal(Start.AddSeconds(0.6), windows[1].StartedAt);
        Assert.Equal(Start.AddSeconds(5), windows[1].EndedAt);
        Assert.DoesNotContain("https://example.test", staged
            .Where(item => item.Route.Track.Type == WindowTrack)
            .Select(item => item.Record.Value.GetProperty("window").GetProperty("title").GetString()));
    }

    [Fact]
    public void ApplicationSwitchAcceptsTheNewTitleWithoutWaiting()
    {
        var staged = new List<(SubmissionRoute Route, RecordSnapshot Record)>();
        var projector = Create(staged);
        projector.Apply(new DesktopObservation.Activity(App), Start);
        projector.Apply(new DesktopObservation.Activity(new DesktopActivitySample(
            new ForegroundApplication("macos", "bundle_id", "com.example.Other", "Other"), "Inbox")),
            Start.AddSeconds(0.2));

        var windows = Latest(staged, WindowTrack);
        Assert.Equal(2, windows.Length);
        Assert.Equal(Start.AddSeconds(0.2), windows[0].EndedAt);
        Assert.Equal(Start.AddSeconds(0.2), windows[1].StartedAt);
        Assert.Equal("Inbox", windows[1].Value.GetProperty("window").GetProperty("title").GetString());
    }

    [Fact]
    public void ApplicationSwitchDiscardsPendingTitleFromPreviousWindow()
    {
        var staged = new List<(SubmissionRoute Route, RecordSnapshot Record)>();
        var projector = Create(staged);
        var other = new DesktopActivitySample(
            new ForegroundApplication("macos", "bundle_id", "com.example.Other", "Other"), "Inbox");

        projector.Apply(new DesktopObservation.Activity(App), Start);
        projector.Apply(new DesktopObservation.Activity(App with { WindowTitle = "Loading" }),
            Start.AddSeconds(0.5));
        projector.Apply(new DesktopObservation.Activity(other), Start.AddSeconds(1));
        projector.Confirm(new DesktopSnapshot(other, []), Start.AddSeconds(2));

        var windows = Latest(staged, WindowTrack);
        Assert.Equal(2, windows.Length);
        Assert.All(windows, record => Assert.True(record.EndedAt >= record.StartedAt));
        Assert.Equal("Inbox", windows[1].Value.GetProperty("window").GetProperty("title").GetString());
        Assert.Equal(Start.AddSeconds(2), windows[1].EndedAt);
    }

    [Fact]
    public void TitleThatHeldUntilAnOutageIsStillRecorded()
    {
        var staged = new List<(SubmissionRoute Route, RecordSnapshot Record)>();
        var projector = Create(staged);
        projector.Apply(new DesktopObservation.Activity(App), Start);
        projector.Apply(new DesktopObservation.Activity(App with { WindowTitle = "Report" }), Start.AddSeconds(0.5));
        projector.Apply(new DesktopObservation.Capability(new CapabilityObservation(
            ObservationCapability.WindowTitle, ObservationState.Unavailable, "observer_failed")), Start.AddSeconds(2.5));

        var windows = Latest(staged, WindowTrack);
        Assert.Equal(2, windows.Length);
        Assert.Equal("Report", windows[1].Value.GetProperty("window").GetProperty("title").GetString());
        Assert.Equal(Start.AddSeconds(0.5), windows[1].StartedAt);
        Assert.Equal(Start.AddSeconds(2.5), windows[1].EndedAt);
    }

    [Fact]
    public void DwellOfZeroAcceptsEveryTitleChangeOnTheNextReading()
    {
        var staged = new List<(SubmissionRoute Route, RecordSnapshot Record)>();
        var projector = Create(staged, TimeSpan.Zero);
        projector.Apply(new DesktopObservation.Activity(App), Start);
        projector.Apply(new DesktopObservation.Activity(App with { WindowTitle = "Other" }), Start.AddSeconds(0.1));
        projector.Apply(new DesktopObservation.Activity(App with { WindowTitle = "Third" }), Start.AddSeconds(0.2));
        // 静置为 0 也仍然是「下一条读数才结算」：最新的标题要等下一次读数才会写出来。
        projector.Apply(new DesktopObservation.Activity(App with { WindowTitle = "Third" }), Start.AddSeconds(0.3));

        var windows = Latest(staged, WindowTrack);
        Assert.Equal(3, windows.Length);
        Assert.Equal(Start.AddSeconds(0.1), windows[1].StartedAt);
        Assert.Equal(Start.AddSeconds(0.2), windows[2].StartedAt);
    }

    [Fact]
    public void ApplicationSwitchEndsTheWindowRecordEvenWhenTheTitleRepeats()
    {
        var staged = new List<(SubmissionRoute Route, RecordSnapshot Record)>();
        var projector = Create(staged);
        projector.Apply(new DesktopObservation.Activity(App), Start);
        projector.Apply(new DesktopObservation.Activity(new DesktopActivitySample(
            new ForegroundApplication("macos", "bundle_id", "com.example.Other", "Other"), "Document")),
            Start.AddSeconds(2));

        var applications = Latest(staged, ApplicationTrack);
        Assert.Equal(2, applications.Length);
        Assert.Equal(Start.AddSeconds(2), applications[0].EndedAt);
        Assert.Equal(Start.AddSeconds(2), applications[1].StartedAt);

        var windows = Latest(staged, WindowTrack);
        Assert.Equal(2, windows.Length);
        Assert.Equal(Start.AddSeconds(2), windows[0].EndedAt);
        Assert.Equal(Start.AddSeconds(2), windows[1].StartedAt);
    }

    [Fact]
    public void WindowTitleFailureEndsTheWindowRecordAndKeepsTheApplicationRecord()
    {
        var staged = new List<(SubmissionRoute Route, RecordSnapshot Record)>();
        var projector = Create(staged);
        projector.Confirm(new DesktopSnapshot(
            App,
            [new CapabilityObservation(ObservationCapability.WindowTitle, ObservationState.Available)]), Start);
        projector.Apply(new DesktopObservation.Capability(new CapabilityObservation(
            ObservationCapability.WindowTitle, ObservationState.Unavailable, "observer_failed")), Start.AddSeconds(5));
        projector.Confirm(new DesktopSnapshot(
            App with { WindowTitle = null },
            [new CapabilityObservation(
                ObservationCapability.WindowTitle, ObservationState.Unavailable, "observer_failed")]),
            Start.AddSeconds(7));

        var application = Assert.Single(Latest(staged, ApplicationTrack));
        Assert.Equal(Start, application.StartedAt);
        Assert.Equal(Start.AddSeconds(7), application.EndedAt);

        var window = Assert.Single(Latest(staged, WindowTrack));
        Assert.Equal(Start.AddSeconds(5), window.EndedAt);
    }

    [Fact]
    public void ApplicationCapabilityFailureEndsBothObservations()
    {
        var staged = new List<(SubmissionRoute Route, RecordSnapshot Record)>();
        var projector = Create(staged);
        projector.Apply(new DesktopObservation.Activity(App), Start);
        projector.Apply(new DesktopObservation.Capability(new CapabilityObservation(
            ObservationCapability.Application, ObservationState.Unavailable, "frontmost_application_unavailable")),
            Start.AddSeconds(5));
        projector.Apply(new DesktopObservation.Activity(App), Start.AddSeconds(7));

        var applications = Latest(staged, ApplicationTrack);
        Assert.Equal(2, applications.Length);
        Assert.Equal(Start.AddSeconds(5), applications[0].EndedAt);
        Assert.Equal(Start.AddSeconds(7), applications[1].StartedAt);
        Assert.Equal(Start.AddSeconds(5), Latest(staged, WindowTrack)[0].EndedAt);
    }

    [Fact]
    public void ApplicationAndWindowRecordsCarryTheirOwnObservation()
    {
        var staged = new List<(SubmissionRoute Route, RecordSnapshot Record)>();
        var projector = Create(staged);
        projector.Apply(new DesktopObservation.Activity(App), Start);

        var application = Assert.Single(Latest(staged, ApplicationTrack));
        Assert.False(application.Value.TryGetProperty("window", out _));
        Assert.Equal("com.example.App", application.Value.GetProperty("application").GetProperty("id").GetString());

        var window = Assert.Single(Latest(staged, WindowTrack));
        Assert.Equal("device-a", window.Value.GetProperty("device_id").GetString());
        Assert.Equal("Document", window.Value.GetProperty("window").GetProperty("title").GetString());
    }

    [Fact]
    public void DelayedConfirmationCreatesObservationGapEvenWhenApplicationMatches()
    {
        var staged = new List<(SubmissionRoute Route, RecordSnapshot Record)>();
        var projector = Create(staged);
        projector.Apply(new DesktopObservation.Activity(App), Start);
        projector.Apply(new DesktopObservation.Activity(App), Start.AddSeconds(11));

        var records = Latest(staged, ApplicationTrack);
        Assert.Equal(2, records.Length);
        Assert.Equal(Start, records[0].EndedAt);
        Assert.Equal(Start.AddSeconds(11), records[1].StartedAt);
    }

    [Theory]
    [InlineData("empty_sample")]
    [InlineData("capability_failure")]
    [InlineData("away")]
    public void DelayedBreakDoesNotExtendApplicationAcrossObservationGap(string cause)
    {
        var staged = new List<(SubmissionRoute Route, RecordSnapshot Record)>();
        var projector = Create(staged);
        projector.Apply(new DesktopObservation.Activity(App), Start);
        var delayed = Start.AddSeconds(11);

        Break(projector, cause, delayed);

        var application = Assert.Single(Latest(staged, ApplicationTrack));
        Assert.Equal(Start, application.EndedAt);
    }

    [Theory]
    [InlineData("empty_sample")]
    [InlineData("capability_failure")]
    [InlineData("away")]
    public void TimelyBreakClosesApplicationAtObservedTransition(string cause)
    {
        var staged = new List<(SubmissionRoute Route, RecordSnapshot Record)>();
        var projector = Create(staged);
        projector.Apply(new DesktopObservation.Activity(App), Start);
        var timely = Start.AddSeconds(5);

        Break(projector, cause, timely);

        var application = Assert.Single(Latest(staged, ApplicationTrack));
        Assert.Equal(timely, application.EndedAt);
    }

    [Fact]
    public void DelayedSnapshotFailureAndRecoveryLeaveGapBetweenApplicationRecords()
    {
        var staged = new List<(SubmissionRoute Route, RecordSnapshot Record)>();
        var projector = Create(staged);
        projector.Confirm(new DesktopSnapshot(
            App,
            [new CapabilityObservation(ObservationCapability.WindowTitle, ObservationState.Available)]), Start);
        projector.Confirm(new DesktopSnapshot(
            null,
            [new CapabilityObservation(
                ObservationCapability.WindowTitle, ObservationState.Unavailable, "observer_failed")]),
            Start.AddSeconds(11));
        projector.Apply(new DesktopObservation.Activity(App with { WindowTitle = null }), Start.AddSeconds(12));

        AssertApplicationGap(staged, Start.AddSeconds(12));
    }

    [Fact]
    public void DelayedAwaySignalAndRecoveryLeaveGapBeforeFreshApplication()
    {
        var staged = new List<(SubmissionRoute Route, RecordSnapshot Record)>();
        var projector = Create(staged);
        projector.Apply(new DesktopObservation.Activity(App), Start);
        projector.Apply(new DesktopObservation.AwayEntered(
            DesktopAwayReason.SystemSleep), Start.AddSeconds(11));
        projector.Apply(new DesktopObservation.AwayExited(
            DesktopAwayReason.SystemSleep, App), Start.AddSeconds(12));

        AssertApplicationGap(staged, Start.AddSeconds(12));
    }

    [Fact]
    public void OverlappingAwayReasonsRemainIndependentAndRecoveryStartsFreshApplication()
    {
        var staged = new List<(SubmissionRoute Route, RecordSnapshot Record)>();
        var projector = Create(staged);
        projector.Apply(new DesktopObservation.Activity(App), Start);
        projector.Apply(new DesktopObservation.AwayEntered(DesktopAwayReason.ScreenLocked), Start.AddSeconds(1));
        projector.Apply(new DesktopObservation.AwayEntered(DesktopAwayReason.SystemSleep), Start.AddSeconds(2));
        projector.Apply(new DesktopObservation.AwayExited(DesktopAwayReason.ScreenLocked, App), Start.AddSeconds(3));
        projector.Apply(new DesktopObservation.AwayExited(DesktopAwayReason.SystemSleep, App), Start.AddSeconds(4));

        var away = Latest(staged, "desktop.system.away");
        Assert.Equal(2, away.Length);
        Assert.Equal(Start.AddSeconds(3), away.Single(record => record.StartedAt == Start.AddSeconds(1)).EndedAt);
        Assert.Equal(Start.AddSeconds(4), away.Single(record => record.StartedAt == Start.AddSeconds(2)).EndedAt);
        Assert.Equal(2, Latest(staged, ApplicationTrack).Length);
    }

    [Fact]
    public void KeyRepeatIsRemovedButScrollDeltaIsPreserved()
    {
        var staged = new List<(SubmissionRoute Route, RecordSnapshot Record)>();
        var projector = Create(staged);
        projector.Apply(new DesktopObservation.Input(new DesktopInputObservation(DesktopInputKind.KeyDown, 4)), Start);
        projector.Apply(new DesktopObservation.Input(new DesktopInputObservation(DesktopInputKind.KeyDown, 4)), Start.AddMilliseconds(1));
        projector.Apply(new DesktopObservation.Input(new DesktopInputObservation(DesktopInputKind.KeyUp, 4)), Start.AddMilliseconds(2));
        projector.Apply(new DesktopObservation.Input(new DesktopInputObservation(DesktopInputKind.KeyDown, 4)), Start.AddMilliseconds(3));
        projector.Apply(new DesktopObservation.Input(new DesktopInputObservation(
            DesktopInputKind.Scroll, DeltaY: -3.5, ScrollUnit: ScrollUnit.Point)), Start.AddMilliseconds(4));

        var input = Latest(staged, "desktop.input.event");
        Assert.Equal(3, input.Length);
        var scroll = input.Single(record => record.Value.GetProperty("kind").GetString() == "scroll");
        Assert.Equal(-3.5, scroll.Value.GetProperty("delta_y").GetDouble());
        Assert.Equal("point", scroll.Value.GetProperty("unit").GetString());
    }

    [Fact]
    public void CapabilityStatesAreRecordedWithoutClaimingCompleteness()
    {
        var staged = new List<(SubmissionRoute Route, RecordSnapshot Record)>();
        var projector = Create(staged);
        projector.Apply(new DesktopObservation.Capability(new CapabilityObservation(
            ObservationCapability.Input, ObservationState.PermissionRequired, "input_monitoring")), Start);
        projector.Apply(new DesktopObservation.Capability(new CapabilityObservation(
            ObservationCapability.Input, ObservationState.Available)), Start.AddSeconds(3));

        var statuses = Latest(staged, "desktop.observation.status");
        Assert.Equal(2, statuses.Length);
        Assert.Contains(statuses, record => record.Value.GetProperty("state").GetString() == "permission_required");
        Assert.Contains(statuses, record => record.Value.GetProperty("state").GetString() == "available");
    }

    private static void Break(DesktopRecordProjector projector, string cause, DateTimeOffset at)
    {
        switch (cause)
        {
            case "empty_sample":
                projector.Apply(new DesktopObservation.Activity(null), at);
                break;
            case "capability_failure":
                projector.Apply(new DesktopObservation.Capability(new CapabilityObservation(
                    ObservationCapability.Application, ObservationState.Unavailable, "application_identity_unavailable")), at);
                break;
            case "away":
                projector.Apply(new DesktopObservation.AwayEntered(DesktopAwayReason.SystemSleep), at);
                break;
        }
    }

    private static void AssertApplicationGap(List<(SubmissionRoute Route, RecordSnapshot Record)> staged, DateTimeOffset resumedAt)
    {
        var records = Latest(staged, ApplicationTrack);
        Assert.Equal(2, records.Length);
        Assert.Equal(Start, records[0].EndedAt);
        Assert.Equal(resumedAt, records[1].StartedAt);
    }

    private static DesktopRecordProjector Create(
        List<(SubmissionRoute Route, RecordSnapshot Record)> staged,
        TimeSpan? windowTitleDwell = null) =>
        new("heartbeat.collector.desktop.macos", "device-a", "Mac", TimeSpan.FromSeconds(10), windowTitleDwell ?? Dwell,
            (route, record) => staged.Add((route, record)));

    private static RecordSnapshot[] Latest(
        IEnumerable<(SubmissionRoute Route, RecordSnapshot Record)> staged,
        string trackType) => staged
        .Where(item => item.Route.Track.Type == trackType)
        .GroupBy(item => item.Record.Id)
        .Select(group => group.Last().Record)
        .OrderBy(record => record.StartedAt)
        .ToArray();
}
