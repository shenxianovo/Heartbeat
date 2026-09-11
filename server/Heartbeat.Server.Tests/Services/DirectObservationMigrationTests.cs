using Heartbeat.Server.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Heartbeat.Server.Tests.Services;

[Collection("postgres")]
public sealed class DirectObservationMigrationTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    protected override string InitialMigration => "20260911043115_ExplicitFactAspects";

    [Fact]
    public async Task DirectWritesPreserveMigratedFactsAndRelations_AndGuardObjectOwnership()
    {
        await using var db = CreateDbContext();
        var factId = Guid.NewGuid();
        var streamId = Guid.NewGuid();
        var subjectId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Users" ("Id", "Username", "IsPublic", "LastSeenAt") VALUES ('owner', 'owner', false, '2026-09-01Z');
            INSERT INTO "Devices" ("Id", "OwnerId", "HardwareId", "DeviceName") VALUES (1, 'owner', 'mac', 'Mac');
            INSERT INTO "Apps" ("Id", "Key", "DisplayName", "IsProvisional") VALUES (1, 'chrome', 'Chrome', false);
            INSERT INTO "AppIdentities" ("Id", "Key", "AppId") VALUES (1, 'mac:chrome', 1);
            INSERT INTO "Persons" ("Id", "OwnerId", "Reference") VALUES (1, 'owner', {Guid.NewGuid()});
            INSERT INTO "PersonAssociations" ("OwnerId", "PersonId", "DeviceId", "Start", "End") VALUES ('owner', 1, 1, NULL, NULL);
            INSERT INTO "Subjects" ("OwnerId", "SubjectId", "Kind", "DeviceId") VALUES ('owner', {subjectId}, 'machine', 1);
            INSERT INTO "Streams" ("OwnerId", "StreamId", "SubjectId", "OutputId", "Source", "FactKind", "Dimensions", "Origin")
              VALUES ('owner', {streamId}, {subjectId}, 'activity', 'system', 'segment', {"{}"}::jsonb, 'native');
            INSERT INTO "Facts" ("Id", "OwnerId", "Kind", "StreamId", "FactId", "Revision", "Source", "TargetKind", "TargetId", "AppIdentityId", "Result", "StartTime", "EndTime")
              VALUES ({factId}, 'owner', 'segment', {streamId}, {factId}, 3, 'system', 'device', 1, 1, {"{\"activityKey\":\"keep\",\"extra\":[1,2]}"}::jsonb, '2026-09-01Z', '2026-09-01T00:01Z');
            """);
        const string factSnapshot = """SELECT to_jsonb(f)::text AS "Value" FROM "Facts" f ORDER BY "Id" """;
        const string relationSnapshot = """SELECT (to_jsonb(r) - 'AssociationId')::text AS "Value" FROM "Relations" r ORDER BY "Id" """;
        const string memberSnapshot = """SELECT to_jsonb(m)::text AS "Value" FROM "RelationMembers" m ORDER BY "RelationId", "Role" """;
        var facts = await db.Database.SqlQueryRaw<string>(factSnapshot).ToListAsync();
        var relations = await db.Database.SqlQueryRaw<string>(relationSnapshot).ToListAsync();
        var members = await db.Database.SqlQueryRaw<string>(memberSnapshot).ToListAsync();
        await db.Database.MigrateAsync();
        await db.Database.MigrateAsync();
        Assert.Equal(facts, await db.Database.SqlQueryRaw<string>(factSnapshot).ToListAsync());
        Assert.Equal(relations, await db.Database.SqlQueryRaw<string>(relationSnapshot).ToListAsync());
        Assert.Equal(members, await db.Database.SqlQueryRaw<string>(memberSnapshot).ToListAsync());
        Assert.False(db.Database.HasPendingModelChanges());

        var app = await db.Objects.SingleAsync(o => o.Kind == "app");
        var fact = await db.Facts.SingleAsync();
        fact.FoiId = app.Id;
        fact.TargetKind = null;
        fact.TargetId = null;
        fact.Aspect = "explicit";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        fact = await db.Facts.SingleAsync();
        Assert.Equal(app.Id, fact.FoiId);
        Assert.Equal("explicit", fact.Aspect);
        Assert.Equal(relations, await db.Database.SqlQueryRaw<string>(relationSnapshot).ToListAsync());
        Assert.Equal(members, await db.Database.SqlQueryRaw<string>(memberSnapshot).ToListAsync());
        var emptyUsage = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("""
            UPDATE "Relations" SET "ValidFrom" = '2026-09-01Z', "ValidTo" = '2026-09-01Z' WHERE "Kind" = 'used-by'
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, emptyUsage.SqlState);
        var other = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Objects" ("Id", "OwnerId", "Kind", "Scope", "Key") VALUES ({other}, 'other', 'account', 'service', 'account');
            """);
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "Facts" SET "FoiId" = {other} WHERE "Id" = {factId}
            """));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
    }
}
