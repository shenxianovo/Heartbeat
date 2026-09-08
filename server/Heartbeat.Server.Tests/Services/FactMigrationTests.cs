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

    [Fact]
    public async Task ExistingRows_MigrateLosslessly_ReplayClaimsSameFact_AndLegacyArchiveSurvives()
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
            await db.GetService<IMigrator>().MigrateAsync(Previous);
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
        Guid factKey;
        string archive;
        await using (var db = CreateDbContext())
        {
            Assert.Equal(4, await db.Facts.CountAsync());
            var row = await db.ActivitySegments.SingleAsync(s => s.Id == oldId);
            factKey = row.FactKey!.Value;
            var fact = await db.Facts.SingleAsync(f => f.Id == factKey);
            archive = fact.LegacyRecord!;
            Assert.Equal("legacy-import", fact.Origin);
            Assert.Equal(0, fact.Revision);
            Assert.Null(fact.IsFinal);
            Assert.True(JsonElement.DeepEquals(JsonDocument.Parse(raw).RootElement, JsonDocument.Parse(archive).RootElement.GetProperty("Attributes")));
            Assert.Equal("https://example.com/?original=1", JsonDocument.Parse(row.Payload!).RootElement.GetProperty("attributes").GetProperty("url").GetString());
            var other = await db.ActivitySegments.SingleAsync(s => s.Id == otherId);
            Assert.True(JsonElement.DeepEquals(JsonDocument.Parse(rawOther).RootElement, JsonDocument.Parse(other.Payload!).RootElement.GetProperty("attributes")));
            var account = await db.ActivitySegments.Include(s => s.Fact).ThenInclude(f => f!.Stream).ThenInclude(s => s.Subject).SingleAsync(s => s.Id == accountId);
            Assert.Null(account.DeviceId);
            Assert.Equal(subjectId, account.Fact!.Stream.SubjectId);
            Assert.Equal("account", account.Fact.Stream.Subject.Kind);
            Assert.Equal((short)65, (await db.InputEvents.SingleAsync()).Code);
            await new FactStore(db).ImportSegmentsAsync(702, [new ActivitySegmentItem
            {
                Id = accountId, Source = "vrchat", IdentityKey = "account", Title = "Account", StartTime = legacy.StartTime, EndTime = legacy.EndTime
            }]);
            Assert.Null((await db.ActivitySegments.SingleAsync(s => s.Id == accountId)).DeviceId);
            await new FactStore(db).ImportSegmentsAsync(701, [legacy]);
            Assert.Equal(archive, (await db.Facts.SingleAsync(f => f.Id == factKey)).LegacyRecord);
        }
        await using (var db = CreateDbContext())
        {
            await new FactStore(db).IngestAsync("owner", batch);
            Assert.Equal(4, await db.Facts.CountAsync());
            var fact = await db.Facts.SingleAsync(f => f.Id == factKey);
            Assert.Equal("native", fact.Origin);
            Assert.Equal(batch.Facts[0].FactId, fact.FactId);
            Assert.Equal(archive, fact.LegacyRecord);
            await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync(Previous));
            Assert.Equal(4, await db.Facts.CountAsync());
        }
    }

    [Fact]
    public async Task LegacyOnlyMigration_CanRoundTripWithoutLosingOriginalWrappedAttributes()
    {
        var legacy = FactStoreTests.LegacySegment(FactStoreTests.SegmentBatch());
        var raw = legacy.Attributes!.Value.GetRawText();
        await using var db = CreateDbContext();
        await db.GetService<IMigrator>().MigrateAsync(Previous);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Devices" ("Id", "OwnerId", "HardwareId", "DeviceName") VALUES (701, 'owner', 'hardware', 'Old PC');
            INSERT INTO "ActivitySegments" ("Id", "DeviceId", "Source", "IdentityKey", "Title", "StartTime", "EndTime", "Attributes")
            VALUES ({legacy.Id}, 701, 'browser', {legacy.IdentityKey}, {legacy.Title}, {legacy.StartTime}, {legacy.EndTime}, {raw}::jsonb);
            """);
        await db.Database.MigrateAsync();
        await db.GetService<IMigrator>().MigrateAsync(Previous);
        var restored = await db.Database.SqlQueryRaw<string>("SELECT \"Attributes\"::text AS \"Value\" FROM \"ActivitySegments\"").SingleAsync();
        Assert.True(JsonElement.DeepEquals(JsonDocument.Parse(raw).RootElement, JsonDocument.Parse(restored).RootElement));
        await db.Database.MigrateAsync();
        Assert.Single(await db.Facts.ToListAsync());
    }
}
