using System.Text.Json;
using Heartbeat.Core.DTOs.Apps;
using Heartbeat.Core.DTOs.Facts;
using Heartbeat.Server.Entities;
using Heartbeat.Server.AppCatalog;
using Microsoft.Extensions.Logging.Abstractions;
using Heartbeat.Server.Services;
using Heartbeat.Server.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Heartbeat.Server.Tests.Services;

[Collection("postgres")]
public sealed class ServiceAccountTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlatformIdentityCorrectionKeepsServiceProductAndItsIcon(bool useCatalog)
    {
        await using var db = CreateDbContext();
        var store = new FactStore(db);
        await store.IngestAsync("owner", Batch());
        var before = Assert.Single(await store.ReadSegmentsAsync("owner", null, null, null));
        db.AppIdentities.Add(new AppIdentity { Key = "win:vrchat", AppId = before.AppId!.Value });
        db.AppIcons.Add(new AppIcon { OwnerId = "owner", AppId = before.AppId.Value, IconData = [1, 2, 3], UpdatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        if (useCatalog)
        {
            var snapshot = AppCatalogLoader.Parse("""{"schemaVersion":1,"catalogVersion":1,"products":[{"key":"corrected-platform-app","displayName":"Corrected App","identities":["win:vrchat"]}]}""");
            await new AppCatalogStartupService(db, NullLogger<AppCatalogStartupService>.Instance).ApplyAsync(snapshot);
        }
        else
        {
            var catalog = new AppCatalogRuntimeSnapshot(AppCatalogLoader.Parse("""{"schemaVersion":1,"catalogVersion":1,"products":[]}"""));
            catalog.Enable();
            await new AppCatalogOverrideService(db, new AppProductReconciliationService(db), catalog)
                .SetAsync("win:vrchat", "corrected-platform-app", "Corrected App", "admin");
        }
        var after = Assert.Single(await store.ReadSegmentsAsync("owner", null, null, null));
        Assert.Equal(before.AppId, after.AppId);
        Assert.Equal(before.TargetId, after.TargetId);
        Assert.Equal(new byte[] { 1, 2, 3 }, await new AppService(db).GetIconAsync("owner", before.AppId.Value));
        Assert.Equal("vrchat", (await db.Apps.SingleAsync(a => a.Id == before.AppId)).Key);
        Assert.NotEqual(before.AppId, (await db.AppIdentities.SingleAsync()).AppId);
    }

    [Theory]
    [InlineData("usr_11111111-1111-4111-8111-111111111111\n")]
    [InlineData("Display Name")]
    [InlineData("")]
    public async Task InvalidAccountReferencesDoNotCreateFactsOrProducts(string account)
    {
        await using var db = CreateDbContext();
        var batch = Batch();
        batch.Facts[0].Target = new ServiceAccountReference("vrchat", account).ToTarget();
        var store = new FactStore(db);
        await Assert.ThrowsAsync<FactIngestException>(() => store.IngestAsync("owner", batch));
        Assert.Empty(await store.ReadSegmentsAsync("owner", null, null, null));
        Assert.Empty(await db.ServiceAccounts.ToListAsync());
        Assert.Empty(await db.Apps.ToListAsync());
    }

    [Fact]
    public async Task AccountIdentityAndReferences_AreOwnerScopedAndImmutable_AndMergePreservesReplay()
    {
        await using var db = CreateDbContext();
        var store = new FactStore(db);
        var batch = Batch();
        await store.IngestAsync("owner", batch);
        await store.IngestAsync("other", batch);
        var before = Assert.Single(await store.ReadSegmentsAsync("owner", null, null, null));
        var other = Assert.Single(await store.ReadSegmentsAsync("other", null, null, null));
        Assert.NotEqual(before.TargetId, other.TargetId);
        Assert.Empty(await store.ReadSegmentsAsync("other", null, null, null, accountId: before.TargetId));
        async Task Reject(FormattableString sql, string code) => Assert.Equal(code,
            (await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(sql))).SqlState);
        await Reject($"UPDATE \"Segments\" SET \"TargetId\" = {before.TargetId} WHERE \"OwnerId\" = 'other'", "23503");
        await Reject($"DELETE FROM \"ServiceAccounts\" WHERE \"Id\" = {before.TargetId}", "23503");
        await Reject($"UPDATE \"ServiceAccounts\" SET \"OwnerId\" = 'other' WHERE \"Id\" = {before.TargetId}", "23514");
        await Reject($"UPDATE \"ServiceAccounts\" SET \"ServiceAccountId\" = 'usr_changed' WHERE \"Id\" = {before.TargetId}", "23514");
        await Reject($"UPDATE \"Segments\" SET \"TargetId\" = 987654 WHERE \"OwnerId\" = 'owner'", "23503");
        var account = await db.ServiceAccounts.AsNoTracking().SingleAsync(a => a.Id == before.TargetId);
        db.ServiceAccounts.Add(new ServiceAccount { OwnerId = account.OwnerId, ServiceKey = account.ServiceKey, ServiceAccountId = account.ServiceAccountId });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        db.Apps.Add(new App { Key = "social-vr", DisplayName = "Social VR" });
        await db.SaveChangesAsync();
        await new AppMergeService(db).MergeAsync(new AppMergeRequest { SourceAppKey = "vrchat", TargetAppKey = "social-vr", DryRun = false });
        db.ChangeTracker.Clear();
        await store.IngestAsync("owner", batch);
        var after = Assert.Single(await store.ReadSegmentsAsync("owner", null, null, null));
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.TargetId, after.TargetId);
        Assert.NotEqual(before.AppId, after.AppId);
        Assert.Null(after.DeviceId);
        Assert.Empty(await db.AppIdentities.ToListAsync());
        batch.Facts[0].Target = new ServiceAccountReference("vrchat", "usr_22222222-2222-4222-8222-222222222222").ToTarget();
        Assert.True((await Assert.ThrowsAsync<FactIngestException>(() => store.IngestAsync("owner", batch))).IsConflict);
        Assert.Equal(before.TargetId, Assert.Single(await store.ReadSegmentsAsync("owner", null, null, null)).TargetId);
    }

    internal static FactUploadRequest Batch()
    {
        var batch = FactStoreTests.SegmentBatch();
        batch.Streams[0].Source = "vrchat.account";
        batch.Streams[0].Subject.Kind = "account";
        batch.Streams[0].Subject.HardwareId = null;
        batch.Streams[0].Dimensions.Clear();
        batch.Facts[0].ObserverId = batch.Streams[0].CollectorInstanceId;
        batch.Facts[0].Target = new ServiceAccountReference("vrchat", "usr_11111111-1111-4111-8111-111111111111").ToTarget();
        batch.Facts[0].Payload = JsonSerializer.SerializeToElement(new { activityKey = "world|instance", title = "World", worldId = "world", instanceId = "instance" });
        return batch;
    }
}
