using Heartbeat.Dev;

namespace Heartbeat.Dev.Tests;

public sealed class DatabaseReadingsTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 16, 4, 33, 0, TimeSpan.Zero);

    [Fact]
    public void AsksForBothTheCurrentAndTheLegacyShapeOfTheTitle()
    {
        var query = DatabaseReadings.Query(new DatabaseWindow(Noon, Noon.AddMinutes(17)));

        Assert.Contains("'desktop.window.foreground'", query, StringComparison.Ordinal);
        Assert.Contains("r.value ? 'window'", query, StringComparison.Ordinal);
        Assert.Contains("timestamptz '2026-09-16 04:33:00.000000+00'", query, StringComparison.Ordinal);
        Assert.Contains("timestamptz '2026-09-16 04:50:00.000000+00'", query, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadsTheRowsPostgresPrintsAsOneJsonValue()
    {
        var records = DatabaseReadings.Parse("""
            [{"startedAt":"2026-09-16T04:33:00.000000Z","endedAt":"2026-09-16T04:33:04.000000Z","application":"com.apple.Terminal","title":"build"},
             {"startedAt":"2026-09-16T04:33:04.000000Z","endedAt":"2026-09-16T04:33:09.000000Z","application":"com.apple.Terminal","title":null}]
            """);

        Assert.Equal(2, records.Length);
        Assert.Equal("com.apple.Terminal", records[0].Application);
        Assert.Equal("build", records[0].Title);
        Assert.Null(records[1].Title);
    }

    [Fact]
    public void TreatsSilenceFromPostgresAsNoRecords()
    {
        Assert.Empty(DatabaseReadings.Parse("  \n"));
    }

    [Fact]
    public void RefusesToReportOnAWindowWithNoRecords()
    {
        var error = Assert.Throws<CommandUsageException>(() => DatabaseReadings.ToDocument([], includeTitles: false));

        Assert.Contains("No foreground window records", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadsEachRecordBoundaryPlusTheEndOfTheLastOne()
    {
        var document = DatabaseReadings.ToDocument(
            [
                new WindowRecord(Noon, Noon.AddSeconds(4), "com.apple.Terminal", "build"),
                new WindowRecord(Noon.AddSeconds(4), Noon.AddSeconds(9), "com.apple.Terminal", "build - done"),
            ],
            includeTitles: false);

        Assert.Equal([Noon, Noon.AddSeconds(4), Noon.AddSeconds(9)], document.Readings.Select(reading => reading.At));
        Assert.All(document.Readings, reading => Assert.True(reading.TitleAvailable));
    }

    [Fact]
    public void DoesNotInventAnOutageBetweenRecordsThatHandOver()
    {
        // 相邻 Record 的首尾差在毫秒级以下是同一次交接，不是观测中断。
        var document = DatabaseReadings.ToDocument(
            [
                new WindowRecord(Noon, Noon.AddTicks(41), "com.apple.Terminal", "build"),
                new WindowRecord(Noon.AddTicks(42), Noon.AddSeconds(9), "com.apple.Terminal", "build - done"),
            ],
            includeTitles: false);

        Assert.DoesNotContain(document.Readings, reading => reading.TitleHash is null);
    }

    [Fact]
    public void ReportsARealSilenceBetweenRecordsAsNoTitleAtAll()
    {
        var document = DatabaseReadings.ToDocument(
            [
                new WindowRecord(Noon, Noon.AddSeconds(4), "com.apple.Terminal", "build"),
                new WindowRecord(Noon.AddSeconds(30), Noon.AddSeconds(40), "com.apple.Terminal", "build"),
            ],
            includeTitles: false);

        var gap = Assert.Single(document.Readings, reading => !reading.TitleAvailable);
        Assert.Equal(Noon.AddSeconds(4), gap.At);
        Assert.Null(gap.Application);
        // 中断之后的标题不能算成「一直没变」，否则那段静默会被当作稳定的证据。
        Assert.False(document.Readings.Last(reading => reading.At == Noon.AddSeconds(30)).SameTitleAsPrevious);
    }

    [Fact]
    public void FingerprintsTitlesInsteadOfKeepingThem()
    {
        var document = DatabaseReadings.ToDocument(
            [new WindowRecord(Noon, Noon.AddSeconds(4), "com.apple.Terminal", "invoice-2026.pdf")],
            includeTitles: false);

        var reading = document.Readings[0];
        Assert.False(document.IncludesTitles);
        Assert.Null(reading.Title);
        Assert.Equal(16, reading.TitleLength);
        Assert.NotNull(reading.TitleHash);
    }

    [Fact]
    public void KeepsTitlesOnlyWhenSensitiveEvidenceIsRequested()
    {
        var document = DatabaseReadings.ToDocument(
            [new WindowRecord(Noon, Noon.AddSeconds(4), "com.apple.Terminal", "invoice-2026.pdf")],
            includeTitles: true);

        Assert.True(document.IncludesTitles);
        Assert.Equal("invoice-2026.pdf", document.Readings[0].Title);
    }

    [Fact]
    public void RecognisesAScrollingTitleAsARotationOfThePreviousOne()
    {
        var document = DatabaseReadings.ToDocument(
            [
                new WindowRecord(Noon, Noon.AddSeconds(1), "com.spotify.client", "now playing - "),
                new WindowRecord(Noon.AddSeconds(1), Noon.AddSeconds(2), "com.spotify.client", "ow playing - n"),
            ],
            includeTitles: false);

        Assert.True(document.Readings[1].RotationOfPrevious);
        Assert.True(document.Readings[1].SameApplicationAsPrevious);
        Assert.False(document.Readings[1].SameTitleAsPrevious);
    }

    [Fact]
    public void OrdersRecordsByTimeSoTheReportSeesOneTimeline()
    {
        var document = DatabaseReadings.ToDocument(
            [
                new WindowRecord(Noon.AddSeconds(4), Noon.AddSeconds(9), "com.google.Chrome", "inbox"),
                new WindowRecord(Noon, Noon.AddSeconds(4), "com.apple.Terminal", "build"),
            ],
            includeTitles: false);

        Assert.Equal([Noon, Noon.AddSeconds(4), Noon.AddSeconds(9)], document.Readings.Select(reading => reading.At));
        Assert.Equal("com.apple.Terminal", document.Readings[0].Application);
        Assert.False(document.Readings[1].SameApplicationAsPrevious);
    }
}
