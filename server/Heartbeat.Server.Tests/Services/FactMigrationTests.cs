using System.Text.Json;
using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Server.Services;
using Heartbeat.Server.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Heartbeat.Server.Tests.Services;

[Collection("postgres")]
public sealed class FactMigrationTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    private const string Previous = "20260829100458_AskingWindowIdentity";
    protected override string InitialMigration => Previous;

    [Fact]
    public async Task ExistingRows_KeepTheirIdsPayloadAndSubjects_AndNativeReplayTakesOverOnce()
    {
        var batch = FactStoreTests.SegmentBatch();
        var legacy = FactStoreTests.LegacySegment(batch);
        var oldId = legacy.Id;
        var otherId = Guid.CreateVersion7();
        var inputId = Guid.CreateVersion7();
        var subjectId = Guid.NewGuid();
        var accountId = Guid.CreateVersion7();
        const string rawOther = """{"url":"https://old.example/?a=1","unknown":{"keep":[1,2,3]}}""";
        var raw = legacy.Attributes!.Value.GetRawText();
        await using (var db = CreateDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Devices" ("Id", "OwnerId", "HardwareId", "DeviceName") VALUES (701, 'owner', 'hardware', 'Old PC'), (702, 'owner', {"subject:account:" + subjectId}, 'Account');
                INSERT INTO "ActivitySegments" ("Id", "DeviceId", "Source", "IdentityKey", "Title", "StartTime", "EndTime", "Attributes")
                VALUES ({oldId}, 701, 'browser', {legacy.IdentityKey}, {legacy.Title}, {legacy.StartTime}, {legacy.EndTime}, {raw}::jsonb),
                       ({otherId}, 701, 'browser', 'old', 'Old', {legacy.StartTime}, {legacy.EndTime}, {rawOther}::jsonb),
                       ({accountId}, 702, 'vrchat', 'account', 'Account', {legacy.StartTime}, {legacy.EndTime}, NULL);
                INSERT INTO "InputEvents" ("Id", "DeviceId", "EventType", "CodeSet", "Code", "Timestamp")
                VALUES ({inputId}, 701, 1, 'windows-vk-v1', 65, {legacy.StartTime});
                """);
            await db.Database.MigrateAsync();
        }
        await using (var db = CreateDbContext())
        {
            Assert.Equal(3, await db.Segments.CountAsync());
            Assert.Single(await db.Events.ToListAsync());
            var fact = await db.Segments.SingleAsync(s => s.Id == oldId);
            Assert.Equal(oldId, fact.FactId);
            Assert.Equal(1, fact.Revision);
            Assert.Equal("https://example.com", fact.Payload.RootElement.GetProperty("activityKey").GetString());
            Assert.False(fact.Payload.RootElement.TryGetProperty("identityKey", out _));
            Assert.Equal("https://example.com/?original=1", fact.Payload.RootElement.GetProperty("attributes").GetProperty("url").GetString());
            var other = await db.Segments.SingleAsync(s => s.Id == otherId);
            Assert.True(JsonElement.DeepEquals(JsonDocument.Parse(rawOther).RootElement, other.Payload.RootElement.GetProperty("attributes")));
            var account = await db.Segments.Include(s => s.Stream).ThenInclude(s => s.Subject).SingleAsync(s => s.Id == accountId);
            Assert.Null(account.Stream.Subject.DeviceId);
            Assert.Equal(subjectId, account.Stream.SubjectId);
            Assert.Equal("account", account.Stream.Subject.Kind);
            var input = await db.Events.SingleAsync();
            Assert.Equal(inputId, input.Id);
            Assert.Equal(inputId, input.FactId);
            Assert.Equal("windows-vk-v1", input.Payload.RootElement.GetProperty("codeSet").GetString());
            Assert.Equal(65, input.Payload.RootElement.GetProperty("code").GetInt32());
            await new FactStore(db).ImportSegmentsAsync(702, [new ActivitySegmentItem
            {
                Id = accountId, Source = "vrchat", IdentityKey = "account", Title = "Account", StartTime = legacy.StartTime, EndTime = legacy.EndTime
            }]);
            await new FactStore(db).ImportSegmentsAsync(701, [legacy]);
        }
        await using (var db = CreateDbContext())
        {
            // Initial native Revision=1 must take over the synthetic legacy Revision=1.
            batch.Facts[0].End = batch.Facts[0].Start!.Value.AddMinutes(1);
            await new FactStore(db).IngestAsync("owner", batch);
            await new FactStore(db).ImportSegmentsAsync(701, [legacy]);
            Assert.Equal(3, await db.Segments.CountAsync());
            var fact = await db.Segments.SingleAsync(f => f.Id == oldId);
            Assert.Equal(batch.Facts[0].FactId, fact.FactId);
            Assert.Equal(batch.Streams[0].StreamId, fact.StreamId);
            Assert.Equal(batch.Facts[0].End, fact.EndTime);
            await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync(Previous));
            Assert.Equal(3, await db.Segments.CountAsync());
        }
    }

    [Fact]
    public async Task ConflictingActivityKey_AbortsUpgradeAndLeavesOldRowsIntact()
    {
        await using var db = CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "Devices" ("Id", "OwnerId", "HardwareId", "DeviceName") VALUES (701, 'owner', 'hardware', 'PC');
            INSERT INTO "ActivitySegments" ("Id", "DeviceId", "Source", "IdentityKey", "Title", "StartTime", "EndTime", "Attributes")
            VALUES ('01990000-0000-7000-8000-000000000001', 701, 'browser', 'a', 'Title', '2026-09-01Z', '2026-09-02Z',
                 '{{"identityKey":"a","activityKey":"b","title":"Title","attributes":{{}}}}'::jsonb);
            """);
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync());
        Assert.Contains("conflicts", error.MessageText);
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM \"ActivitySegments\"").SingleAsync());
        Assert.Equal(Previous, (await db.Database.GetAppliedMigrationsAsync()).Last());
    }

    [Theory]
    [InlineData("7", null, "{\"identityKey\":7,\"title\":null,\"attributes\":{}}")]
    [InlineData("7", "42", "{\"identityKey\":\"7\",\"title\":42,\"attributes\":{}}")]
    [InlineData("7", null, "{\"identityKey\":\"7\",\"attributes\":{},\"unknown\":[1,2]}")]
    [InlineData("7", "Title", "[1,2,3]")]
    public async Task MigrationAndCacheImport_InterpretHistoricalJsonIdentically(string key, string? title, string attributes)
    {
        await using var db = CreateDbContext();
        var oldId = Guid.CreateVersion7();
        var at = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Devices" ("Id", "OwnerId", "HardwareId", "DeviceName") VALUES (701, 'owner', 'hardware', 'PC');
            INSERT INTO "ActivitySegments" ("Id", "DeviceId", "Source", "IdentityKey", "Title", "StartTime", "EndTime", "Attributes")
            VALUES ({oldId}, 701, 'browser', {key}, {title}, {at}, {at.AddMinutes(1)}, {attributes}::jsonb);
            """);
        await db.Database.MigrateAsync();
        var newId = Guid.CreateVersion7();
        await new FactStore(db).ImportSegmentsAsync(701, [new ActivitySegmentItem
        {
            Id = newId, Source = "browser", IdentityKey = key, Title = title,
            StartTime = at, EndTime = at.AddMinutes(1), Attributes = JsonDocument.Parse(attributes).RootElement
        }]);
        var migrated = await db.Segments.SingleAsync(s => s.Id == oldId);
        var imported = await db.Segments.SingleAsync(s => s.Id == newId);
        Assert.True(JsonElement.DeepEquals(migrated.Payload.RootElement, imported.Payload.RootElement));
    }
}
