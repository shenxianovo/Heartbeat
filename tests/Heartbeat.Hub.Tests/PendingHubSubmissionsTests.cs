using System.Text.Json;
using Heartbeat.Hub;

namespace Heartbeat.Hub.Tests;

public sealed class PendingHubSubmissionsTests(ITestOutputHelper output)
{
    private static readonly SubmissionRoute Route = new(
        new CollectorDeclaration("example.collector", "device-a", "Example"),
        new TrackDeclaration("example.point", 1, "point"));

    [Fact]
    public void BatchesFiveHundredAtATime()
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
    public async Task OldReceiptCannotClearANewerRangeSnapshot()
    {
        using var fixture = new QueueFixture();
        var queue = fixture.Open();
        var hub = new LocalHubSubmissionClient(queue);
        var pending = new PendingHubSubmissions();
        var start = new DateTimeOffset(2026, 9, 14, 8, 0, 0, TimeSpan.Zero);
        var record = new RecordSnapshot(Guid.CreateVersion7(start), start, start, null,
            JsonSerializer.SerializeToElement(new { value = 1 }));
        var route = Route with { Track = new TrackDeclaration("example.range", 1, "range") };
        pending.Stage(route, record);
        var sent = Assert.Single(pending.ReadBatches());
        pending.Stage(route, record with { EndedAt = start.AddSeconds(5) });

        await hub.SubmitAsync(sent.ToSubmission(), TestContext.Current.CancellationToken);
        pending.Confirm(sent);

        var retained = Assert.Single(Assert.Single(pending.ReadBatches()).Records);
        Assert.Equal(start.AddSeconds(5), retained.EndedAt);
        var renewed = Assert.Single(pending.ReadBatches());
        await hub.SubmitAsync(renewed.ToSubmission(), TestContext.Current.CancellationToken);
        pending.Confirm(renewed);
        Assert.Empty(pending.ReadBatches());
        Assert.Equal(start.AddSeconds(5), Assert.Single(queue.TakePending()).Record.EndedAt);
    }

    [Fact]
    public void SeparatesCollectorAndTrackDeclarations()
    {
        var pending = new PendingHubSubmissions();
        SubmissionRoute[] routes = [Route,
            Route with { Collector = Route.Collector with { Target = "device-b" } },
            Route with { Track = Route.Track with { Type = "example.other" } }];
        foreach (var route in routes) pending.Stage(route, Point(0));

        var batches = pending.ReadBatches();

        Assert.Equal(routes, batches.Select(batch => batch.Route));
        foreach (var batch in batches) Assert.Single(batch.Records);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenameRetainsTheLatestDeclarationUntilItsOwnReceipt(bool extend)
    {
        using var fixture = new QueueFixture();
        var queue = fixture.Open();
        var hub = new LocalHubSubmissionClient(queue);
        var pending = new PendingHubSubmissions();
        var route = Route with { Track = new("example.range", 1, "range") };
        var point = Point(0);
        var original = point with { EndedAt = point.StartedAt };
        pending.Stage(route, original);
        var sent = Assert.Single(pending.ReadBatches());
        var renamed = route with { Collector = route.Collector with { DisplayName = "Renamed" } };
        var latest = original with { EndedAt = original.EndedAt!.Value.AddSeconds(extend ? 5 : 0) };

        pending.Stage(renamed, latest);
        await hub.SubmitAsync(sent.ToSubmission(), TestContext.Current.CancellationToken);
        pending.Confirm(sent);

        var retained = Assert.Single(pending.ReadBatches());
        Assert.Equal(renamed, retained.Route);
        Assert.Equal(latest, Assert.Single(retained.Records));
        await hub.SubmitAsync(retained.ToSubmission(), TestContext.Current.CancellationToken);
        pending.Confirm(retained);
        Assert.Empty(pending.ReadBatches());
        var accepted = Assert.Single(queue.TakePending());
        Assert.Equal("Renamed", accepted.Route.Collector.DisplayName);
        Assert.Equal(original.Id, accepted.Record.Id);
        Assert.Equal(latest.EndedAt, accepted.Record.EndedAt);
    }

    [Theory]
    [InlineData("collector")]
    [InlineData("target")]
    [InlineData("type")]
    [InlineData("version")]
    [InlineData("time")]
    public void RejectsChangesToPendingIdentityOrTimeDefinition(string change)
    {
        var pending = new PendingHubSubmissions();
        var record = Point(0);
        pending.Stage(Route, record);
        var changed = change switch
        {
            "collector" => Route with { Collector = Route.Collector with { Key = "example.other" } },
            "target" => Route with { Collector = Route.Collector with { Target = "device-b" } },
            "type" => Route with { Track = Route.Track with { Type = "example.other" } },
            "version" => Route with { Track = Route.Track with { Version = 2 } },
            _ => Route with { Track = Route.Track with { TimeMode = "range" } },
        };

        Assert.Throws<InvalidOperationException>(() => pending.Stage(changed, record));
        Assert.Equal(Route, Assert.Single(pending.ReadBatches()).Route);
    }

    [Fact]
    public async Task RejectedCustodyRetainsSnapshotsUntilRetrySucceeds()
    {
        using var fixture = new QueueFixture();
        var queue = fixture.Open(capacity: 1);
        var hub = new LocalHubSubmissionClient(queue);
        await hub.SubmitAsync(new(Route.Collector, Route.Track, [Point(0)]), TestContext.Current.CancellationToken);
        var pending = new PendingHubSubmissions();
        var record = Point(1);
        pending.Stage(Route, record);
        var sent = Assert.Single(pending.ReadBatches());

        await Assert.ThrowsAsync<IOException>(() => hub.SubmitAsync(sent.ToSubmission(), TestContext.Current.CancellationToken));

        Assert.Equal(record, Assert.Single(Assert.Single(pending.ReadBatches()).Records));
        Assert.Single(queue.TakePending());
        var retryHub = new LocalHubSubmissionClient(fixture.Open(capacity: 2));
        await retryHub.SubmitAsync(sent.ToSubmission(), TestContext.Current.CancellationToken);
        pending.Confirm(sent);
        Assert.Empty(pending.ReadBatches());
        Assert.Contains(queue.TakePending(), item => item.Record.Id == record.Id);
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

        var batches = pending.ReadBatches();
        Assert.Equal([1, 1], batches.Select(batch => batch.Records.Count));
        using var fixture = new QueueFixture();
        var queue = fixture.Open();
        foreach (var batch in batches) queue.Accept(batch.ToSubmission());
        Assert.Equal(2, queue.TakePending().Count);
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
