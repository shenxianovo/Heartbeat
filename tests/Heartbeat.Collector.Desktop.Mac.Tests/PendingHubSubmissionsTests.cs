using System.Text.Json;
using Heartbeat.Collector.Desktop.Mac;
using Heartbeat.Hub;

namespace Heartbeat.Collector.Desktop.Mac.Tests;

public sealed class PendingHubSubmissionsTests(ITestOutputHelper output)
{
    private static readonly SubmissionRoute Route = new(
        new CollectorDeclaration("heartbeat.collector.desktop.macos", "device-a", "Mac"),
        new TrackDeclaration("example.point", 1, "point", null));

    [Fact]
    public void GroupsRecordsByTrackAndBatchesFiveHundredAtATime()
    {
        var pending = new PendingHubSubmissions();
        for (var index = 0; index < 501; index++)
        {
            pending.Stage(Route, Point(index));
        }

        var batches = pending.ReadBatches();

        Assert.Equal([500, 1], batches.Select(batch => batch.Records.Count));
    }

    [Fact]
    public void OldReceiptCannotClearANewerRangeSnapshot()
    {
        var pending = new PendingHubSubmissions();
        var start = new DateTimeOffset(2026, 9, 14, 8, 0, 0, TimeSpan.Zero);
        var record = new RecordSnapshot(Guid.CreateVersion7(start), start, start, null,
            JsonSerializer.SerializeToElement(new { value = 1 }));
        var route = Route with { Track = new TrackDeclaration("example.range", 1, "range", "explicit") };
        pending.Stage(route, record);
        var sent = Assert.Single(pending.ReadBatches());
        pending.Stage(route, record with { EndedAt = start.AddSeconds(5) });

        pending.Confirm(sent);

        var retained = Assert.Single(Assert.Single(pending.ReadBatches()).Records);
        Assert.Equal(start.AddSeconds(5), retained.EndedAt);
    }

    [Fact]
    public void SplitsBatchesBeforeTheOneMegabyteHubLimit()
    {
        var pending = new PendingHubSubmissions();
        var payload = new string('x', 600_000);
        var first = Point(1) with { Value = JsonSerializer.SerializeToElement(new { payload }) };
        var second = Point(2) with { Value = JsonSerializer.SerializeToElement(new { payload }) };
        pending.Stage(Route, first);
        pending.Stage(Route, second);

        Assert.Equal([1, 1], pending.ReadBatches().Select(batch => batch.Records.Count));
    }

    [Fact]
    public void PreparingOneBatchDoesNotAllocateHundredsOfCopiesOfItsPayload()
    {
        var pending = new PendingHubSubmissions();
        for (var index = 0; index < 500; index++) pending.Stage(Route, Point(index));
        _ = pending.ReadBatches(); // Warm serializer metadata and buffers before measuring.
        var before = GC.GetAllocatedBytesForCurrentThread();

        var batches = pending.ReadBatches();

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Single(batches);
        output.WriteLine($"Preparing 500 small Records allocated {allocated:N0} bytes.");
        Assert.True(allocated < 10_000_000, $"Preparing 500 small Records allocated {allocated:N0} bytes.");
    }

    [Fact]
    public void ExactByteLimitIncludesEnvelopeEscapingAndSeparators()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var route = Route with { Collector = Route.Collector with { DisplayName = "Mac \" 中文" } };
        var record = Point(0) with { Value = JsonSerializer.SerializeToElement(new { payload = "" }) };
        var overhead = JsonSerializer.SerializeToUtf8Bytes(
            new HubSubmission(route.Collector, route.Track, [record]), options).Length;
        record = record with { Value = JsonSerializer.SerializeToElement(new { payload = new string('x', 1_048_576 - overhead) }) };
        var pending = new PendingHubSubmissions();
        pending.Stage(route, record);
        pending.Stage(route, Point(1));

        var batches = pending.ReadBatches();

        Assert.Equal([1, 1], batches.Select(batch => batch.Records.Count));
        Assert.Equal(1_048_576, JsonSerializer.SerializeToUtf8Bytes(batches[0].ToSubmission(), options).Length);
        pending.Stage(route, record with
        {
            Value = JsonSerializer.SerializeToElement(new { payload = new string('x', 1_048_577 - overhead) }),
        });
        Assert.Throws<InvalidOperationException>(() => pending.ReadBatches());
    }

    private static RecordSnapshot Point(int offset)
    {
        var at = new DateTimeOffset(2026, 9, 14, 8, 0, 0, TimeSpan.Zero).AddMilliseconds(offset);
        return new RecordSnapshot(Guid.CreateVersion7(at), at, null, null,
            JsonSerializer.SerializeToElement(new { offset }));
    }
}
