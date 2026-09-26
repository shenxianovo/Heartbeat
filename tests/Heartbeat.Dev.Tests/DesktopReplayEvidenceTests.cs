using System.Text.Json;
using Heartbeat.Dev;
using Heartbeat.Hub;

namespace Heartbeat.Dev.Tests;

public sealed class DesktopReplayEvidenceTests
{
    [Fact]
    public void CollectionDiagnosticsSeparateRoutingTimeAndDurationWithoutExportingNativeValues()
    {
        var start = DateTimeOffset.Parse("2026-09-26T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var owner = Guid.NewGuid();
        var expected = new DesktopObservationExpectation(owner, "private-device", "controlled-app", start);
        var correct = new ScenarioRecord(owner, "heartbeat.collector.desktop.macos", "private-device",
            new TrackDeclaration("desktop.application.foreground", 1, "range", "explicit"), Guid.NewGuid(),
            new RecordSnapshot(Guid.NewGuid(), start, start.AddSeconds(3), null, JsonSerializer.SerializeToElement(new
            {
                device_id = "private-device", application = new { platform = "macos", id_kind = "bundle_id", id = "controlled-app" },
                privateContext = "must-not-enter-diagnostics",
            })));
        ScenarioRecord[] records =
        [
            correct,
            correct with { OwnerId = Guid.NewGuid() },
            correct with { Target = "other-device" },
            correct with { Record = correct.Record with { Value = JsonSerializer.SerializeToElement("invalid-payload") } },
            correct with { Record = correct.Record with { StartedAt = start.AddSeconds(-1) } },
            correct with { Record = correct.Record with { EndedAt = start.AddSeconds(1) } },
        ];
        var result = expected.Inspect(records, start.AddSeconds(5));
        Assert.Equal(6, result.Total);
        Assert.Equal(5, result.Owner);
        Assert.Equal(4, result.Target);
        Assert.Equal(3, result.Payload);
        Assert.Equal(2, result.TimeWindow);
        Assert.Equal(1, result.Duration);
        Assert.Equal(correct.Record.Id, result.Witness!.RecordId);
        Assert.Null(expected.Inspect(records[1..], start.AddSeconds(5)).Witness);
        var report = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("private-device", report);
        Assert.DoesNotContain("must-not-enter-diagnostics", report);
        Assert.DoesNotContain("invalid-payload", report);
    }

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

}
