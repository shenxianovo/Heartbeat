using System.Text.Json;
using Heartbeat.Core.DTOs.Facts;
using Heartbeat.Server.Services;
using Heartbeat.Server.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Tests.Services;

[Collection("postgres")]
public sealed class ObservationTargetMigrationTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    protected override string InitialMigration => "20260908141403_NativeFactCustody";

    [Theory]
    [InlineData("segment")]
    [InlineData("event")]
    public async Task FamilyBaseline_BackfillsOnlyKnownSystemAttribution_AndOldReplayKeepsTheSameRow(string kind)
    {
        var batch = FactStoreTests.SegmentBatch();
        var stream = batch.Streams[0];
        stream.Source = "system";
        stream.FactKind = kind;
        var fact = batch.Facts[0];
        if (kind == "event")
        {
            fact.OccurredAt = fact.Start;
            fact.Start = fact.End = null;
            fact.IsFinal = null;
            fact.Payload = JsonSerializer.SerializeToElement(new { eventType = "mouseButton", codeSet = "heartbeat-key-position-v1", code = 1 });
        }
        else fact.Payload = JsonSerializer.SerializeToElement(new { activityKey = "old", title = "untouched", unknown = new[] { 4, 5 } });
        var rowId = Guid.CreateVersion7();
        var unknownId = Guid.CreateVersion7();
        var legacyStream = Guid.NewGuid();
        await using var db = CreateDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Devices" ("Id", "OwnerId", "HardwareId", "DeviceName") VALUES (701, 'owner', 'hardware', 'PC');
            INSERT INTO "Subjects" ("OwnerId", "SubjectId", "Kind", "DeviceId") VALUES ('owner', {stream.Subject.SubjectId}, 'machine', 701);
            INSERT INTO "Streams" ("OwnerId", "StreamId", "SubjectId", "CollectorInstanceId", "OutputId", "Source", "FactKind", "Dimensions", "Origin")
            VALUES ('owner', {stream.StreamId}, {stream.Subject.SubjectId}, {stream.CollectorInstanceId}, {stream.OutputId}, 'system', {kind}, {"{}"}::jsonb, 'native'),
                   ('owner', {legacyStream}, {stream.Subject.SubjectId}, NULL, 'legacy-import', 'system', {kind}, {"{}"}::jsonb, 'legacy-import');
            """);
        if (kind == "segment")
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Segments" ("Id", "OwnerId", "StreamId", "FactId", "Revision", "Source", "StartTime", "EndTime", "Payload")
                VALUES ({rowId}, 'owner', {stream.StreamId}, {fact.FactId}, 1, 'system', {fact.Start}, {fact.End}, {fact.Payload.Value.GetRawText()}::jsonb),
                       ({unknownId}, 'owner', {legacyStream}, {unknownId}, 1, 'system', {fact.Start}, {fact.End}, {fact.Payload.Value.GetRawText()}::jsonb);
                """);
        else
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Events" ("Id", "OwnerId", "StreamId", "FactId", "Revision", "Source", "Timestamp", "Payload")
                VALUES ({rowId}, 'owner', {stream.StreamId}, {fact.FactId}, 1, 'system', {fact.OccurredAt}, {fact.Payload.Value.GetRawText()}::jsonb),
                       ({unknownId}, 'owner', {legacyStream}, {unknownId}, 1, 'system', {fact.OccurredAt}, {fact.Payload.Value.GetRawText()}::jsonb);
                """);
        var tableIds = await db.Database.SqlQueryRaw<long>("""SELECT oid::bigint AS "Value" FROM pg_class WHERE relname IN ('Segments','Events') AND relkind='r' ORDER BY oid""").ToListAsync();
        await db.Database.MigrateAsync();
        await db.Database.MigrateAsync();
        Assert.Equal(tableIds, await db.Database.SqlQueryRaw<long>("""SELECT oid::bigint AS "Value" FROM pg_class WHERE relname IN ('Segments','Events') AND relkind='r' ORDER BY oid""").ToListAsync());
        var store = new FactStore(db);
        var rows = kind == "segment" ? await store.ReadSegmentsAsync("owner", 701, null, null) : await store.ReadEventsAsync("owner", 701, null, null);
        var saved = Assert.Single(rows, r => r.Id == rowId);
        Assert.Equal(stream.CollectorInstanceId, saved.ObserverId);
        Assert.Equal("device", saved.TargetKind);
        Assert.Equal(701, saved.TargetId);
        Assert.Null(Assert.Single(rows, r => r.Id == unknownId).ObserverId);
        Assert.True(JsonElement.DeepEquals(fact.Payload.Value, saved.Payload));
        await store.IngestAsync("owner", batch); // equivalent pre-attribution snapshot
        fact.ObserverId = stream.CollectorInstanceId;
        fact.Target = new FactTarget("device", "hardware");
        await store.IngestAsync("owner", batch);
        rows = kind == "segment" ? await store.ReadSegmentsAsync("owner", 701, null, null) : await store.ReadEventsAsync("owner", 701, null, null);
        Assert.Equal(2, rows.Count);
        Assert.Equal(saved.Id, Assert.Single(rows, r => r.FactId == fact.FactId).Id);
    }
}
