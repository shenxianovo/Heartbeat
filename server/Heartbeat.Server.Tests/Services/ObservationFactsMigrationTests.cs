using Heartbeat.Server.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Heartbeat.Server.Tests.Services;

[Collection("postgres")]
public sealed class ObservationFactsMigrationTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    protected override string InitialMigration => "20260911004949_CompleteHistoricalTargets";

    [Fact]
    public async Task CrossFamilyRowIdCollisionRejectsUpgrade_WithoutChangingEitherFact()
    {
        await using var db = CreateDbContext();
        var id = Guid.NewGuid();
        var subject = Guid.NewGuid();
        foreach (var family in new[] { "segment", "event" })
        {
            var stream = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Subjects" ("OwnerId", "SubjectId", "Kind") VALUES ('owner', {subject}, 'person') ON CONFLICT DO NOTHING;
                INSERT INTO "Streams" ("OwnerId", "StreamId", "SubjectId", "OutputId", "Source", "FactKind", "Dimensions", "Origin")
                  VALUES ('owner', {stream}, {subject}, 'custom', 'custom', {family}, {"{}"}::jsonb, 'native');
                """);
            if (family == "segment")
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "Segments" ("Id", "OwnerId", "StreamId", "FactId", "Revision", "Source", "Payload", "StartTime", "EndTime")
                    VALUES ({id}, 'owner', {stream}, {id}, 3, 'custom', {"{\"kept\":true}"}::jsonb, '2026-09-01Z', '2026-09-01Z');
                    """);
            else
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "Events" ("Id", "OwnerId", "StreamId", "FactId", "Revision", "Source", "Payload", "Timestamp")
                    VALUES ({id}, 'owner', {stream}, {id}, 5, 'custom', {"{\"kept\":false}"}::jsonb, '2026-09-01Z');
                    """);
        }
        const string snapshot = """
            SELECT to_jsonb(s)::text AS "Value" FROM "Segments" s
            UNION ALL SELECT to_jsonb(e)::text AS "Value" FROM "Events" e ORDER BY "Value"
            """;
        var before = await db.Database.SqlQueryRaw<string>(snapshot).ToListAsync();
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync());
        Assert.Contains("share an Id", error.MessageText);
        Assert.Equal(before, await db.Database.SqlQueryRaw<string>(snapshot).ToListAsync());
        Assert.Equal(InitialMigration, (await db.Database.GetAppliedMigrationsAsync()).Last());
    }
}
