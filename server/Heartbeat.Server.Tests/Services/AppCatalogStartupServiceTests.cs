using Heartbeat.Server.AppCatalog;
using Heartbeat.Server.Entities;
using Heartbeat.Server.Services;
using Heartbeat.Server.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Heartbeat.Server.Tests.Services;

[Collection("postgres")]
public class AppCatalogStartupServiceTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task ApplyAsync_FirstApplicationIsTransactionalAndIdempotent()
    {
        var snapshot = EmptySnapshot(version: 1);
        await using var db = CreateDbContext();
        db.Apps.Add(new App { Key = "existing", DisplayName = "Existing", IsProvisional = true });
        await db.SaveChangesAsync();
        var service = CreateService(db);

        Assert.Equal(AppCatalogStartupResult.Applied, await service.ApplyAsync(snapshot));
        Assert.Equal(AppCatalogStartupResult.AlreadyApplied, await service.ApplyAsync(snapshot));

        var state = await db.AppCatalogStates.SingleAsync();
        Assert.Equal(1, state.Id);
        Assert.Equal(1, state.SchemaVersion);
        Assert.Equal(1, state.CatalogVersion);
        Assert.Equal(snapshot.ContentHash, state.ContentHash);
        Assert.Equal(Now, state.AppliedAt);
        Assert.Equal(AppCatalogStartupModes.Normal, state.StartupMode);
        Assert.Single(await db.AppCatalogAudits.ToListAsync());
        Assert.Single(await db.Apps.ToListAsync());
        Assert.Empty(await db.AppIdentities.ToListAsync());
    }

    [Fact]
    public async Task ApplyAsync_SameVersionDifferentHashFailsWithoutMutation()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);
        await service.ApplyAsync(EmptySnapshot(version: 1));
        var changed = AppCatalogLoader.Parse(
            """{"schemaVersion":1,"catalogVersion":1,"products":[{"key":"alpha","displayName":"Alpha","identities":["win:alpha"]}]}""");

        var exception = await Assert.ThrowsAsync<AppCatalogException>(() => service.ApplyAsync(changed));

        Assert.Contains("content drift", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, (await db.AppCatalogStates.SingleAsync()).CatalogVersion);
        Assert.Single(await db.AppCatalogAudits.ToListAsync());
    }

    [Fact]
    public async Task ApplyAsync_OlderBinaryEntersRollbackCompatibilityWithoutDowngrade()
    {
        await using var db = CreateDbContext();
        db.AppCatalogStates.Add(new AppCatalogState
        {
            Id = 1,
            SchemaVersion = 1,
            CatalogVersion = 3,
            ContentHash = new string('a', 64),
            AppliedAt = Now.AddDays(-1),
            StartupMode = AppCatalogStartupModes.Normal
        });
        await db.SaveChangesAsync();

        var result = await CreateService(db).ApplyAsync(EmptySnapshot(version: 2));

        Assert.Equal(AppCatalogStartupResult.RollbackCompatible, result);
        var state = await db.AppCatalogStates.SingleAsync();
        Assert.Equal(3, state.CatalogVersion);
        Assert.Equal(new string('a', 64), state.ContentHash);
        Assert.Equal(Now.AddDays(-1), state.AppliedAt);
        Assert.Equal(AppCatalogStartupModes.RollbackCompatible, state.StartupMode);
        Assert.Empty(await db.AppCatalogAudits.ToListAsync());
    }

    [Fact]
    public async Task ApplyAsync_HigherCatalogVersionAdvancesStateAndAudit()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);
        await service.ApplyAsync(EmptySnapshot(version: 1));

        Assert.Equal(AppCatalogStartupResult.Applied, await service.ApplyAsync(EmptySnapshot(version: 2)));

        Assert.Equal(2, (await db.AppCatalogStates.SingleAsync()).CatalogVersion);
        Assert.Equal(2, await db.AppCatalogAudits.CountAsync());
    }

    private AppCatalogStartupService CreateService(Heartbeat.Server.Data.AppDbContext db) => new(
        db,
        NullLogger<AppCatalogStartupService>.Instance,
        new FixedClock(Now));

    private static AppCatalogSnapshot EmptySnapshot(int version) => AppCatalogLoader.Parse(
        $$"""{"schemaVersion":1,"catalogVersion":{{version}},"products":[]}""");

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
