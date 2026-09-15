using Heartbeat.Collector.Desktop.Mac;
using Heartbeat.Hub;

namespace Heartbeat.Collector.Desktop.Mac.Tests;

public sealed class DesktopRecordProjectorTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 14, 8, 0, 0, TimeSpan.Zero);
    private static readonly DesktopActivitySample App = new(
        new ForegroundApplication("macos", "bundle_id", "com.example.App", "Example"), "Document");

    [Fact]
    public void TitleAndWindowTransitionsAlwaysCreateNewRecordsWithoutClickGate()
    {
        var staged = new List<(SubmissionRoute Route, RecordSnapshot Record)>();
        var projector = Create(staged);
        projector.Apply(new MacSystemObservation.Activity(App, ActivityChangeKind.Confirmation), Start);
        projector.Apply(new MacSystemObservation.Activity(App with { WindowTitle = "Other" }, ActivityChangeKind.TitleChanged),
            Start.AddSeconds(1));
        projector.Apply(new MacSystemObservation.Activity(App with { WindowTitle = "Other" }, ActivityChangeKind.FocusedWindowChanged),
            Start.AddSeconds(2));

        var records = Latest(staged, "desktop.application.foreground");
        Assert.Equal(3, records.Length);
        Assert.Equal(3, records.Select(record => record.Id).Distinct().Count());
        Assert.Equal(Start.AddSeconds(1), records[0].EndedAt);
    }

    [Fact]
    public void DelayedConfirmationCreatesObservationGapEvenWhenApplicationMatches()
    {
        var staged = new List<(SubmissionRoute Route, RecordSnapshot Record)>();
        var projector = Create(staged);
        projector.Apply(new MacSystemObservation.Activity(App, ActivityChangeKind.Confirmation), Start);
        projector.Apply(new MacSystemObservation.Activity(App, ActivityChangeKind.Confirmation), Start.AddSeconds(11));

        var records = Latest(staged, "desktop.application.foreground");
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
        projector.Apply(new MacSystemObservation.Activity(App, ActivityChangeKind.Confirmation), Start);
        var delayed = Start.AddSeconds(11);

        switch (cause)
        {
            case "empty_sample":
                projector.Apply(new MacSystemObservation.Activity(
                    null, ActivityChangeKind.Confirmation), delayed);
                break;
            case "capability_failure":
                projector.Apply(new MacSystemObservation.Capability(new CapabilityObservation(
                    ObservationCapability.WindowTitle, ObservationState.Unavailable, "observer_failed")), delayed);
                break;
            case "away":
                projector.Apply(new MacSystemObservation.AwayEntered(MacAwayReason.SystemSleep), delayed);
                break;
        }

        var application = Assert.Single(Latest(staged, "desktop.application.foreground"));
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
        projector.Apply(new MacSystemObservation.Activity(App, ActivityChangeKind.Confirmation), Start);
        var timely = Start.AddSeconds(5);

        switch (cause)
        {
            case "empty_sample":
                projector.Apply(new MacSystemObservation.Activity(
                    null, ActivityChangeKind.Confirmation), timely);
                break;
            case "capability_failure":
                projector.Apply(new MacSystemObservation.Capability(new CapabilityObservation(
                    ObservationCapability.WindowTitle, ObservationState.Unavailable, "observer_failed")), timely);
                break;
            case "away":
                projector.Apply(new MacSystemObservation.AwayEntered(MacAwayReason.SystemSleep), timely);
                break;
        }

        var application = Assert.Single(Latest(staged, "desktop.application.foreground"));
        Assert.Equal(timely, application.EndedAt);
    }

    [Fact]
    public void DelayedSnapshotFailureAndRecoveryLeaveGapBetweenApplicationRecords()
    {
        var staged = new List<(SubmissionRoute Route, RecordSnapshot Record)>();
        var projector = Create(staged);
        projector.Confirm(new MacSystemSnapshot(
            App,
            [new CapabilityObservation(ObservationCapability.WindowTitle, ObservationState.Available)]), Start);
        projector.Confirm(new MacSystemSnapshot(
            null,
            [new CapabilityObservation(
                ObservationCapability.WindowTitle, ObservationState.Unavailable, "observer_failed")]),
            Start.AddSeconds(11));
        projector.Apply(new MacSystemObservation.Activity(
            App with { WindowTitle = null }, ActivityChangeKind.Recovery), Start.AddSeconds(12));

        var records = Latest(staged, "desktop.application.foreground");
        Assert.Equal(2, records.Length);
        Assert.Equal(Start, records[0].EndedAt);
        Assert.Equal(Start.AddSeconds(12), records[1].StartedAt);
    }

    [Fact]
    public void DelayedAwaySignalAndRecoveryLeaveGapBeforeFreshApplication()
    {
        var staged = new List<(SubmissionRoute Route, RecordSnapshot Record)>();
        var projector = Create(staged);
        projector.Apply(new MacSystemObservation.Activity(App, ActivityChangeKind.Confirmation), Start);
        projector.Apply(new MacSystemObservation.AwayEntered(
            MacAwayReason.SystemSleep), Start.AddSeconds(11));
        projector.Apply(new MacSystemObservation.AwayExited(
            MacAwayReason.SystemSleep, App), Start.AddSeconds(12));

        var records = Latest(staged, "desktop.application.foreground");
        Assert.Equal(2, records.Length);
        Assert.Equal(Start, records[0].EndedAt);
        Assert.Equal(Start.AddSeconds(12), records[1].StartedAt);
    }

    [Fact]
    public void OverlappingAwayReasonsRemainIndependentAndRecoveryStartsFreshApplication()
    {
        var staged = new List<(SubmissionRoute Route, RecordSnapshot Record)>();
        var projector = Create(staged);
        projector.Apply(new MacSystemObservation.Activity(App, ActivityChangeKind.Confirmation), Start);
        projector.Apply(new MacSystemObservation.AwayEntered(MacAwayReason.ScreenLocked), Start.AddSeconds(1));
        projector.Apply(new MacSystemObservation.AwayEntered(MacAwayReason.SystemSleep), Start.AddSeconds(2));
        projector.Apply(new MacSystemObservation.AwayExited(MacAwayReason.ScreenLocked, App), Start.AddSeconds(3));
        projector.Apply(new MacSystemObservation.AwayExited(MacAwayReason.SystemSleep, App), Start.AddSeconds(4));

        var away = Latest(staged, "desktop.system.away");
        Assert.Equal(2, away.Length);
        Assert.Equal(Start.AddSeconds(3), away.Single(record => record.StartedAt == Start.AddSeconds(1)).EndedAt);
        Assert.Equal(Start.AddSeconds(4), away.Single(record => record.StartedAt == Start.AddSeconds(2)).EndedAt);
        Assert.Equal(2, Latest(staged, "desktop.application.foreground").Length);
    }

    [Fact]
    public void KeyRepeatIsRemovedButScrollDeltaIsPreserved()
    {
        var staged = new List<(SubmissionRoute Route, RecordSnapshot Record)>();
        var projector = Create(staged);
        projector.Apply(new MacSystemObservation.Input(new DesktopInputObservation(DesktopInputKind.KeyDown, 4)), Start);
        projector.Apply(new MacSystemObservation.Input(new DesktopInputObservation(DesktopInputKind.KeyDown, 4)), Start.AddMilliseconds(1));
        projector.Apply(new MacSystemObservation.Input(new DesktopInputObservation(DesktopInputKind.KeyUp, 4)), Start.AddMilliseconds(2));
        projector.Apply(new MacSystemObservation.Input(new DesktopInputObservation(DesktopInputKind.KeyDown, 4)), Start.AddMilliseconds(3));
        projector.Apply(new MacSystemObservation.Input(new DesktopInputObservation(
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
        projector.Apply(new MacSystemObservation.Capability(new CapabilityObservation(
            ObservationCapability.Input, ObservationState.PermissionRequired, "input_monitoring")), Start);
        projector.Apply(new MacSystemObservation.Capability(new CapabilityObservation(
            ObservationCapability.Input, ObservationState.Available)), Start.AddSeconds(3));

        var statuses = Latest(staged, "desktop.observation.status");
        Assert.Equal(2, statuses.Length);
        Assert.Contains(statuses, record => record.Value.GetProperty("state").GetString() == "permission_required");
        Assert.Contains(statuses, record => record.Value.GetProperty("state").GetString() == "available");
    }

    private static DesktopRecordProjector Create(List<(SubmissionRoute Route, RecordSnapshot Record)> staged) =>
        new("device-a", "Mac", TimeSpan.FromSeconds(10), (route, record) => staged.Add((route, record)));

    private static RecordSnapshot[] Latest(
        IEnumerable<(SubmissionRoute Route, RecordSnapshot Record)> staged,
        string trackType) => staged
        .Where(item => item.Route.Track.Type == trackType)
        .GroupBy(item => item.Record.Id)
        .Select(group => group.Last().Record)
        .OrderBy(record => record.StartedAt)
        .ToArray();
}
