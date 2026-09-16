using Heartbeat.Dev;

namespace Heartbeat.Dev.Tests;

public sealed class ProbeOptionsTests
{
    [Fact]
    public void FallsBackToDefaultDwellCandidatesWhenNoneAreRequested()
    {
        var options = ProbeOptions.Parse(["window-title"]);

        Assert.Null(options.Dwells);
    }

    [Fact]
    public void ReadsDwellCandidatesAsASecondsList()
    {
        var options = ProbeOptions.Parse(["window-title", "--dwell-seconds", "1, 1.5,2"]);

        Assert.Equal([1, 1.5, 2], options.Dwells);
    }

    [Fact]
    public void RejectsDwellCandidatesThatCannotDelayARecord()
    {
        var error = Assert.Throws<CommandUsageException>(() =>
            ProbeOptions.Parse(["window-title", "--dwell-seconds", "1,0"]));

        Assert.Contains("positive", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadsADatabaseWindowFromLocalTimestamps()
    {
        var options = ProbeOptions.Parse(
            ["window-title", "--from-database", "--since", "2026-09-16T04:33:00Z", "--until", "2026-09-16T04:50:00Z"]);

        Assert.Equal(new DateTimeOffset(2026, 9, 16, 4, 33, 0, TimeSpan.Zero), options.Database?.Since);
        Assert.Equal(new DateTimeOffset(2026, 9, 16, 4, 50, 0, TimeSpan.Zero), options.Database?.Until);
    }

    [Fact]
    public void ExportsUpToNowWhenNoEndIsGiven()
    {
        var options = ProbeOptions.Parse(["window-title", "--from-database", "--since", "2026-09-16T04:33:00Z"]);

        Assert.NotNull(options.Database);
        Assert.True(options.Database.Until > options.Database.Since);
    }

    [Fact]
    public void NeedsAStartBeforeItWillReadTheDatabase()
    {
        var error = Assert.Throws<CommandUsageException>(() =>
            ProbeOptions.Parse(["window-title", "--from-database"]));

        Assert.Contains("--since", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsAWindowThatEndsBeforeItStarts()
    {
        var error = Assert.Throws<CommandUsageException>(() => ProbeOptions.Parse(
            ["window-title", "--from-database", "--since", "2026-09-16T04:50:00Z", "--until", "2026-09-16T04:33:00Z"]));

        Assert.Contains("before", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesToTakeReadingsFromTwoSourcesAtOnce()
    {
        var readings = Path.Combine(Path.GetTempPath(), $"probe-{Guid.NewGuid():N}.json");
        File.WriteAllText(readings, "{}");
        try
        {
            var error = Assert.Throws<CommandUsageException>(() => ProbeOptions.Parse(
                ["window-title", "--from-database", "--since", "2026-09-16T04:33:00Z", "--readings", readings]));

            Assert.Contains("Choose one source", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(readings);
        }
    }

    [Fact]
    public void RejectsAWindowWithoutADatabaseToReadItFrom()
    {
        var error = Assert.Throws<CommandUsageException>(() =>
            ProbeOptions.Parse(["window-title", "--since", "2026-09-16T04:33:00Z"]));

        Assert.Contains("--from-database", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsATimestampItCannotUnderstand()
    {
        var error = Assert.Throws<CommandUsageException>(() =>
            ProbeOptions.Parse(["window-title", "--from-database", "--since", "yesterday"]));

        Assert.Contains("timestamp", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ObservesThisMacWhenNoOtherSourceIsNamed()
    {
        var options = ProbeOptions.Parse(["window-title"]);

        Assert.Null(options.Database);
        Assert.Null(options.Readings);
    }
}
