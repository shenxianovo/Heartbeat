using Heartbeat.Dev;

namespace Heartbeat.Dev.Tests;

public sealed class WindowTitleChurnTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 16, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RepeatedPollReadingsAreOneObservedValue()
    {
        var report = WindowTitleChurn.Analyze(
        [
            Reading(0, "com.example.Editor", "Report.txt"),
            Reading(0.25, "com.example.Editor", "Report.txt"),
            Reading(0.5, "com.example.Editor", "Report.txt"),
            Reading(0.75, "com.example.Editor", "Report.txt"),
        ]);

        Assert.Equal(4, report.Readings);
        Assert.Equal(1, report.ObservedValues);
        Assert.Equal(0, report.TitleChanges);
        Assert.All(report.Dwells, dwell => Assert.Equal(1, dwell.WindowRecords));
    }

    [Fact]
    public void AnimatedTitlesShowUpAsSubSecondTitleChanges()
    {
        var report = WindowTitleChurn.Analyze(Marquee(steps: 9).ToArray());

        Assert.Equal(0, report.ApplicationSwitches);
        Assert.Equal(9, report.TitleChanges);
        Assert.Equal(9, report.TitleChangesWithinOneSecond);
        Assert.Equal(9, report.RotationLikeChanges);
        Assert.Equal(9, report.SameLengthChanges);
        Assert.Equal(0.2, report.ShortestHoldSeconds!.Value, 3);
        var churn = Assert.Single(report.Applications);
        Assert.Equal("com.example.Player", churn.Application);
        Assert.Equal(9, churn.TitleChanges);
    }

    [Fact]
    public void DwellSuppressesAnimationAndStillCommitsAStableTitle()
    {
        // The tail value keeps being confirmed by polling, which is how a real probe ends.
        var readings = Marquee(steps: 8)
            .Append(Reading(2, "com.example.Player", "Now Playing - Rest"))
            .Append(Reading(12, "com.example.Player", "Now Playing - Rest"))
            .ToArray();

        var second = Single(WindowTitleChurn.Analyze(readings, [1]).Dwells);
        var quarter = Single(WindowTitleChurn.Analyze(readings, [0.25]).Dwells);

        Assert.Equal(2, second.WindowRecords);
        Assert.Equal(8, second.SuppressedChanges);
        Assert.Equal(1.8, second.UnstableSeconds, 3);
        Assert.Equal(1.8, second.LongestUnstableStretchSeconds, 3);
        Assert.Equal(3, quarter.WindowRecords);
    }

    [Fact]
    public void SwitchingApplicationIsCommittedNoMatterHowBrieflyItIsHeld()
    {
        var report = WindowTitleChurn.Analyze(
        [
            Reading(0, "com.example.Editor", "Report.txt"),
            Reading(1, "com.example.Launcher", "Spotlight"),
            Reading(1.1, "com.example.Editor", "Report.txt"),
        ], [5]);

        Assert.Equal(2, report.ApplicationSwitches);
        Assert.Equal(0, report.TitleChanges);
        Assert.Equal(3, Single(report.Dwells).WindowRecords);
    }

    [Fact]
    public void ChangesThatOnlyPollingSawAreCountedSeparately()
    {
        var report = WindowTitleChurn.Analyze(
        [
            Reading(0, "com.example.Editor", "Report.txt", origin: "event"),
            Reading(1, "com.example.Editor", "Report.txt (edited)"),
            Reading(2, "com.example.Editor", "Notes.txt", origin: "event"),
        ]);

        Assert.Equal(2, report.TitleChanges);
        Assert.Equal(1, report.TitleChangesFirstSeenByPoll);
    }

    [Fact]
    public void MissingTitlesAreCountedRatherThanTreatedAsAnEmptyTitle()
    {
        var report = WindowTitleChurn.Analyze(
        [
            Reading(0, "com.example.Editor", "Report.txt"),
            new WindowTitleReading(Start.AddSeconds(1), "poll", "com.example.Editor", null, 0, false),
        ]);

        Assert.Equal(1, report.TitleUnavailableReadings);
        Assert.Equal(1, report.TitleChanges);
        Assert.False(WindowTitleChurn.TitleWasNeverReadable(report));
    }

    [Fact]
    public void AProbeThatNeverReadATitleIsNotAProbeWithoutChurn()
    {
        var report = WindowTitleChurn.Analyze(
        [
            new WindowTitleReading(Start, "poll", "com.example.Editor", null, 0, false),
            new WindowTitleReading(Start.AddSeconds(1), "poll", "com.example.Editor", null, 0, false),
        ]);

        Assert.Equal(0, report.TitleChanges);
        Assert.True(WindowTitleChurn.TitleWasNeverReadable(report));
    }

    [Fact]
    public void AnEmptyProbeSupportsNoConclusion()
    {
        Assert.Throws<InvalidOperationException>(() => WindowTitleChurn.Analyze([]));
    }

    private static IEnumerable<WindowTitleReading> Marquee(int steps)
    {
        const string title = "Now Playing - Song";
        yield return Reading(0, "com.example.Player", title, origin: "event");
        for (var step = 1; step <= steps; step++)
        {
            var rotated = string.Concat(title.AsSpan(step % title.Length), title.AsSpan(0, step % title.Length));
            yield return Reading(0.2 * step, "com.example.Player", rotated, origin: "event", rotation: true);
        }
    }

    private static WindowTitleReading Reading(
        double second,
        string application,
        string title,
        string origin = "poll",
        bool rotation = false) =>
        new(Start.AddSeconds(second), origin, application, title, title.Length, rotation);

    private static DwellOutcome Single(IReadOnlyList<DwellOutcome> dwells) => dwells.Single();
}
