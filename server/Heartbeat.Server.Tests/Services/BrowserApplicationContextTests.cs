using System.Text.Json;
using Heartbeat.Core.DTOs.Apps;
using Heartbeat.Core.DTOs.Facts;
using Heartbeat.Server.AppCatalog;
using Heartbeat.Server.Entities;
using Heartbeat.Server.Services;
using Heartbeat.Server.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Tests.Services;

[Collection("postgres")]
public sealed class BrowserApplicationContextTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task MergeAndIdentityCorrection_MaintainObjectReferencesWithoutMergingFacts_AndOfflineReplayConverges()
    {
        await using var db = CreateDbContext();
        var store = new FactStore(db);
        var first = NativeBatch("mac:com.test.first");
        var second = NativeBatch("mac:com.test.second");
        await store.IngestAsync("owner", first);
        await store.IngestAsync("owner", second);
        var before = await store.ReadSegmentsAsync("owner", null, null, null);
        Assert.Equal(2, before.Select(f => f.FoiId).Distinct().Count());
        var products = await db.AppIdentities.Include(i => i.App).ToDictionaryAsync(i => i.Key, i => i.App);
        var merge = new AppMergeService(db);
        await merge.MergeAsync(new AppMergeRequest { SourceAppKey = products["mac:com.test.first"].Key, TargetAppKey = products["mac:com.test.second"].Key, DryRun = false });
        db.ChangeTracker.Clear();
        var merged = await store.ReadSegmentsAsync("owner", null, null, null);
        Assert.Equal(2, merged.Count);
        Assert.Single(merged.Select(f => f.FoiId).Distinct());
        await store.IngestAsync("owner", first);
        await store.IngestAsync("owner", second);
        var catalog = new AppCatalogRuntimeSnapshot(AppCatalogLoader.Parse("""{"schemaVersion":1,"catalogVersion":1,"products":[]}"""));
        catalog.Enable();
        var overrides = new AppCatalogOverrideService(db, new AppProductReconciliationService(db), catalog);
        await overrides.SetAsync("mac:com.test.first", "corrected-browser", "Corrected browser", "admin");
        db.ChangeTracker.Clear();
        var corrected = await store.ReadSegmentsAsync("owner", null, null, null);
        Assert.Equal(2, corrected.Select(f => f.FoiId).Distinct().Count());
        Assert.Equal(merged.Single(f => f.FactId == second.Facts[0].FactId).FoiId,
            corrected.Single(f => f.FactId == second.Facts[0].FactId).FoiId);
        await store.IngestAsync("owner", first);
        Assert.Equal(before.Select(f => f.Id).Order(), corrected.Select(f => f.Id).Order());
        Assert.All(corrected, row => Assert.Equal(1, row.Revision));
        Assert.Empty(await new UsageService(db).GetUsageAsync("owner", null, null, null));
    }

    [Fact]
    public async Task ProductMergePreservesNativeFactsWithoutPlatformIdentity_AndOldProductReplay()
    {
        await using var db = CreateDbContext();
        var batch = NativeBatch("mac:unused");
        var fact = batch.Facts[0];
        fact.Foi = new ObservationObjectReference("app", ObservationObjectScopes.App, "old-browser");
        fact.Relations![0].Members[0] = new FactRelationMember("app", fact.Foi);
        var store = new FactStore(db);
        await store.IngestAsync("owner", batch);
        var before = Assert.Single(await store.ReadSegmentsAsync("owner", null, null, null));
        Assert.Null((await db.Facts.SingleAsync()).AppIdentityId);
        db.Apps.Add(new App { Key = "browser", DisplayName = "Browser" });
        await db.SaveChangesAsync();
        await new AppMergeService(db).MergeAsync(new AppMergeRequest
            { SourceAppKey = "old-browser", TargetAppKey = "browser", DryRun = false });
        var after = Assert.Single(await store.ReadSegmentsAsync("owner", null, null, null));
        Assert.NotEqual(before.FoiId, after.FoiId);
        await store.IngestAsync("owner", batch);
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.Revision, after.Revision);
        Assert.True(JsonElement.DeepEquals(before.Payload, after.Payload));
        Assert.Equal(after.FoiId, await db.RelationMembers.Where(m => m.Role == "app").Select(m => (Guid?)m.ObjectId).SingleAsync());
    }

    private static FactUploadRequest NativeBatch(string appIdentity)
    {
        var batch = Batch(appIdentity);
        var fact = batch.Facts[0];
        var app = new ObservationObjectReference("app", ObservationObjectScopes.AppIdentity, appIdentity);
        fact.CollectorId = fact.ObserverId;
        fact.ObserverId = null;
        fact.Target = null;
        fact.Foi = app;
        fact.Aspect = "selected-page";
        fact.Payload = JsonSerializer.SerializeToElement(new { activityKey = "https://example.com", title = "Page" });
        fact.Relations = [new FactRelationSnapshot("observed-on", [
            new FactRelationMember("app", app),
            new FactRelationMember("device", new ObservationObjectReference("machine", ObservationObjectScopes.Machine, "hardware"))])];
        return batch;
    }

    internal static FactUploadRequest Batch(string appIdentityKey, string hardware = "hardware")
    {
        var batch = FactStoreTests.SegmentBatch();
        var observer = Guid.NewGuid();
        batch.Streams[0].Subject.HardwareId = hardware;
        batch.Streams[0].Dimensions = new Dictionary<string, string>
        {
            ["appIdentityKey"] = appIdentityKey, ["externalHostIdentity"] = observer.ToString("D")
        };
        batch.Facts[0].ObserverId = observer;
        batch.Facts[0].Target = new ApplicationContextReference(hardware, appIdentityKey).ToTarget();
        return batch;
    }
}
