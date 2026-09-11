using System.Text.Json;
using Heartbeat.Core.DTOs.Facts;
using Heartbeat.Core.Facts;
using Heartbeat.Server.Services;
using Heartbeat.Server.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Tests.Services;

[Collection("postgres")]
public sealed class LegacyObservationTakeoverTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task SameRevision_HistoricalUnknownCanBeCompleted_AndOldReplayKeepsTheCompletion()
    {
        var batch = FactStoreTests.SegmentBatch();
        var snapshot = batch.Facts[0];
        Guid rowId;
        await using (var db = CreateDbContext())
        {
            await new FactStore(db).IngestAsync("owner", batch);
            rowId = (await db.Segments.SingleAsync()).Id;
        }
        // Browser without an installation UUID has no reliable Observer. The recovered
        // old Target envelope supplies that missing evidence without a new observation.
        snapshot.ObserverId = Guid.NewGuid();
        snapshot.Target = new("device", "hardware");
        await using (var db = CreateDbContext())
            await new FactStore(db).IngestAsync("owner", batch);
        var observer = snapshot.ObserverId;
        snapshot.ObserverId = null;
        snapshot.Target = null;
        await using (var db = CreateDbContext())
        {
            var store = new FactStore(db);
            await store.IngestAsync("owner", batch);
            var read = Assert.Single(await store.ReadSegmentsAsync("owner", null, null, null));
            Assert.Equal(rowId, read.Id);
            Assert.Equal(1, read.Revision);
            Assert.Equal(observer, read.ObserverId);
            snapshot.ObserverId = Guid.NewGuid();
            snapshot.Target = new("device", "hardware");
            Assert.True((await Assert.ThrowsAsync<FactIngestException>(() => store.IngestAsync("owner", batch))).IsConflict);
        }
    }
    [Fact]
    public async Task SameRevision_LegacyEnvelopeAndExplicitObservationCompareConvertedCompleteSemantics()
    {
        var batch = BrowserApplicationContextTests.Batch("mac:com.test.browser", "hardware");
        var snapshot = batch.Facts[0];
        await using var db = CreateDbContext();
        var store = new FactStore(db);
        await store.IngestAsync("owner", batch);
        var original = Assert.Single(await store.ReadSegmentsAsync("owner", null, null, null));
        var converted = ObservationCompatibility.Convert(snapshot.ObserverId, snapshot.Target,
            snapshot.Payload!.Value, batch.Streams[0].Dimensions);
        snapshot.ObserverId = null;
        snapshot.Target = null;
        snapshot.CollectorId = converted.CollectorId;
        snapshot.Foi = converted.Foi;
        snapshot.Relations = converted.Relations;
        snapshot.Payload = ActivityFactPayload.Normalize(snapshot.Payload.Value);
        snapshot.Aspect = original.Aspect;
        await store.IngestAsync("owner", batch);
        db.ChangeTracker.Clear();
        var replay = Assert.Single(await store.ReadSegmentsAsync("owner", null, null, null));
        Assert.Equal(original.Id, replay.Id);
        Assert.Equal(original.Revision, replay.Revision);
        Assert.True(JsonElement.DeepEquals(original.Payload, replay.Payload));
        snapshot.Relations = [];
        Assert.True((await Assert.ThrowsAsync<FactIngestException>(() => store.IngestAsync("owner", batch))).IsConflict);
    }

    [Fact]
    public async Task UnknownHistoricalFoiAndAspect_CompleteWithoutChangingRevisionOrResult()
    {
        var batch = FactStoreTests.SegmentBatch("person");
        batch.Streams[0].Source = "custom";
        var snapshot = batch.Facts[0];
        snapshot.Payload = JsonSerializer.SerializeToElement(new[] { "opaque", "future" });
        await using var db = CreateDbContext();
        var store = new FactStore(db);
        await store.IngestAsync("owner", batch);
        var before = Assert.Single(await store.ReadSegmentsAsync("owner", null, null, null));
        Assert.Null(before.FoiId);
        Assert.Null(before.Aspect);
        snapshot.CollectorId = Guid.NewGuid();
        snapshot.Foi = new("account", "custom-service", "existing-account");
        snapshot.Relations = [];
        snapshot.Aspect = "custom.status";
        await store.IngestAsync("owner", batch);
        var after = Assert.Single(await store.ReadSegmentsAsync("owner", null, null, null));
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(1, after.Revision);
        Assert.NotNull(after.FoiId);
        Assert.Equal("custom.status", after.Aspect);
        Assert.True(JsonElement.DeepEquals(before.Payload, after.Payload));
        snapshot.Revision++;
        snapshot.Foi = new("account", "custom-service", "different-account");
        Assert.True((await Assert.ThrowsAsync<FactIngestException>(() => store.IngestAsync("owner", batch))).IsConflict);
    }

    [Fact]
    public async Task ConflictingHistoricalCompletion_RollsBackEvidenceAndKeepsUnknown()
    {
        var batch = FactStoreTests.SegmentBatch();
        await using var db = CreateDbContext();
        var store = new FactStore(db);
        await store.IngestAsync("owner", batch);
        var original = Assert.Single(await store.ReadSegmentsAsync("owner", null, null, null));
        batch.Facts[0].ObserverId = Guid.NewGuid();
        batch.Facts[0].Target = new("device", "hardware");
        batch.Facts[0].Payload = JsonSerializer.SerializeToElement(new { activityKey = "different" });
        Assert.True((await Assert.ThrowsAsync<FactIngestException>(() => store.IngestAsync("owner", batch))).IsConflict);
        var after = Assert.Single(await store.ReadSegmentsAsync("owner", null, null, null));
        Assert.Equal(original.Id, after.Id);
        Assert.Null(after.ObserverId);
        Assert.Equal(1, after.Revision);
        Assert.True(JsonElement.DeepEquals(original.Payload, after.Payload));
    }

    [Fact]
    public async Task HistoricalFoiCompletion_CannotAdoptRelationsForADifferentObject()
    {
        var batch = FactStoreTests.SegmentBatch();
        var snapshot = batch.Facts[0];
        snapshot.CollectorId = Guid.NewGuid();
        snapshot.Foi = new("machine", ObservationObjectScopes.Machine, "original-machine");
        snapshot.Relations = [];
        await using var db = CreateDbContext();
        var store = new FactStore(db);
        await store.IngestAsync("owner", batch);
        var original = Assert.Single(await store.ReadSegmentsAsync("owner", null, null, null));
        snapshot.Revision++;
        snapshot.Foi = null;
        snapshot.Relations = [ObservationCompatibility.ObservedOn(
            new("machine", ObservationObjectScopes.Machine, "different-machine"),
            new("app", ObservationObjectScopes.AppIdentity, "mac:com.test.browser"))];
        Assert.True((await Assert.ThrowsAsync<FactIngestException>(() => store.IngestAsync("owner", batch))).IsConflict);
        var after = Assert.Single(await store.ReadSegmentsAsync("owner", null, null, null));
        Assert.Equal(original.FoiId, after.FoiId);
        Assert.Equal(original.Id, after.Id);
        Assert.Equal(1, after.Revision);
        Assert.Empty(await db.Relations.ToListAsync());
    }

}
