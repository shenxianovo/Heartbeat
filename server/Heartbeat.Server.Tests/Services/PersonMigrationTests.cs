using System.Text.Json;
using Heartbeat.Core.DTOs.Facts;
using Heartbeat.Server.Entities;
using Heartbeat.Server.Services;
using Heartbeat.Server.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Heartbeat.Server.Tests.Services;

[Collection("postgres")]
public sealed class PersonMigrationTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    protected override string InitialMigration => "20260910134705_VRChatServiceAccounts";

    [Fact]
    public async Task UpgradePreservesBothFamilyTablesAndAllSavedValues_AndCreatesNoUsageEvidence()
    {
        await using var db = CreateDbContext();
        var store = new FactStore(db);
        foreach (var kind in new[] { "device", "application-context", "account", "unknown-account" })
            foreach (var family in new[] { "segment", "event" })
            {
                var batch = kind.Contains("account", StringComparison.Ordinal) ? ServiceAccountTests.Batch() : FactStoreTests.SegmentBatch();
                var fact = batch.Facts[0];
                if (kind == "unknown-account") { fact.ObserverId = null; fact.Target = null; }
                if (kind is "device" or "application-context")
                {
                    fact.ObserverId = batch.Streams[0].CollectorInstanceId;
                    fact.Target = kind == "device" ? new FactTarget("device", "history") : new ApplicationContextReference("history", "mac:com.google.chrome").ToTarget();
                }
                batch.Streams[0].FactKind = family;
                if (family == "event") { fact.OccurredAt = fact.Start; fact.Start = fact.End = null; fact.IsFinal = null; }
                await store.IngestAsync("owner", batch);
            }
        const string snapshot = """
            SELECT 'segment:' || to_jsonb(s)::text AS "Value" FROM "Segments" s
            UNION ALL SELECT 'event:' || to_jsonb(e)::text AS "Value" FROM "Events" e ORDER BY "Value"
            """;
        var before = await db.Database.SqlQueryRaw<string>(snapshot).ToListAsync();
        Assert.Equal(8, before.Count);
        const string tables = """SELECT oid::bigint AS "Value" FROM pg_class WHERE relname IN ('Segments','Events') AND relkind = 'r' ORDER BY oid""";
        var oids = await db.Database.SqlQueryRaw<long>(tables).ToListAsync();
        await db.Database.MigrateAsync();
        await db.Database.MigrateAsync();
        Assert.Equal(oids, await db.Database.SqlQueryRaw<long>(tables).ToListAsync());
        Assert.Equal(before, await db.Database.SqlQueryRaw<string>(snapshot).ToListAsync());
        Assert.Empty(await db.Persons.ToListAsync());
        Assert.Empty(await db.PersonAssociations.ToListAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(0, (await new PersonFactQuery(db).ReadSegments("owner", null, null, 0, 50, default)).TotalCount);
    }

    [Theory]
    [InlineData("segment")]
    [InlineData("event")]
    public async Task PersonalTargetAndUsageReferences_AreGuardedByTheDatabase(string family)
    {
        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
        db.Users.AddRange(new User { Id = "owner", Username = "alice" }, new User { Id = "other", Username = "bob" });
        var person = new Person { OwnerId = "owner", Reference = Guid.NewGuid() };
        var other = new Person { OwnerId = "other", Reference = Guid.NewGuid() };
        db.Persons.AddRange(person, other);
        await db.SaveChangesAsync();
        var batch = FactStoreTests.SegmentBatch("person");
        batch.Streams[0].FactKind = family;
        var fact = batch.Facts[0];
        fact.ObserverId = batch.Streams[0].CollectorInstanceId;
        fact.Target = new PersonReference(person.Reference).ToTarget();
        fact.Payload = JsonSerializer.SerializeToElement(new { explicitPerson = true });
        if (family == "event") { fact.OccurredAt = fact.Start; fact.Start = fact.End = null; fact.IsFinal = null; }
        await new FactStore(db).IngestAsync("owner", batch);
        var table = family == "segment" ? "Segments" : "Events";
        async Task Reject(string sql, string state, params object[] args)
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql, args));
            Assert.Equal(state, error.SqlState);
        }
        await Reject($"UPDATE \"{table}\" SET \"TargetId\" = {{0}}", PostgresErrorCodes.ForeignKeyViolation, other.Id);
        await Reject($"UPDATE \"{table}\" SET \"TargetId\" = 9223372036854775807", PostgresErrorCodes.ForeignKeyViolation);
        await Reject("DELETE FROM \"Persons\" WHERE \"Id\" = {0}", PostgresErrorCodes.ForeignKeyViolation, person.Id);
        await Reject("UPDATE \"Persons\" SET \"Reference\" = {0} WHERE \"Id\" = {1}", PostgresErrorCodes.CheckViolation, Guid.NewGuid(), person.Id);
        await Reject("UPDATE \"Persons\" SET \"OwnerId\" = 'other' WHERE \"Id\" = {0}", PostgresErrorCodes.CheckViolation, person.Id);
        var device = new Device { OwnerId = "owner", HardwareId = "owned" };
        db.Devices.Add(device);
        var accountBatch = ServiceAccountTests.Batch();
        await db.SaveChangesAsync();
        await new FactStore(db).IngestAsync("owner", accountBatch);
        var account = await db.ServiceAccounts.SingleAsync();
        const string link = """INSERT INTO "PersonAssociations" ("OwnerId", "PersonId", "DeviceId", "AccountId", "Start", "End") VALUES ({0}, {1}, {2}, {3}, NULL, NULL)""";
        await Reject(link, PostgresErrorCodes.ForeignKeyViolation, "other", other.Id, device.Id, new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint, Value = DBNull.Value });
        await Reject(link, PostgresErrorCodes.ForeignKeyViolation, "owner", other.Id, device.Id, new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint, Value = DBNull.Value });
        await Reject(link, PostgresErrorCodes.ForeignKeyViolation, "other", other.Id, new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint, Value = DBNull.Value }, account.Id);
        await Reject(link, PostgresErrorCodes.CheckViolation, "owner", person.Id, device.Id, account.Id);
        await Reject(link, PostgresErrorCodes.CheckViolation, "owner", person.Id, new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint, Value = DBNull.Value }, new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint, Value = DBNull.Value });
        await db.Database.ExecuteSqlRawAsync(link, "owner", person.Id, device.Id, new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint, Value = DBNull.Value });
        await Reject("DELETE FROM \"Devices\" WHERE \"Id\" = {0}", PostgresErrorCodes.RestrictViolation, device.Id);
        await Reject("UPDATE \"PersonAssociations\" SET \"Start\" = '2026-09-01Z', \"End\" = '2026-09-01Z'", PostgresErrorCodes.CheckViolation);
        await Reject("""UPDATE "PersonAssociations" SET "Start" = '-infinity', "End" = NULL""", PostgresErrorCodes.CheckViolation);
    }
    [Theory]
    [InlineData("segment")]
    [InlineData("event")]
    public async Task StaleSnapshotCannotDeletePersonAfterAnotherTransactionCommitsAReference(string family)
    {
        long personId;
        var reference = Guid.NewGuid();
        await using (var setup = CreateDbContext())
        {
            await setup.Database.MigrateAsync();
            setup.Users.Add(new User { Id = "owner", Username = "alice" });
            var person = new Person { OwnerId = "owner", Reference = reference };
            setup.Persons.Add(person);
            await setup.SaveChangesAsync();
            personId = person.Id;
        }
        await using var stale = CreateDbContext();
        await using var transaction = await stale.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
        Assert.Equal(reference, (await stale.Persons.AsNoTracking().SingleAsync()).Reference);
        await using (var writer = CreateDbContext())
        {
            var batch = FactStoreTests.SegmentBatch("person");
            batch.Streams[0].FactKind = family;
            var fact = batch.Facts[0];
            fact.ObserverId = batch.Streams[0].CollectorInstanceId;
            fact.Target = new PersonReference(reference).ToTarget();
            if (family == "event") { fact.OccurredAt = fact.Start; fact.Start = fact.End = null; fact.IsFinal = null; }
            await new FactStore(writer).IngestAsync("owner", batch);
        }
        var error = await Assert.ThrowsAsync<PostgresException>(() => stale.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM "Persons" WHERE "Id" = {personId}"""));
        Assert.Equal(PostgresErrorCodes.SerializationFailure, error.SqlState);
        await transaction.RollbackAsync();
        await using var verify = CreateDbContext();
        Assert.Equal(reference, (await verify.Persons.SingleAsync()).Reference);
        var store = new FactStore(verify);
        var facts = family == "segment" ? await store.ReadSegmentsAsync("owner", null, null, null) : await store.ReadEventsAsync("owner", null, null, null);
        Assert.Equal(personId, Assert.Single(facts).TargetId);
    }

}
