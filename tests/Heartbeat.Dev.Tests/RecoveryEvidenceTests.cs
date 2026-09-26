using System.Text.Json;
using Heartbeat.Dev;
using Heartbeat.Hub;

namespace Heartbeat.Dev.Tests;

public sealed class RecoveryEvidenceTests
{
    [Fact]
    public void RecoveryRejectsLossDuplicationSubstitutionAndCorruptedCustody()
    {
        var start = DateTimeOffset.Parse("2026-09-26T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var before = new RecoveryRecord(Guid.NewGuid(), "collector", "device",
            new TrackDeclaration("application", 1, "range", "explicit"), Guid.NewGuid(),
            new RecordSnapshot(Guid.NewGuid(), start, start.AddSeconds(5), null, JsonSerializer.SerializeToElement(new { count = 1 })));
        RecoveryRecord[][] corrupt =
        [
            [], [before, before],
            [before with { Record = before.Record with { Id = Guid.NewGuid() } }],
            [before with { TrackId = Guid.NewGuid() }],
            [before with { CollectorKey = "another-collector" }],
            [before with { OwnerId = Guid.NewGuid() }],
            [before with { Target = "another-device" }],
            [before with { Track = before.Track with { Type = "another-track" } }],
            [before with { Record = before.Record with { EndedAt = start } }],
            [before with { Record = before.Record with { Value = JsonSerializer.SerializeToElement(new { count = 2 }) } }],
        ];
        foreach (var after in corrupt)
            Assert.Throws<InvalidOperationException>(() => RecoveryEvidence.RequireSameRecords([before], after));
    }

    [Fact]
    public void PostgresMicrosecondPrecisionDoesNotHideARealTimestampChange()
    {
        var time = DateTimeOffset.Parse("2026-09-26T08:00:00.1234567Z", System.Globalization.CultureInfo.InvariantCulture);
        var before = new RecoveryRecord(Guid.NewGuid(), "collector", "device", new TrackDeclaration("input", 1, "point", null),
            Guid.NewGuid(), new RecordSnapshot(Guid.NewGuid(), null, null, time, JsonSerializer.SerializeToElement(1)));
        var rounded = before with { Record = before.Record with { ObservedAt = time.AddTicks(-7) } };
        RecoveryEvidence.RequireSameRecords([before], [rounded]);
        Assert.Throws<InvalidOperationException>(() => RecoveryEvidence.RequireSameRecords([before],
            [rounded with { Record = rounded.Record with { ObservedAt = time.AddTicks(-10) } }]));
    }
}
