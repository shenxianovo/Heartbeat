using Heartbeat.Core;
using Heartbeat.Core.DTOs.Knowledge;
using Heartbeat.Server.AppCatalog;
using Heartbeat.Server.Entities;
using Heartbeat.Server.Services;
using Heartbeat.Server.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Heartbeat.Server.Tests.Services;

[Collection("postgres")]
public class AppCatalogReconcilerTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Startup_PreservesEstablishedId_WhenCanonicalKeyIsOccupiedByProvisional()
    {
        await using var db = CreateDbContext();
        var formal = new App
        {
            Key = "google-chrome",
            DisplayName = "Chrome.exe",
            IsProvisional = false
        };
        var provisional = new App
        {
            Key = "chrome",
            DisplayName = "Google Chrome",
            IsProvisional = true
        };
        var windows = new AppIdentity { Key = "win:chrome", App = formal };
        var mac = new AppIdentity { Key = "mac:com.google.chrome", App = provisional };
        var device = new Device
        {
            OwnerId = "owner", HardwareId = "hw", DeviceName = "Mac",
            CurrentApp = "Google Chrome", CurrentAppIdentity = mac
        };
        db.AddRange(formal, provisional, windows, mac, device);
        await db.SaveChangesAsync();
        var segment = new ActivitySegment
        {
            Id = Guid.CreateVersion7(), DeviceId = device.Id, Source = ActivitySources.System,
            IdentityKey = "chrome", AppId = provisional.Id, AppIdentityId = mac.Id,
            StartTime = Now.AddMinutes(-1), EndTime = Now
        };
        db.ActivitySegments.Add(segment);
        await db.SaveChangesAsync();
        var establishedId = formal.Id;

        var snapshot = Snapshot(1);
        var runtime = new AppCatalogRuntimeSnapshot(snapshot);
        var service = new AppCatalogStartupService(
            db, NullLogger<AppCatalogStartupService>.Instance, new FixedClock(Now),
            new AppProductReconciliationService(db), runtime);

        Assert.Equal(AppCatalogStartupResult.Applied, await service.ApplyAsync(snapshot));
        db.ChangeTracker.Clear();

        var app = await db.Apps.Include(x => x.Identities).SingleAsync();
        Assert.Equal(establishedId, app.Id);
        Assert.Equal("chrome", app.Key);
        Assert.Equal("Google Chrome", app.DisplayName);
        Assert.False(app.IsProvisional);
        Assert.Equal(
            ["mac:com.google.chrome", "win:chrome"],
            app.Identities.Select(x => x.Key).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(establishedId, (await db.ActivitySegments.SingleAsync()).AppId);
        Assert.Equal("Google Chrome", (await db.Devices.SingleAsync()).CurrentApp);
        Assert.True(runtime.IsEnabled);
    }

    [Fact]
    public async Task ResolveAsync_FirstCatalogIdentity_CreatesCanonicalProductDirectly()
    {
        var snapshot = Snapshot(1);
        var runtime = new AppCatalogRuntimeSnapshot(snapshot);
        runtime.Enable();
        await using var db = CreateDbContext();

        var identity = await new AppIdentityService(db, runtime)
            .ResolveAsync("MAC:COM.GOOGLE.CHROME", "Observed Chrome");

        Assert.Equal("mac:com.google.chrome", identity.Key);
        Assert.Equal("chrome", identity.App.Key);
        Assert.Equal("Google Chrome", identity.App.DisplayName);
        Assert.False(identity.App.IsProvisional);
        Assert.Single(await db.Apps.ToListAsync());
    }

    [Fact]
    public async Task Startup_ProductFailureRollsBackMappingsStateAndAudit()
    {
        await using var db = CreateDbContext();
        var formal = new App { Key = "google-chrome", DisplayName = "Chrome.exe" };
        formal.Identities.Add(new AppIdentity { Key = "win:chrome" });
        var unrelated = new App { Key = "chrome", DisplayName = "Unrelated", IsProvisional = true };
        unrelated.Identities.Add(new AppIdentity { Key = "win:unrelated" });
        db.AddRange(formal, unrelated);
        await db.SaveChangesAsync();
        var snapshot = Snapshot(1);
        var service = new AppCatalogStartupService(
            db, NullLogger<AppCatalogStartupService>.Instance, new FixedClock(Now),
            new AppProductReconciliationService(db));

        await Assert.ThrowsAsync<AppCatalogException>(() => service.ApplyAsync(snapshot));
        db.ChangeTracker.Clear();

        Assert.Equal(
            "google-chrome",
            await db.AppIdentities.Where(x => x.Key == "win:chrome").Select(x => x.App.Key).SingleAsync());
        Assert.Empty(await db.AppCatalogStates.ToListAsync());
        Assert.Empty(await db.AppCatalogAudits.ToListAsync());
    }

    [Fact]
    public async Task Startup_ConcurrentReplicasSerializeAndWriteOneApplicationAudit()
    {
        string connectionString;
        await using (var seed = CreateDbContext())
            connectionString = seed.Database.GetDbConnection().ConnectionString;
        await using var gate = new NpgsqlConnection(connectionString);
        await gate.OpenAsync();
        await using var gateTransaction = await gate.BeginTransactionAsync();
        await using (var command = gate.CreateCommand())
        {
            command.Transaction = gateTransaction;
            command.CommandText =
                "SELECT pg_advisory_xact_lock(hashtextextended('heartbeat.app-catalog', 0))";
            await command.ExecuteNonQueryAsync();
        }

        var snapshot = Snapshot(1);
        await using var dbA = CreateDbContext();
        await using var dbB = CreateDbContext();
        var applyA = new AppCatalogStartupService(
            dbA, NullLogger<AppCatalogStartupService>.Instance, new FixedClock(Now),
            new AppProductReconciliationService(dbA)).ApplyAsync(snapshot);
        var applyB = new AppCatalogStartupService(
            dbB, NullLogger<AppCatalogStartupService>.Instance, new FixedClock(Now),
            new AppProductReconciliationService(dbB)).ApplyAsync(snapshot);

        var bothWaiting = false;
        await using (var observer = new NpgsqlConnection(connectionString))
        {
            await observer.OpenAsync();
            for (var attempt = 0; attempt < 100; attempt++)
            {
                await using var wait = observer.CreateCommand();
                wait.CommandText =
                    "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event = 'advisory'";
                bothWaiting = Convert.ToInt32(await wait.ExecuteScalarAsync()) >= 2;
                if (bothWaiting) break;
                await Task.Delay(20);
            }
        }

        await gateTransaction.CommitAsync();
        Assert.True(bothWaiting);
        var results = await Task.WhenAll(applyA, applyB);
        Assert.Contains(AppCatalogStartupResult.Applied, results);
        Assert.Contains(AppCatalogStartupResult.AlreadyApplied, results);
        await using var verify = CreateDbContext();
        Assert.Single(await verify.AppCatalogAudits.Where(x => x.EventType == "catalog-applied").ToListAsync());
    }

    [Fact]
    public async Task Override_CreateDeleteAndCatalogPromotion_PreserveIntentAndAudit()
    {
        var snapshot = Snapshot(1);
        var runtime = new AppCatalogRuntimeSnapshot(snapshot);
        runtime.Enable();
        await using var db = CreateDbContext();
        var chrome = new App { Key = "chrome", DisplayName = "Google Chrome" };
        var chromium = new App { Key = "chromium", DisplayName = "Chromium" };
        var mac = new AppIdentity { Key = "mac:com.google.chrome", App = chrome };
        db.AddRange(chrome, chromium, mac);
        await db.SaveChangesAsync();
        var service = new AppCatalogOverrideService(
            db, new AppProductReconciliationService(db), runtime, new FixedClock(Now));

        await service.SetAsync(mac.Key, chromium.Key, null, "admin-sub");
        db.ChangeTracker.Clear();
        var active = await db.AppCatalogOverrides.Include(x => x.TargetApp).SingleAsync();
        Assert.Equal(AppCatalogOverrideStatuses.Active, active.Status);
        Assert.Equal("chromium", active.TargetApp!.Key);
        Assert.Equal("chromium", active.TargetAppKey);
        Assert.Equal(chromium.Id, (await db.AppIdentities.SingleAsync()).AppId);

        await service.DeleteAsync(mac.Key, "admin-sub");
        db.ChangeTracker.Clear();
        Assert.Equal(AppCatalogOverrideStatuses.Deleted, (await db.AppCatalogOverrides.SingleAsync()).Status);
        Assert.Equal(chrome.Id, (await db.AppIdentities.SingleAsync()).AppId);
        Assert.Null((await db.AppCatalogOverrides.SingleAsync()).TargetAppId);
        Assert.Equal(2, await db.AppCatalogAudits.CountAsync());

        var promotedOverride = new AppCatalogOverride
        {
            AppIdentityId = mac.Id,
            TargetAppId = chrome.Id,
            TargetAppKey = chrome.Key,
            Status = AppCatalogOverrideStatuses.Active,
            CreatedBySubject = "admin-sub",
            UpdatedBySubject = "admin-sub",
            CreatedAt = Now,
            UpdatedAt = Now
        };
        db.AppCatalogOverrides.Add(promotedOverride);
        await db.SaveChangesAsync();
        var startup = new AppCatalogStartupService(
            db, NullLogger<AppCatalogStartupService>.Instance, new FixedClock(Now.AddMinutes(1)),
            new AppProductReconciliationService(db), runtime);

        await startup.ApplyAsync(snapshot);
        db.ChangeTracker.Clear();
        Assert.Equal(
            AppCatalogOverrideStatuses.Promoted,
            (await db.AppCatalogOverrides.SingleAsync(x => x.Id == promotedOverride.Id)).Status);
        Assert.Null((await db.AppCatalogOverrides.SingleAsync(x => x.Id == promotedOverride.Id)).TargetAppId);
        Assert.Contains(await db.AppCatalogAudits.ToListAsync(), x => x.EventType == "override-promoted");
    }

    [Fact]
    public async Task Override_UpdateAndDeleteWithoutCatalog_MigrateConsumersAndRetainHistory()
    {
        var runtime = new AppCatalogRuntimeSnapshot(EmptySnapshot(1));
        runtime.Enable();
        await using var db = CreateDbContext();
        var source = new App { Key = "tool", DisplayName = "Tool", IsProvisional = true };
        var firstTarget = new App { Key = "private-tool", DisplayName = "Private Tool" };
        var secondTarget = new App { Key = "work-tool", DisplayName = "Work Tool" };
        var identity = new AppIdentity { Key = "mac:com.example.tool", App = source };
        var device = new Device
        {
            OwnerId = "owner", HardwareId = "hw-tool", DeviceName = "Mac",
            CurrentApp = source.DisplayName, CurrentAppIdentity = identity
        };
        db.AddRange(source, firstTarget, secondTarget, identity, device);
        await db.SaveChangesAsync();
        var observed = new ActivitySegment
        {
            Id = Guid.CreateVersion7(), DeviceId = device.Id, Source = ActivitySources.System,
            IdentityKey = "tool-window", AppId = source.Id, AppIdentityId = identity.Id,
            StartTime = Now.AddMinutes(-3), EndTime = Now.AddMinutes(-2)
        };
        var legacy = new ActivitySegment
        {
            Id = Guid.CreateVersion7(), DeviceId = device.Id, Source = ActivitySources.System,
            IdentityKey = "legacy-tool", AppId = source.Id,
            StartTime = Now.AddMinutes(-2), EndTime = Now.AddMinutes(-1)
        };
        db.AddRange(observed, legacy, new AppIcon
        {
            OwnerId = "owner", App = source, IconData = [1, 2, 3], UpdatedAt = Now
        });
        await db.SaveChangesAsync();
        var sourceId = source.Id;
        var firstTargetId = firstTarget.Id;
        var secondTargetId = secondTarget.Id;
        var service = new AppCatalogOverrideService(
            db, new AppProductReconciliationService(db), runtime, new FixedClock(Now));

        await service.SetAsync(identity.Key, firstTarget.Key, null, "admin-one");
        db.ChangeTracker.Clear();
        Assert.False(await db.Apps.AnyAsync(x => x.Id == sourceId));
        Assert.All(await db.ActivitySegments.ToListAsync(), x => Assert.Equal(firstTargetId, x.AppId));
        Assert.Equal(firstTargetId, (await db.AppIcons.SingleAsync()).AppId);

        await service.SetAsync(identity.Key, secondTarget.Key, null, "admin-two");
        db.ChangeTracker.Clear();
        Assert.False(await db.Apps.AnyAsync(x => x.Id == firstTargetId));
        Assert.Equal(secondTargetId, (await db.AppIdentities.SingleAsync()).AppId);
        Assert.All(await db.ActivitySegments.ToListAsync(), x => Assert.Equal(secondTargetId, x.AppId));
        Assert.Equal(secondTargetId, (await db.AppIcons.SingleAsync()).AppId);
        var active = await db.AppCatalogOverrides.SingleAsync();
        Assert.Equal(secondTargetId, active.TargetAppId);
        Assert.Equal("work-tool", active.TargetAppKey);
        Assert.Equal("admin-one", active.CreatedBySubject);
        Assert.Equal("admin-two", active.UpdatedBySubject);

        var deletion = await service.DeleteAsync(identity.Key, "admin-three");
        db.ChangeTracker.Clear();
        Assert.False(await db.Apps.AnyAsync(x => x.Id == secondTargetId));
        var provisional = await db.Apps.Include(x => x.Identities).SingleAsync();
        Assert.True(provisional.IsProvisional);
        Assert.Equal(provisional.Id, deletion.Reconciliation.TargetAppId);
        Assert.Equal("tool", provisional.Key);
        Assert.Equal(identity.Key, Assert.Single(provisional.Identities).Key);
        Assert.All(await db.ActivitySegments.ToListAsync(), x => Assert.Equal(provisional.Id, x.AppId));
        Assert.Equal(provisional.Id, (await db.AppIcons.SingleAsync()).AppId);
        Assert.Equal("tool", (await db.Devices.SingleAsync()).CurrentApp);
        var deleted = await db.AppCatalogOverrides.SingleAsync();
        Assert.Equal(AppCatalogOverrideStatuses.Deleted, deleted.Status);
        Assert.Null(deleted.TargetAppId);
        Assert.Equal("work-tool", deleted.TargetAppKey);
        Assert.Equal(3, await db.AppCatalogAudits.CountAsync());
    }

    [Fact]
    public async Task Override_PreviewRollsBackEveryMutation()
    {
        var runtime = new AppCatalogRuntimeSnapshot(EmptySnapshot(1));
        runtime.Enable();
        await using var db = CreateDbContext();
        var source = new App { Key = "preview-source", DisplayName = "Preview", IsProvisional = true };
        var identity = new AppIdentity { Key = "win:preview", App = source };
        db.AddRange(source, identity);
        await db.SaveChangesAsync();
        var sourceId = source.Id;
        var service = new AppCatalogOverrideService(
            db, new AppProductReconciliationService(db), runtime, new FixedClock(Now));

        var preview = await service.PreviewAsync(
            identity.Key, "preview-target", "Preview Target", "admin-sub");

        Assert.Equal("preview-target", preview.TargetAppKey);
        Assert.Equal(sourceId, (await db.AppIdentities.SingleAsync()).AppId);
        Assert.False(await db.Apps.AnyAsync(x => x.Key == "preview-target"));
        Assert.Empty(await db.AppCatalogOverrides.ToListAsync());
        Assert.Empty(await db.AppCatalogAudits.ToListAsync());
    }

    [Fact]
    public async Task Override_ConcurrentSameTarget_SerializesToOneMutation()
    {
        await using (var seed = CreateDbContext())
        {
            var source = new App { Key = "concurrent-source", DisplayName = "Concurrent", IsProvisional = true };
            var target = new App { Key = "concurrent-target", DisplayName = "Target" };
            seed.AddRange(source, target, new AppIdentity { Key = "win:concurrent", App = source });
            await seed.SaveChangesAsync();
        }
        var runtime = new AppCatalogRuntimeSnapshot(EmptySnapshot(1));
        runtime.Enable();
        await using var dbA = CreateDbContext();
        await using var dbB = CreateDbContext();

        async Task<string> AttemptAsync(Heartbeat.Server.Data.AppDbContext db)
        {
            try
            {
                await new AppCatalogOverrideService(
                    db, new AppProductReconciliationService(db), runtime, new FixedClock(Now))
                    .SetAsync("win:concurrent", "concurrent-target", null, "admin-sub");
                return "committed";
            }
            catch (AppCatalogOverrideException exception)
            {
                return exception.Code;
            }
        }

        var results = await Task.WhenAll(AttemptAsync(dbA), AttemptAsync(dbB));

        Assert.Equal(["committed", "same_target"], results.Order(StringComparer.Ordinal).ToArray());
        await using var verify = CreateDbContext();
        Assert.Single(await verify.AppCatalogOverrides.ToListAsync());
        Assert.Single(await verify.AppCatalogAudits.ToListAsync());
    }

    [Fact]
    public async Task ResolveAsync_DoesNotMergeCatalogIdentityIntoUnrelatedProvisionalKeyCollision()
    {
        var snapshot = Snapshot(1);
        var runtime = new AppCatalogRuntimeSnapshot(snapshot);
        runtime.Enable();
        await using var db = CreateDbContext();
        var unrelated = new App { Key = "chrome", DisplayName = "Chrome-shaped Tool", IsProvisional = true };
        unrelated.Identities.Add(new AppIdentity { Key = "mac:com.example.chrome" });
        db.Apps.Add(unrelated);
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<AppCatalogException>(() =>
            new AppIdentityService(db, runtime).ResolveAsync("win:chrome", "Google Chrome"));

        Assert.Contains("unrelated provisional", exception.Message);
        Assert.Single(await db.AppIdentities.ToListAsync());
        Assert.Single(await db.Apps.ToListAsync());
    }

    [Fact]
    public async Task Startup_SameVersionRepairsActiveOverrideWithoutDuplicateCatalogAudit()
    {
        var snapshot = Snapshot(1);
        var runtime = new AppCatalogRuntimeSnapshot(snapshot);
        await using var db = CreateDbContext();
        var chrome = new App { Key = "chrome", DisplayName = "Google Chrome" };
        var overrideTarget = new App { Key = "private-browser", DisplayName = "Private Browser" };
        var identity = new AppIdentity { Key = "mac:com.google.chrome", App = chrome };
        db.AddRange(chrome, overrideTarget, identity, new AppCatalogState
        {
            Id = 1,
            SchemaVersion = snapshot.Document.SchemaVersion,
            CatalogVersion = snapshot.Document.CatalogVersion,
            ContentHash = snapshot.ContentHash,
            AppliedAt = Now,
            StartupMode = AppCatalogStartupModes.Normal
        });
        await db.SaveChangesAsync();
        db.AppCatalogOverrides.Add(new AppCatalogOverride
        {
            AppIdentityId = identity.Id,
            TargetAppId = overrideTarget.Id,
            TargetAppKey = overrideTarget.Key,
            Status = AppCatalogOverrideStatuses.Active,
            CreatedBySubject = "admin-sub",
            UpdatedBySubject = "admin-sub",
            CreatedAt = Now,
            UpdatedAt = Now
        });
        await db.SaveChangesAsync();

        var result = await new AppCatalogStartupService(
            db, NullLogger<AppCatalogStartupService>.Instance, new FixedClock(Now),
            new AppProductReconciliationService(db), runtime).ApplyAsync(snapshot);

        Assert.Equal(AppCatalogStartupResult.AlreadyApplied, result);
        Assert.Equal(overrideTarget.Id, (await db.AppIdentities.SingleAsync()).AppId);
        Assert.Empty(await db.AppCatalogAudits.ToListAsync());
        Assert.True(runtime.IsEnabled);
    }

    [Fact]
    public async Task Override_RollbackCompatibilityRejectsMutation()
    {
        var runtime = new AppCatalogRuntimeSnapshot(Snapshot(1));
        runtime.EnterRollbackCompatibility();
        await using var db = CreateDbContext();
        var service = new AppCatalogOverrideService(
            db, new AppProductReconciliationService(db), runtime, new FixedClock(Now));

        var exception = await Assert.ThrowsAsync<AppCatalogOverrideException>(() =>
            service.SetAsync("win:chrome", "chrome", null, "admin-sub"));

        Assert.Equal("catalog_rollback_compatibility", exception.Code);
        Assert.Empty(await db.AppCatalogOverrides.ToListAsync());
    }

    [Fact]
    public async Task Override_DeleteToMissingCatalogTargetMovesOnlySelectedIdentity()
    {
        var snapshot = Snapshot(1);
        var runtime = new AppCatalogRuntimeSnapshot(snapshot);
        runtime.Enable();
        await using var db = CreateDbContext();
        var shared = new App { Key = "shared-browser", DisplayName = "Shared Browser" };
        var chromeIdentity = new AppIdentity { Key = "mac:com.google.chrome", App = shared };
        var privateIdentity = new AppIdentity { Key = "mac:com.example.private", App = shared };
        db.AddRange(shared, chromeIdentity, privateIdentity);
        await db.SaveChangesAsync();
        db.AppCatalogOverrides.Add(new AppCatalogOverride
        {
            AppIdentityId = chromeIdentity.Id,
            TargetAppId = shared.Id,
            TargetAppKey = shared.Key,
            Status = AppCatalogOverrideStatuses.Active,
            CreatedBySubject = "admin-sub",
            UpdatedBySubject = "admin-sub",
            CreatedAt = Now,
            UpdatedAt = Now
        });
        await db.SaveChangesAsync();

        await new AppCatalogOverrideService(
            db, new AppProductReconciliationService(db), runtime, new FixedClock(Now))
            .DeleteAsync(chromeIdentity.Key, "admin-sub");
        db.ChangeTracker.Clear();

        var chrome = await db.Apps.Include(x => x.Identities).SingleAsync(x => x.Key == "chrome");
        Assert.Equal(chromeIdentity.Id, Assert.Single(chrome.Identities).Id);
        var remainingShared = await db.Apps.Include(x => x.Identities)
            .SingleAsync(x => x.Key == "shared-browser");
        Assert.Equal(privateIdentity.Id, Assert.Single(remainingShared.Identities).Id);
        Assert.Equal("Shared Browser", remainingShared.DisplayName);
    }

    [Fact]
    public async Task Startup_CanonicalRenameRefreshesActiveOverrideTargetKey()
    {
        var snapshot = Snapshot(1);
        await using var db = CreateDbContext();
        var product = new App { Key = "old-chrome", DisplayName = "Chrome.exe" };
        var catalogIdentity = new AppIdentity { Key = "win:chrome", App = product };
        var privateIdentity = new AppIdentity { Key = "mac:com.example.private", App = product };
        db.AddRange(product, catalogIdentity, privateIdentity);
        await db.SaveChangesAsync();
        db.AppCatalogOverrides.Add(new AppCatalogOverride
        {
            AppIdentityId = privateIdentity.Id,
            TargetAppId = product.Id,
            TargetAppKey = product.Key,
            Status = AppCatalogOverrideStatuses.Active,
            CreatedBySubject = "admin-sub",
            UpdatedBySubject = "admin-sub",
            CreatedAt = Now,
            UpdatedAt = Now
        });
        await db.SaveChangesAsync();

        await new AppCatalogStartupService(
            db, NullLogger<AppCatalogStartupService>.Instance, new FixedClock(Now),
            new AppProductReconciliationService(db)).ApplyAsync(snapshot);
        db.ChangeTracker.Clear();

        Assert.Equal("chrome", (await db.Apps.SingleAsync()).Key);
        Assert.Equal("chrome", (await db.AppCatalogOverrides.SingleAsync()).TargetAppKey);
    }

    [Fact]
    public async Task Startup_DoesNotRewriteAmbiguousDisplayAliasOwnedByAnotherProduct()
    {
        var snapshot = AppCatalogLoader.Parse(
            """{"schemaVersion":1,"catalogVersion":1,"products":[{"key":"target","displayName":"Target","identities":["win:source"]}]}""");
        await using var db = CreateDbContext();
        var source = new App { Key = "source", DisplayName = "Shared", IsProvisional = true };
        source.Identities.Add(new AppIdentity { Key = "win:source" });
        var unrelated = new App { Key = "unrelated", DisplayName = "Shared" };
        unrelated.Identities.Add(new AppIdentity { Key = "win:unrelated" });
        db.AddRange(source, unrelated, new MutedMatcher
        {
            Id = Guid.CreateVersion7(),
            OwnerId = "owner",
            Source = ActivitySources.System,
            StepsJson = AppMatcher("shared"),
            CreatedAt = Now
        });
        await db.SaveChangesAsync();

        await new AppCatalogStartupService(
            db, NullLogger<AppCatalogStartupService>.Instance, new FixedClock(Now),
            new AppProductReconciliationService(db)).ApplyAsync(snapshot);

        Assert.Equal(AppMatcher("shared"), (await db.MutedMatchers.SingleAsync()).StepsJson);
    }

    private static AppCatalogSnapshot Snapshot(int version) => AppCatalogLoader.Parse(
        $$"""{"schemaVersion":1,"catalogVersion":{{version}},"products":[{"key":"chrome","displayName":"Google Chrome","identities":["mac:com.google.chrome","win:chrome"]}]}""");

    private static AppCatalogSnapshot EmptySnapshot(int version) => AppCatalogLoader.Parse(
        $$"""{"schemaVersion":1,"catalogVersion":{{version}},"products":[]}""");

    private static string AppMatcher(string value) => MatcherCodec.Serialize(
        [new MatcherStepDto { Reading = "app", Op = MatcherOps.Equal, Value = value }]);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
