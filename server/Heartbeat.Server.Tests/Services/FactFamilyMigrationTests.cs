using Heartbeat.Server.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Tests.Services;

[Collection("postgres")]
public sealed class FactFamilyMigrationTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    protected override string InitialMigration => "20260829100458_AskingWindowIdentity";

    [Fact]
    public async Task OldDatabase_UpgradesDirectlyToTwoMinimalFactTables()
    {
        await using var db = CreateDbContext();
        var oldTables = await db.Database.SqlQueryRaw<long>("""
            SELECT oid::bigint AS "Value" FROM pg_class
            WHERE relname IN ('ActivitySegments', 'InputEvents') AND relkind = 'r' ORDER BY oid
            """).ToListAsync();
        Assert.Equal(["20260908141403_NativeFactCustody"], await db.Database.GetPendingMigrationsAsync());
        await db.Database.MigrateAsync();
        var familyTables = await db.Database.SqlQueryRaw<long>("""
            SELECT oid::bigint AS "Value" FROM pg_class
            WHERE relname IN ('Segments', 'Events') AND relkind = 'r' ORDER BY oid
            """).ToListAsync();
        Assert.Equal(oldTables, familyTables); // Evolve the old tables, rather than copy their rows to new ones.
        var columns = await db.Database.SqlQueryRaw<string>("""
            SELECT table_name || '.' || column_name AS "Value" FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name IN ('Segments', 'Events', 'Facts')
            ORDER BY table_name, column_name
            """).ToListAsync();
        var expected = new[]
        {
            "AppIdentityId", "FactId", "Id", "OwnerId", "Payload", "Revision", "Source", "StreamId"
        };
        Assert.Equal(expected.Append("Timestamp").Select(x => "Events." + x)
            .Concat(expected.Concat(["StartTime", "EndTime"]).Select(x => "Segments." + x)).Order(), columns);
        var timeTypes = await db.Database.SqlQueryRaw<string>("""
            SELECT udt_name AS "Value" FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name IN ('Segments', 'Events')
              AND column_name IN ('StartTime', 'EndTime', 'Timestamp')
            """).ToListAsync();
        Assert.Equal(3, timeTypes.Count);
        Assert.All(timeTypes, type => Assert.Equal("timestamptz", type));
    }
}
