using System.Text.Json;
using Heartbeat.Core.DTOs.Facts;
using Heartbeat.Server.Services;
using Heartbeat.Server.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Tests.Services;

[Collection("postgres")]
public sealed class ObservationStorageTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task SameAppOnTwoDevices_RelationsBindTheExactFact_AndFollowCorrections()
    {
        await using var db = CreateDbContext();
        var mac = BrowserApplicationContextTests.Batch("mac:com.test.chrome", "mac-one");
        var windows = BrowserApplicationContextTests.Batch("mac:com.test.chrome", "windows-one");
        var store = new FactStore(db);
        await store.IngestAsync("owner", mac);
        await store.IngestAsync("owner", windows);
        var devices = await db.Devices.ToDictionaryAsync(d => d.HardwareId, d => d.Id);
        var macFact = Assert.Single(await store.ReadSegmentsAsync("owner", devices["mac-one"], null, null));
        var windowsFact = Assert.Single(await store.ReadSegmentsAsync("owner", devices["windows-one"], null, null));
        Assert.NotEqual(macFact.Id, windowsFact.Id);
        var foiCount = await db.Database.SqlQueryRaw<int>("""SELECT count(DISTINCT "FoiId")::int AS "Value" FROM "Facts" """).SingleAsync();
        Assert.Equal(1, foiCount);
        Assert.Equal(2, await db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM "Relations" WHERE "Kind" = 'observed-on'""").SingleAsync());
        mac.Facts[0].Revision++;
        mac.Facts[0].End = mac.Facts[0].Start!.Value.AddSeconds(1);
        await store.IngestAsync("owner", mac);
        await store.IngestAsync("owner", mac);
        var end = await db.Database.SqlQuery<DateTimeOffset>($"""
            SELECT "ValidTo" AS "Value" FROM "Relations" WHERE "Evidence"->>'factId' = {macFact.Id.ToString("D")}
            """).SingleAsync();
        Assert.Equal(mac.Facts[0].End, end);
        Assert.Equal(2, await db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM "Relations" WHERE "Kind" = 'observed-on'""").SingleAsync());
        Assert.Empty(await store.ReadSegmentsAsync("other", devices["mac-one"], null, null));
    }

    [Fact]
    public async Task RelationsRejectCrossOwnerMembersAndEvidence_AndIncompleteRoles()
    {
        await using var db = CreateDbContext();
        var store = new FactStore(db);
        await store.IngestAsync("owner", BrowserApplicationContextTests.Batch("mac:com.test.chrome", "mac"));
        await store.IngestAsync("other", BrowserApplicationContextTests.Batch("mac:com.test.chrome", "windows"));
        var relation = await db.Relations.SingleAsync(r => r.OwnerId == "owner");
        var otherFact = await db.Facts.SingleAsync(f => f.OwnerId == "other");
        var otherDevice = await db.Objects.SingleAsync(o => o.OwnerId == "other" && o.Kind == "machine");
        var crossOwner = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "RelationMembers" SET "ObjectId" = {otherDevice.Id} WHERE "RelationId" = {relation.Id} AND "Role" = 'device'
            """));
        Assert.Equal("23503", crossOwner.SqlState);
        var evidence = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "Relations" SET "Evidence" = jsonb_build_object('factId', {otherFact.Id}) WHERE "Id" = {relation.Id}
            """));
        Assert.Equal("23503", evidence.SqlState);
        var missing = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM "RelationMembers" WHERE "RelationId" = {relation.Id} AND "Role" = 'device'
            """));
        Assert.Equal("23514", missing.SqlState);
        Assert.Equal(2, await db.RelationMembers.CountAsync(m => m.RelationId == relation.Id));
    }

    [Fact]
    public async Task RelationsCannotLoseTheirEvidence_OrMoveMembersBetweenRelations()
    {
        await using var db = CreateDbContext();
        var store = new FactStore(db);
        await store.IngestAsync("owner", BrowserApplicationContextTests.Batch("mac:com.test.chrome", "mac"));
        await store.IngestAsync("owner", BrowserApplicationContextTests.Batch("mac:com.test.chrome", "windows"));
        var relations = await db.Relations.OrderBy(r => r.Id).ToArrayAsync();
        var emptyEvidence = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "Relations" SET "Evidence" = jsonb_build_object() WHERE "Id" = {relations[0].Id}
            """));
        Assert.Equal("23514", emptyEvidence.SqlState);
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM "RelationMembers" WHERE "RelationId" = {relations[1].Id} AND "Role" = 'app'
            """);
        var moved = await Assert.ThrowsAsync<Npgsql.PostgresException>(async () =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "RelationMembers" SET "RelationId" = {relations[1].Id} WHERE "RelationId" = {relations[0].Id} AND "Role" = 'app'
                """);
            await transaction.CommitAsync();
        });
        Assert.Equal("23514", moved.SqlState);
    }

    [Fact]
    public async Task KnownAppEvidenceRemainsQueryable_WhenTheObservedObjectIsUnknown()
    {
        await using var db = CreateDbContext();
        var store = new FactStore(db);
        await store.IngestAsync("owner", BrowserApplicationContextTests.Batch("mac:com.test.chrome", "mac"));
        var appId = await db.Apps.Select(a => a.Id).SingleAsync();
        await db.Database.ExecuteSqlRawAsync("""DELETE FROM "Relations"; UPDATE "Facts" SET "FoiId" = NULL""");
        var fact = Assert.Single(await store.ReadSegmentsAsync("owner", null, null, null, appId: appId));
        Assert.Equal(appId, fact.AppId);
        Assert.Null(fact.FoiId);
        Assert.Null(fact.DeviceId);
        Assert.Empty(await db.Relations.ToListAsync());
    }

    [Fact]
    public async Task FamiliesShareOneStore_AndReplayPreservesIndependentFactsAndContent()
    {
        await using var db = CreateDbContext();
        var segment = FactStoreTests.SegmentBatch();
        var item = segment.Facts[0];
        item.Payload = JsonSerializer.SerializeToElement(new { activityKey = "page", extra = new[] { 2, 7 } });
        var events = FactStoreTests.SegmentBatch();
        events.Streams[0].FactKind = "event";
        var point = events.Facts[0];
        point.FactId = item.FactId;
        point.OccurredAt = point.Start;
        point.Start = point.End = null;
        point.IsFinal = null;
        point.Payload = JsonSerializer.SerializeToElement(new { value = 17, extra = "keep" });
        var store = new FactStore(db);
        await store.IngestAsync("owner", segment);
        await store.IngestAsync("owner", events);
        await store.IngestAsync("owner", segment);
        await store.IngestAsync("owner", events);
        var savedSegment = Assert.Single(await store.ReadSegmentsAsync("owner", null, null, null));
        var savedEvent = Assert.Single(await store.ReadEventsAsync("owner", null, null, null));
        Assert.NotEqual(savedSegment.Id, savedEvent.Id);
        Assert.True(JsonElement.DeepEquals(item.Payload.Value, savedSegment.Payload));
        Assert.True(JsonElement.DeepEquals(point.Payload.Value, savedEvent.Payload));
        Assert.Equal(2, await db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM "Facts" """).SingleAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM pg_class WHERE relname IN ('Segments','Events') AND relkind = 'r'""").SingleAsync());
    }
}
