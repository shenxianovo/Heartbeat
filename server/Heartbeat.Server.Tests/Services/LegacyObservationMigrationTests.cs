using Heartbeat.Server.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Heartbeat.Server.Tests.Services;

[Collection("postgres")]
public sealed class LegacyObservationMigrationTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    protected override string InitialMigration => "20260908141403_NativeFactCustody";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SupportedBaselinesPreserveEveryLegacyKeyAndValue_ThroughRepeatedUpgrade(bool historicalTargetsAlreadyApplied)
    {
        await using var db = CreateDbContext();
        var clientFactId = Guid.NewGuid();
        var installation = Guid.NewGuid();
        var runtime = Guid.NewGuid();
        var subject = Guid.NewGuid();
        var account = Guid.NewGuid();
        var unknown = Guid.NewGuid();
        var streams = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToArray();
        var expected = new Dictionary<Guid, (string? FoiKind, string? Aspect, Guid? Collector, bool HasApp)>();
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "Apps" ("Id", "Key", "DisplayName", "IsProvisional") VALUES (701, 'chrome', 'Chrome', false);
            INSERT INTO "AppIdentities" ("Id", "Key", "AppId") VALUES (701, 'mac:chrome', 701);
            """);
        foreach (var owner in new[] { "first-owner", "second-owner" })
        {
            var device = owner == "first-owner" ? 701 : 702;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Devices" ("Id", "OwnerId", "HardwareId", "DeviceName") VALUES ({device}, {owner}, {owner}, 'PC');
                INSERT INTO "Subjects" ("OwnerId", "SubjectId", "Kind", "DeviceId")
                  VALUES ({owner}, {subject}, 'machine', {device}), ({owner}, {account}, 'account', NULL), ({owner}, {unknown}, 'person', NULL);
                """);
            for (var index = 0; index < streams.Length; index++)
            {
                // Two streams with the same FactId per family, then the same identities under another Owner.
                var scenario = index / 2;
                var kind = scenario >= 3 ? "event" : "segment";
                var source = scenario switch { 0 or 3 => "system", 1 => "browser", 2 => "vrchat.account", _ => "custom" };
                var streamSubject = scenario == 2 ? account : scenario == 4 ? unknown : subject;
                var native = index % 2 == 0;
                var stream = streams[index];
                var id = Guid.NewGuid();
                var payload = scenario switch
                {
                    3 => """{"eventType":"keyDown","codeSet":"windows-vk-v1","code":65,"unknown":[null,{"keep":true}]}""",
                    4 => """[null,{"nested":[1,true,"未知"]}]""",
                    _ => """{"activityKey":"same","title":"kept","unknown":[null,{"precise":12345678901234567890.123456789}]}"""
                };
                var dimensions = $$"""{"externalHostIdentity":"{{installation}}","unknown":"retained"}""";
                var origin = native ? "native" : "legacy-import";
                long? identity = scenario == 1 ? 701 : null;
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "Streams" ("OwnerId", "StreamId", "SubjectId", "CollectorInstanceId", "OutputId", "Source", "FactKind", "Dimensions", "Origin")
                      VALUES ({owner}, {stream}, {streamSubject}, {runtime}, 'output', {source}, {kind}, {dimensions}::jsonb, {origin});
                    INSERT INTO "FactGaps" ("OwnerId", "StreamId", "GapId", "Start", "End", "Reason", "EstimatedFactsLost")
                      VALUES ({owner}, {stream}, {clientFactId}, 639238176001234567, 639238176001234569, 'collector-offline', NULL);
                    """);
                if (kind == "segment")
                    await db.Database.ExecuteSqlInterpolatedAsync($"""
                        INSERT INTO "Segments" ("Id", "OwnerId", "StreamId", "FactId", "Revision", "Source", "AppIdentityId", "StartTime", "EndTime", "Payload")
                          VALUES ({id}, {owner}, {stream}, {clientFactId}, 7, {source}, {identity}, '2026-09-01T00:00:00.123456Z', '2026-09-01T00:00:00.123456Z', {payload}::jsonb);
                        """);
                else
                    await db.Database.ExecuteSqlInterpolatedAsync($"""
                        INSERT INTO "Events" ("Id", "OwnerId", "StreamId", "FactId", "Revision", "Source", "AppIdentityId", "Timestamp", "Payload")
                          VALUES ({id}, {owner}, {stream}, {clientFactId}, 9, {source}, {identity}, '2026-09-01T00:00:00.654321Z', {payload}::jsonb);
                        """);
                expected[id] = (scenario switch { 1 => "app", 2 => "account", 4 => null, _ => "machine" },
                    scenario switch { 0 => "desktop-activity", 1 => "selected-page", 2 => "account-location", 3 => "input", _ => null },
                    native && scenario != 4 ? scenario == 1 ? installation : runtime : null, identity is not null);
            }
        }
        const string deliverySnapshot = """
            SELECT 'subject:' || to_jsonb(s)::text AS "Value" FROM "Subjects" s
            UNION ALL SELECT 'stream:' || to_jsonb(s)::text FROM "Streams" s
            UNION ALL SELECT 'gap:' || to_jsonb(g)::text FROM "FactGaps" g ORDER BY "Value"
            """;
        const string legacySnapshot = """
            SELECT jsonb_build_array("Id", "OwnerId", 'segment', "StreamId", "FactId", "Revision", "Source", "AppIdentityId", "StartTime", "EndTime", "Payload")::text AS "Value" FROM "Segments"
            UNION ALL SELECT jsonb_build_array("Id", "OwnerId", 'event', "StreamId", "FactId", "Revision", "Source", "AppIdentityId", "Timestamp", NULL, "Payload")::text FROM "Events" ORDER BY "Value"
            """;
        var before = await db.Database.SqlQueryRaw<string>(legacySnapshot).ToListAsync();
        var delivery = await db.Database.SqlQueryRaw<string>(deliverySnapshot).ToListAsync();
        if (historicalTargetsAlreadyApplied)
            await db.GetService<IMigrator>().MigrateAsync("20260911004949_CompleteHistoricalTargets");
        await db.Database.MigrateAsync();
        const string migratedSnapshot = """
            SELECT jsonb_build_array("Id", "OwnerId", "Kind", "StreamId", "FactId", "Revision", "Source", "AppIdentityId", "StartTime", "EndTime", "Result")::text AS "Value" FROM "Facts" ORDER BY "Value"
            """;
        Assert.Equal(before, await db.Database.SqlQueryRaw<string>(migratedSnapshot).ToListAsync());
        Assert.Equal(delivery, await db.Database.SqlQueryRaw<string>(deliverySnapshot).ToListAsync());
        var objects = await db.Objects.ToDictionaryAsync(o => o.Id);
        var facts = await db.Facts.ToListAsync();
        Assert.Equal(20, facts.Count);
        foreach (var fact in facts)
        {
            var mapping = expected[fact.Id];
            Assert.Equal(mapping.Collector, fact.ObserverId);
            Assert.Equal(mapping.Aspect, fact.Aspect);
            Assert.Equal(mapping.FoiKind, fact.FoiId is { } foi ? objects[foi].Kind : null);
            Assert.Null(fact.AppReferenceEvidence); // An old platform identity does not prove an original product-reference spelling.
            var relations = await db.Relations.Where(r => r.FactId == fact.Id).ToListAsync();
            if (!mapping.HasApp) { Assert.Empty(relations); continue; }
            var relation = Assert.Single(relations);
            Assert.Equal(fact.OwnerId, relation.OwnerId);
            Assert.Equal(DateTimeOffset.Parse("2026-09-01T00:00:00.123456Z"), relation.ValidFrom);
            Assert.Equal(relation.ValidFrom, relation.ValidTo);
            Assert.Equal(fact.Id.ToString(), relation.Evidence!.RootElement.GetProperty("factId").GetString());
            var members = await db.RelationMembers.Where(m => m.RelationId == relation.Id).ToListAsync();
            Assert.Equal(fact.FoiId, Assert.Single(members, m => m.Role == "app").ObjectId);
            var machine = objects[Assert.Single(members, m => m.Role == "device").ObjectId];
            Assert.Equal(fact.OwnerId, machine.OwnerId);
            Assert.Equal(fact.OwnerId, machine.Key);
        }
        const string completeSnapshot = """
            SELECT 'fact:' || to_jsonb(f)::text AS "Value" FROM "Facts" f
            UNION ALL SELECT 'object:' || to_jsonb(o)::text FROM "Objects" o
            UNION ALL SELECT 'relation:' || to_jsonb(r)::text FROM "Relations" r
            UNION ALL SELECT 'member:' || to_jsonb(m)::text FROM "RelationMembers" m ORDER BY "Value"
            """;
        var accepted = await db.Database.SqlQueryRaw<string>(completeSnapshot).ToListAsync();
        await db.Database.MigrateAsync();
        Assert.Equal(accepted, await db.Database.SqlQueryRaw<string>(completeSnapshot).ToListAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task LateBackfillFailureRollsBackObjectsAndFacts_AndSameDatabaseCanRetry()
    {
        await using var db = CreateDbContext();
        var id = Guid.NewGuid();
        var stream = Guid.NewGuid();
        var subject = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Devices" ("Id", "OwnerId", "HardwareId", "DeviceName") VALUES (701, 'owner', 'hardware', 'PC');
            INSERT INTO "Subjects" ("OwnerId", "SubjectId", "Kind", "DeviceId") VALUES ('owner', {subject}, 'machine', 701);
            INSERT INTO "Streams" ("OwnerId", "StreamId", "SubjectId", "OutputId", "Source", "FactKind", "Dimensions", "Origin")
              VALUES ('owner', {stream}, {subject}, 'activity', 'system', 'segment', {"{}"}::jsonb, 'legacy-import');
            INSERT INTO "Segments" ("Id", "OwnerId", "StreamId", "FactId", "Revision", "Source", "StartTime", "EndTime", "Payload")
              VALUES ({id}, 'owner', {stream}, {Guid.NewGuid()}, 7, 'system', '2026-09-01Z', '2026-09-01T00:01Z', {"{\"activityKey\":\"kept\",\"unknown\":[1,null]}"}::jsonb);
            """);
        const string beforeObjects = "20260911024930_ObservationFacts";
        await db.GetService<IMigrator>().MigrateAsync(beforeObjects);
        const string snapshot = """
            SELECT (to_jsonb(f) - 'FoiId' - 'Aspect' - 'AppReferenceEvidence')::text AS "Value" FROM "Facts" f ORDER BY "Id"
            """;
        var before = await db.Database.SqlQueryRaw<string>(snapshot).ToListAsync();
        // Fail after object creation/backfill, when the migration reaches the first Fact update.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fixture_reject_backfill() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE check_violation USING MESSAGE = 'fixture late backfill failure'; END $$;
            CREATE TRIGGER "Fixture_RejectBackfill" BEFORE UPDATE ON "Facts" FOR EACH ROW EXECUTE FUNCTION fixture_reject_backfill();
            """);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync());
            Assert.Equal("fixture late backfill failure", error.MessageText);
            Assert.Equal(beforeObjects, (await db.Database.GetAppliedMigrationsAsync()).Last());
            Assert.Equal(before, await db.Database.SqlQueryRaw<string>(snapshot).ToListAsync());
            Assert.Equal(0, await db.Database.SqlQueryRaw<int>("""
                SELECT count(*)::int AS "Value" FROM information_schema.tables
                WHERE table_schema = 'public' AND table_name IN ('Objects', 'Collectors', 'Relations', 'RelationMembers')
                """).SingleAsync());
        }
        await db.Database.ExecuteSqlRawAsync("""
            DROP TRIGGER "Fixture_RejectBackfill" ON "Facts";
            DROP FUNCTION fixture_reject_backfill();
            """);
        await db.Database.MigrateAsync();
        await db.Database.MigrateAsync();
        Assert.Equal(before, await db.Database.SqlQueryRaw<string>(snapshot).ToListAsync());
        var saved = await db.Facts.SingleAsync();
        Assert.Equal(id, saved.Id);
        Assert.Null(saved.ObserverId);
        Assert.Equal("machine", (await db.Objects.SingleAsync()).Kind);
        Assert.Equal("desktop-activity", saved.Aspect);
    }

    [Fact]
    public async Task IndependentFamilyIdCollisionStopsEveryRetry_WithoutChoosingOrDroppingEitherRow()
    {
        await using var db = CreateDbContext();
        var id = Guid.NewGuid();
        var subject = Guid.NewGuid();
        var stream = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Subjects" ("OwnerId", "SubjectId", "Kind") VALUES ('owner', {subject}, 'person');
            INSERT INTO "Streams" ("OwnerId", "StreamId", "SubjectId", "OutputId", "Source", "FactKind", "Dimensions", "Origin")
              VALUES ('owner', {stream}, {subject}, 'custom', 'custom', 'segment', {"{}"}::jsonb, 'native');
            INSERT INTO "Segments" ("Id", "OwnerId", "StreamId", "FactId", "Revision", "Source", "StartTime", "EndTime", "Payload")
              VALUES ({id}, 'owner', {stream}, {id}, 7, 'custom', '2026-09-01Z', '2026-09-01Z', {"[1,null]"}::jsonb);
            INSERT INTO "Events" ("Id", "OwnerId", "StreamId", "FactId", "Revision", "Source", "Timestamp", "Payload")
              VALUES ({id}, 'owner', {stream}, {id}, 9, 'custom', '2026-09-01Z', {"{\"independent\":true}"}::jsonb);
            """);
        await db.GetService<IMigrator>().MigrateAsync("20260911004949_CompleteHistoricalTargets");
        const string snapshot = """
            SELECT 'segment:' || to_jsonb(s)::text AS "Value" FROM "Segments" s
            UNION ALL SELECT 'event:' || to_jsonb(e)::text FROM "Events" e ORDER BY "Value"
            """;
        var before = await db.Database.SqlQueryRaw<string>(snapshot).ToListAsync();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync());
            Assert.Contains("resolve the explicit identity mapping", error.MessageText);
            Assert.Equal(before, await db.Database.SqlQueryRaw<string>(snapshot).ToListAsync());
            Assert.Equal("20260911004949_CompleteHistoricalTargets", (await db.Database.GetAppliedMigrationsAsync()).Last());
        }
    }
}
