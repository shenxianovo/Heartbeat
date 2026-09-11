using Heartbeat.Server.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Heartbeat.Server.Tests.Services;

[Collection("postgres")]
public sealed class IndependentObservationMigrationTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    protected override string InitialMigration => "20260911060000_DirectObservations";

    [Fact]
    public async Task UpgradePreservesLegacyCustody_AndStoresIndependentFactsWithoutDeliveryKeys()
    {
        await using var db = CreateDbContext();
        var legacy = Guid.NewGuid();
        var stream = Guid.NewGuid();
        var subject = Guid.NewGuid();
        var collector = Guid.NewGuid();
        var foi = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Subjects" ("OwnerId", "SubjectId", "Kind") VALUES ('owner', {subject}, 'person');
            INSERT INTO "Streams" ("OwnerId", "StreamId", "SubjectId", "OutputId", "Source", "FactKind", "Dimensions", "Origin")
              VALUES ('owner', {stream}, {subject}, 'custom', 'custom', 'segment', {"{}"}::jsonb, 'native');
            INSERT INTO "Facts" ("Id", "OwnerId", "Kind", "StreamId", "FactId", "Revision", "Source", "Result", "StartTime", "EndTime")
              VALUES ({legacy}, 'owner', 'segment', {stream}, {Guid.NewGuid()}, 7, 'custom', {"{\"unknown\":[1,{\"x\":true}]}"}::jsonb, '2026-09-01T00:00:00.123456Z', '2026-09-01T00:01:00.654321Z');
            """);
        const string snapshot = """SELECT (to_jsonb(f) - 'AppReferenceEvidence')::text AS "Value" FROM "Facts" f ORDER BY "Id" """;
        var before = await db.Database.SqlQueryRaw<string>(snapshot).ToListAsync();
        var oldSchema = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Facts" ("Id", "OwnerId", "Kind", "Revision", "Result", "StartTime")
              VALUES ({Guid.NewGuid()}, 'owner', 'event', 1, {"{}"}::jsonb, '2026-09-01Z');
            """));
        Assert.Equal(PostgresErrorCodes.NotNullViolation, oldSchema.SqlState);
        Assert.Contains(oldSchema.ColumnName, new[] { "StreamId", "FactId", "Source" });
        await db.Database.MigrateAsync();
        await db.Database.MigrateAsync();
        Assert.Equal(before, await db.Database.SqlQueryRaw<string>(snapshot).ToListAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Null((await db.Facts.SingleAsync()).AppReferenceEvidence);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Objects" ("Id", "OwnerId", "Kind", "Scope", "Key") VALUES ({foi}, 'owner', 'person', 'custom', 'self');
            """);
        foreach (var kind in new[] { "segment", "event" })
        {
            var id = Guid.NewGuid();
            DateTimeOffset? end = kind == "segment" ? DateTimeOffset.Parse("2026-09-01T00:01:00.654321Z") : null;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Facts" ("Id", "OwnerId", "Kind", "Revision", "CollectorId", "FoiId", "Aspect", "Result", "StartTime", "EndTime")
                  VALUES ({id}, 'owner', {kind}, 2, {collector}, {foi}, 'unknown-aspect', {"{\"unknown\":[1,{\"x\":true}]}"}::jsonb, '2026-09-01T00:00:00.123456Z', {end});
                """);
            var fact = await db.Facts.Include(f => f.Stream).SingleAsync(f => f.Id == id);
            Assert.Null(fact.StreamId);
            Assert.Null(fact.FactId);
            Assert.Null(fact.Stream);
            Assert.Null(fact.Source);
            Assert.Equal(collector, fact.ObserverId);
            Assert.Equal(foi, fact.FoiId);
            Assert.Equal("unknown-aspect", fact.Aspect);
            Assert.True(fact.Payload.RootElement.GetProperty("unknown")[1].GetProperty("x").GetBoolean());
            Assert.Equal(foi, (await db.FactAttributions.SingleAsync(f => f.Id == id)).FoiId);
        }
        Assert.Equal(1, await db.FactStreams.CountAsync());
        Assert.Equal(1, await db.FactSubjects.CountAsync());
        Assert.Empty(await db.Relations.ToListAsync());
        Assert.Null((await db.Collectors.SingleAsync()).Kind);

        var collision = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Facts" ("Id", "OwnerId", "Kind", "Revision", "Result", "StartTime")
              VALUES ({legacy}, 'other-owner', 'event', 1, {"{}"}::jsonb, '2026-09-01Z');
            """));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, collision.SqlState);
        var partialDelivery = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Facts" ("Id", "OwnerId", "Kind", "FactId", "Revision", "Result", "StartTime")
              VALUES ({Guid.NewGuid()}, 'owner', 'event', {Guid.NewGuid()}, 1, {"{}"}::jsonb, '2026-09-01Z');
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, partialDelivery.SqlState);
        var foreignFoi = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Facts" ("Id", "OwnerId", "Kind", "Revision", "FoiId", "Result", "StartTime")
              VALUES ({Guid.NewGuid()}, 'other-owner', 'event', 1, {foi}, {"{}"}::jsonb, '2026-09-01Z');
            """));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, foreignFoi.SqlState);
    }
}
