using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System.Text.Json;
using Heartbeat.Server.Services;
using Heartbeat.Server.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Tests.Services;

[Collection("postgres")]
public sealed class ServiceAccountMigrationTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    protected override string InitialMigration => "20260910130810_BrowserApplicationContexts";

    [Theory]
    [InlineData("segment")]
    [InlineData("event")]
    public async Task UnknownHistoryPreservesOriginalIdentityAndPayload_AndOldReplayDoesNotBindCurrentAccount(string kind)
    {
        await using var db = CreateDbContext();
        var batch = ServiceAccountTests.Batch();
        var stream = batch.Streams[0];
        stream.FactKind = kind;
        var fact = batch.Facts[0];
        fact.ObserverId = null; fact.Target = null;
        if (kind == "event") { fact.OccurredAt = fact.Start; fact.Start = fact.End = null; fact.IsFinal = null; }
        var rowId = Guid.CreateVersion7();
        var legacyRowId = Guid.CreateVersion7();
        var legacyStreamId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Subjects" ("OwnerId", "SubjectId", "Kind", "DisplayName")
            VALUES ('owner', {stream.Subject.SubjectId}, 'account', 'Not a service identity');
            INSERT INTO "Streams" ("OwnerId", "StreamId", "SubjectId", "CollectorInstanceId", "OutputId", "Source", "FactKind", "Dimensions", "Origin")
            VALUES ('owner', {stream.StreamId}, {stream.Subject.SubjectId}, {stream.CollectorInstanceId}, {stream.OutputId}, 'vrchat.account', {kind}, {"{}"}::jsonb, 'native'),
              ('owner', {legacyStreamId}, {stream.Subject.SubjectId}, NULL, 'legacy-import', 'vrchat.account', {kind}, {"{}"}::jsonb, 'legacy-import');
            """);
        var payload = fact.Payload!.Value.GetRawText();
        if (kind == "segment")
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Segments" ("Id", "OwnerId", "StreamId", "FactId", "Revision", "Source", "StartTime", "EndTime", "Payload")
                VALUES ({rowId}, 'owner', {stream.StreamId}, {fact.FactId}, 1, 'vrchat.account', {fact.Start}, {fact.End}, {payload}::jsonb),
                  ({legacyRowId}, 'owner', {legacyStreamId}, {legacyRowId}, 1, 'vrchat.account', {fact.Start}, {fact.End}, {payload}::jsonb);
                """);
        else
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Events" ("Id", "OwnerId", "StreamId", "FactId", "Revision", "Source", "Timestamp", "Payload")
                VALUES ({rowId}, 'owner', {stream.StreamId}, {fact.FactId}, 1, 'vrchat.account', {fact.OccurredAt}, {payload}::jsonb),
                  ({legacyRowId}, 'owner', {legacyStreamId}, {legacyRowId}, 1, 'vrchat.account', {fact.OccurredAt}, {payload}::jsonb);
                """);
        var oid = await db.Database.SqlQueryRaw<long>("""SELECT oid::bigint AS "Value" FROM pg_class WHERE relname IN ('Segments','Events') AND relkind='r' ORDER BY oid""").ToListAsync();
        await db.GetService<IMigrator>().MigrateAsync("20260911004949_CompleteHistoricalTargets");
        await db.GetService<IMigrator>().MigrateAsync("20260911004949_CompleteHistoricalTargets");
        Assert.Equal(oid, await db.Database.SqlQueryRaw<long>("""SELECT oid::bigint AS "Value" FROM pg_class WHERE relname IN ('Segments','Events') AND relkind='r' ORDER BY oid""").ToListAsync());
        await db.Database.MigrateAsync();
        var account = Assert.Single(await db.ServiceAccounts.ToListAsync());
        Assert.Null(account.ServiceAccountId);
        Assert.Equal(stream.Subject.SubjectId, account.LegacySubjectId);
        var store = new FactStore(db);
        await store.IngestAsync("owner", batch);
        // A renamed payload is the upgraded representation of the same pending snapshot.
        if (kind == "segment") fact.Payload = JsonDocument.Parse(payload.Replace("activityKey", "identityKey")).RootElement;
        await store.IngestAsync("owner", batch);
        var rows = kind == "segment" ? await store.ReadSegmentsAsync("owner", null, null, null, accountId: account.Id)
            : await store.ReadEventsAsync("owner", null, null, null, accountId: account.Id);
        Assert.Equal(2, rows.Count);
        Assert.Equal(stream.CollectorInstanceId, Assert.Single(rows, r => r.Id == rowId).ObserverId);
        Assert.Null(Assert.Single(rows, r => r.Id == legacyRowId).ObserverId);
        Assert.All(rows, r => { Assert.Null(r.DeviceId); Assert.NotNull(r.FoiId); Assert.Equal(1, r.Revision); Assert.True(JsonElement.DeepEquals(JsonDocument.Parse(payload).RootElement, r.Payload)); });
        var current = ServiceAccountTests.Batch();
        await store.IngestAsync("owner", current);
        Assert.Equal(2, await db.ServiceAccounts.CountAsync());
        Assert.Equal(db.Entry(account).Property<Guid?>("ObjectId").CurrentValue, (kind == "segment" ? await store.ReadSegmentsAsync("owner", null, null, null) : await store.ReadEventsAsync("owner", null, null, null)).Single(r => r.Id == rowId).FoiId);
    }
}
