using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System.Text.Json;
using Heartbeat.Core.DTOs.Facts;
using Heartbeat.Server.Services;
using Heartbeat.Server.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Tests.Services;

[Collection("postgres")]
public sealed class BrowserApplicationContextMigrationTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    protected override string InitialMigration => "20260910121906_ObservationTargets";

    [Theory]
    [InlineData("segment")]
    [InlineData("event")]
    public async Task BrowserHistory_MapsSavedEvidenceOnly_AndOldAndNewReplayKeepFamilyRows(string kind)
    {
        await using var db = CreateDbContext();
        var batch = BrowserApplicationContextTests.Batch("mac:com.google.chrome");
        var stream = batch.Streams[0];
        stream.FactKind = kind;
        stream.Dimensions.Remove("appIdentityKey"); // Historical AppIdentityId can be the only saved App evidence.
        var fact = batch.Facts[0];
        if (kind == "event")
        {
            fact.OccurredAt = fact.Start; fact.Start = fact.End = null; fact.IsFinal = null;
        }
        var rowId = Guid.CreateVersion7();
        var unknownId = Guid.CreateVersion7();
        var legacyStream = Guid.NewGuid();
        var payload = """{"activityKey":"https://example.com","unknown":{"original":[7,9]},"attributes":{"windowId":7}}""";
        fact.Payload = JsonDocument.Parse(payload).RootElement;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Devices" ("Id", "OwnerId", "HardwareId", "DeviceName") VALUES (701, 'owner', 'hardware', 'PC');
            INSERT INTO "Apps" ("Id", "Key", "DisplayName", "IsProvisional") VALUES (801, 'chrome', 'Chrome', false);
            INSERT INTO "AppIdentities" ("Id", "Key", "AppId") VALUES (901, 'mac:com.google.chrome', 801);
            INSERT INTO "Subjects" ("OwnerId", "SubjectId", "Kind", "DeviceId") VALUES ('owner', {stream.Subject.SubjectId}, 'machine', 701);
            INSERT INTO "Streams" ("OwnerId", "StreamId", "SubjectId", "CollectorInstanceId", "OutputId", "Source", "FactKind", "Dimensions", "Origin")
            VALUES ('owner', {stream.StreamId}, {stream.Subject.SubjectId}, {stream.CollectorInstanceId}, {stream.OutputId}, 'browser', {kind}, {JsonSerializer.Serialize(stream.Dimensions)}::jsonb, 'native'),
                   ('owner', {legacyStream}, {stream.Subject.SubjectId}, NULL, 'legacy-import', 'browser', {kind}, {"{}"}::jsonb, 'legacy-import');
            """);
        if (kind == "segment")
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Segments" ("Id", "OwnerId", "StreamId", "FactId", "Revision", "Source", "AppIdentityId", "StartTime", "EndTime", "Payload")
                VALUES ({rowId}, 'owner', {stream.StreamId}, {fact.FactId}, 1, 'browser', 901, {fact.Start}, {fact.End}, {payload}::jsonb),
                       ({unknownId}, 'owner', {legacyStream}, {unknownId}, 1, 'browser', NULL, {fact.Start}, {fact.End}, {payload}::jsonb);
                """);
        else
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Events" ("Id", "OwnerId", "StreamId", "FactId", "Revision", "Source", "AppIdentityId", "Timestamp", "Payload")
                VALUES ({rowId}, 'owner', {stream.StreamId}, {fact.FactId}, 1, 'browser', 901, {fact.OccurredAt}, {payload}::jsonb),
                       ({unknownId}, 'owner', {legacyStream}, {unknownId}, 1, 'browser', NULL, {fact.OccurredAt}, {payload}::jsonb);
                """);
        var tableIds = await db.Database.SqlQueryRaw<long>("""SELECT oid::bigint AS "Value" FROM pg_class WHERE relname IN ('Segments','Events') AND relkind='r' ORDER BY oid""").ToListAsync();
        await db.GetService<IMigrator>().MigrateAsync("20260911004949_CompleteHistoricalTargets");
        await db.GetService<IMigrator>().MigrateAsync("20260911004949_CompleteHistoricalTargets");
        Assert.Equal(tableIds, await db.Database.SqlQueryRaw<long>("""SELECT oid::bigint AS "Value" FROM pg_class WHERE relname IN ('Segments','Events') AND relkind='r' ORDER BY oid""").ToListAsync());
        await db.Database.MigrateAsync();
        var store = new FactStore(db);
        async Task<List<FactResponse>> Read() => kind == "segment"
            ? await store.ReadSegmentsAsync("owner", 701, null, null) : await store.ReadEventsAsync("owner", 701, null, null);
        var before = await Read();
        var mapped = Assert.Single(before, row => row.Id == rowId);
        Assert.Equal("app", (await db.Objects.SingleAsync(o => o.Id == mapped.FoiId)).Kind);
        Assert.Equal(801, mapped.AppId);
        Assert.Equal(fact.ObserverId, mapped.ObserverId);
        var unknown = Assert.Single(before, row => row.Id == unknownId);
        Assert.Equal("machine", (await db.Objects.SingleAsync(o => o.Id == unknown.FoiId)).Kind);
        Assert.Null(unknown.ObserverId);
        Assert.Null(unknown.AppId);
        await store.IngestAsync("owner", batch);
        fact.ObserverId = null; fact.Target = null;
        await store.IngestAsync("owner", batch);
        var after = await Read();
        Assert.Equal(before.Select(r => r.Id).Order(), after.Select(r => r.Id).Order());
        Assert.All(after, row => { Assert.Equal(1, row.Revision); Assert.True(JsonElement.DeepEquals(fact.Payload.Value, row.Payload)); });
        Assert.Equal(fact.Start, mapped.Start); Assert.Equal(fact.End, mapped.End); Assert.Equal(fact.OccurredAt, mapped.OccurredAt);
    }
}
