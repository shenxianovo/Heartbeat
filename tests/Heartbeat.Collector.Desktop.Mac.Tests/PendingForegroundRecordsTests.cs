using Heartbeat.Collector.Desktop.Mac;

namespace Heartbeat.Collector.Desktop.Mac.Tests;

public sealed class PendingForegroundRecordsTests
{
    [Fact]
    public void PendingRecordsCoalesceExtensionsAndRetainApplicationChanges()
    {
        var pending = new PendingForegroundRecords();
        var first = CreateRecord("first");
        var extended = first with { EndedAt = first.EndedAt.AddSeconds(5) };
        var next = CreateRecord("next");

        pending.Stage(first);
        pending.Stage(extended);
        pending.Stage(next);

        var batch = pending.ReadBatch();
        Assert.Equal(2, batch.Count);
        Assert.Contains(extended, batch);
        Assert.Contains(next, batch);
    }

    [Fact]
    public void AcknowledgingAnOlderSnapshotKeepsNewerProgress()
    {
        var pending = new PendingForegroundRecords();
        var first = CreateRecord("first");
        pending.Stage(first);
        var sent = Assert.Single(pending.ReadBatch());
        var extended = first with { EndedAt = first.EndedAt.AddSeconds(5) };
        pending.Stage(extended);

        pending.Confirm(sent);

        Assert.Equal(extended, Assert.Single(pending.ReadBatch()));
        pending.Confirm(extended);
        Assert.Empty(pending.ReadBatch());
    }

    private static ForegroundRecord CreateRecord(string application)
    {
        var start = new DateTimeOffset(2026, 9, 12, 16, 0, 0, TimeSpan.Zero);
        return new ForegroundRecord(Guid.CreateVersion7(), start, start,
            new ForegroundApplication("macos", "bundle_id", application));
    }
}
