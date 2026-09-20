using System.Text.Json;
using Heartbeat.Dev;

namespace Heartbeat.Dev.Tests;

public sealed class DesktopReplayEvidenceTests
{
    [Fact]
    public void QueueDrainCannotReplaceOrRegressTheRecordAlreadyObserved()
    {
        var started = new DateTimeOffset(2026, 9, 20, 8, 0, 0, TimeSpan.Zero);
        var before = new DesktopReplayEvidence(Guid.NewGuid(), Guid.NewGuid(), started, started.AddSeconds(2));
        before.RequireContinuationOf(before);
        (before with { EndedAt = started.AddSeconds(3) }).RequireContinuationOf(before);

        DesktopReplayEvidence[] wrong =
        [
            before with { TrackId = Guid.NewGuid() },
            before with { RecordId = Guid.NewGuid() },
            before with { StartedAt = started.AddSeconds(1) },
            before with { EndedAt = started.AddSeconds(1) },
        ];
        foreach (var evidence in wrong)
            Assert.Throws<InvalidOperationException>(() => evidence.RequireContinuationOf(before));
    }

    [Fact]
    public void PartialDatabaseEvidenceCannotProduceAReplayExpectation()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DesktopReplayEvidence>("{}"));
    }
}
