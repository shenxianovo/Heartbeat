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
    public async Task MergeAndIdentityCorrection_MaintainContextReferencesWithoutMergingFacts_AndOfflineReplayConverges()
    {
        await using var db = CreateDbContext();
        var store = new FactStore(db);
        var first = Batch("mac:com.test.first");
        var second = Batch("mac:com.test.second");
        await store.IngestAsync("owner", first);
        await store.IngestAsync("owner", second);
        var before = await store.ReadSegmentsAsync("owner", null, null, null);
        Assert.Equal(2, before.Select(f => f.TargetId).Distinct().Count());
        var products = await db.AppIdentities.Include(i => i.App).ToDictionaryAsync(i => i.Key, i => i.App);
        var merge = new AppMergeService(db);
        await merge.MergeAsync(new AppMergeRequest { SourceAppKey = products["mac:com.test.first"].Key, TargetAppKey = products["mac:com.test.second"].Key, DryRun = false });
        db.ChangeTracker.Clear();
        var merged = await store.ReadSegmentsAsync("owner", null, null, null);
        Assert.Equal(2, merged.Count);
        Assert.Single(merged.Select(f => f.TargetId).Distinct());
        await store.IngestAsync("owner", first);
        await store.IngestAsync("owner", second);
        var catalog = new AppCatalogRuntimeSnapshot(AppCatalogLoader.Parse("""{"schemaVersion":1,"catalogVersion":1,"products":[]}"""));
        catalog.Enable();
        var overrides = new AppCatalogOverrideService(db, new AppProductReconciliationService(db), catalog);
        await overrides.SetAsync("mac:com.test.first", "corrected-browser", "Corrected browser", "admin");
        db.ChangeTracker.Clear();
        var corrected = await store.ReadSegmentsAsync("owner", null, null, null);
        Assert.Equal(2, corrected.Select(f => f.TargetId).Distinct().Count());
        Assert.Equal(merged.Single(f => f.FactId == second.Facts[0].FactId).TargetId,
            corrected.Single(f => f.FactId == second.Facts[0].FactId).TargetId);
        await store.IngestAsync("owner", first);
        first.Facts[0].ObserverId = null;
        first.Facts[0].Target = null;
        await store.IngestAsync("owner", first);
        Assert.Equal(before.Select(f => f.Id).Order(), corrected.Select(f => f.Id).Order());
        Assert.All(corrected, row => Assert.Equal(1, row.Revision));
        Assert.Empty(await new UsageService(db).GetUsageAsync("owner", null, null, null));
    }

    [Fact]
    public async Task ContextUniquenessAndOwnership_AreEnforcedAtStorageBoundary_AndDeviceAppQueriesRemainIsolated()
    {
        var first = Batch("mac:com.test.browser");
        var second = Batch("mac:com.test.browser");
        await Task.WhenAll(new[] { first, second }.Select(async batch =>
        {
            await using var concurrent = CreateDbContext();
            await new FactStore(concurrent).IngestAsync("owner", batch);
        }));
        await using var db = CreateDbContext();
        var store = new FactStore(db);
        var contexts = await db.ApplicationContexts.ToListAsync();
        var context = Assert.Single(contexts);
        var rows = await store.ReadSegmentsAsync("owner", context.DeviceId, null, null, appId: context.AppId);
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows.Select(f => f.ObserverId).Distinct().Count());
        await store.IngestAsync("other", first);
        Assert.Empty(await store.ReadSegmentsAsync("other", context.DeviceId, null, null));
        var invalid = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"Facts\" SET \"TargetId\" = {context.Id} WHERE \"OwnerId\" = 'other'"));
        Assert.Equal("23503", invalid.SqlState);
        var referenced = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM \"ApplicationContexts\" WHERE \"Id\" = {context.Id}"));
        Assert.Equal("23503", referenced.SqlState);
        var duplicate = new ApplicationContextRecord { OwnerId = "owner", DeviceId = context.DeviceId, AppId = context.AppId };
        db.ApplicationContexts.Add(duplicate);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        first.Facts[0].ObserverId = Guid.NewGuid();
        var conflict = await Assert.ThrowsAsync<FactIngestException>(() => store.IngestAsync("owner", first));
        Assert.True(conflict.IsConflict);
        Assert.Equal(2, (await store.ReadSegmentsAsync("owner", context.DeviceId, null, null)).Count);
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
