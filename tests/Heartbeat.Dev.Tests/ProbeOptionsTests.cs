using Heartbeat.Dev;

namespace Heartbeat.Dev.Tests;

public sealed class ProbeOptionsTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 16, 4, 33, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = new(2026, 9, 16, 4, 50, 0, TimeSpan.Zero);

    [Fact]
    public void ExportsUpToNowWhenNoEndIsGiven()
    {
        var options = ProbeOptions.Create(database: true, since: Start);

        Assert.NotNull(options.Database);
        Assert.True(options.Database.Until > options.Database.Since);
    }

    [Fact]
    public void NeedsAStartBeforeItWillReadTheDatabase()
    {
        var error = Assert.Throws<CommandUsageException>(() =>
            ProbeOptions.Create(database: true));

        Assert.Contains("--since", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsAWindowThatEndsBeforeItStarts()
    {
        var error = Assert.Throws<CommandUsageException>(() => ProbeOptions.Create(database: true, since: End, until: Start));

        Assert.Contains("before", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesToTakeReadingsFromTwoSourcesAtOnce()
    {
        var readings = Path.Combine(Path.GetTempPath(), $"probe-{Guid.NewGuid():N}.json");
        File.WriteAllText(readings, "{}");
        try
        {
            var error = Assert.Throws<CommandUsageException>(() => ProbeOptions.Create(database: true, since: Start, readings: readings));

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
            ProbeOptions.Create(since: Start));

        Assert.Contains("--from-database", error.Message, StringComparison.Ordinal);
    }
}
