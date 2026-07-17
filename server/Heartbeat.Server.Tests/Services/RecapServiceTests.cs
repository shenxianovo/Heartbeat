using Heartbeat.Core;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Heartbeat.Server.Services;
using Heartbeat.Server.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Tests.Services;

[Collection("postgres")]
public class RecapServiceTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    private long _deviceId;
    private long _appId;

    protected override async Task SeedAsync(AppDbContext db)
    {
        var device = new Device { OwnerId = "user-1", HardwareId = "hw-1", DeviceName = "Test PC" };
        var app = new App { Name = "vscode" };
        db.Devices.Add(device);
        db.Apps.Add(app);
        await db.SaveChangesAsync();
        _deviceId = device.Id;
        _appId = app.Id;
    }

    private sealed class FakeGenerator : IRecapGenerator
    {
        public int Calls;
        public bool Fail;

        public string Model => "fake-model";
        public string PromptHash => "deadbeef";

        public Task<string> GenerateAsync(string digest, CancellationToken ct = default)
        {
            Calls++;
            if (Fail) throw new RecapGenerationException("upstream down");
            return Task.FromResult($"narrative-{Calls}");
        }
    }

    private ActivitySegment SystemSegment(DateTimeOffset start, DateTimeOffset end) => new()
    {
        Id = Guid.CreateVersion7(),
        DeviceId = _deviceId,
        Source = ActivitySources.System,
        IdentityKey = "vscode|",
        AppId = _appId,
        StartTime = start,
        EndTime = end
    };

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>已结束的历史日窗口（UTC 昨天再往前一天，避开"今天"的水位路径）。</summary>
    private static readonly DateTimeOffset PastDay =
        new(DateTimeOffset.UtcNow.Date.AddDays(-2), TimeSpan.Zero);

    /// <summary>"今天"场景的固定时钟：窗口 2026-07-08（UTC），now 定在正午——窗口未结束，走水位路径。</summary>
    private static readonly DateTimeOffset FixedDay = new(2026, 7, 8, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FixedNoon = FixedDay.AddHours(12);

    [Fact]
    public async Task EmptyDay_NoLlmCall_NothingCached()
    {
        using var db = CreateDbContext();
        var fake = new FakeGenerator();
        var svc = new RecapService(db, fake);

        var result = await svc.GetDailyRecapAsync("user-1", PastDay, force: false);

        Assert.True(result.IsEmpty);
        Assert.Null(result.Narrative);
        Assert.Equal(0, fake.Calls);
        Assert.Empty(await db.Recaps.ToListAsync());
    }

    [Fact]
    public async Task PastDay_FirstRequestGenerates_SecondServedFromCache()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(SystemSegment(PastDay.AddHours(9), PastDay.AddHours(11)));
        await db.SaveChangesAsync();

        var fake = new FakeGenerator();
        var svc = new RecapService(db, fake);

        var first = await svc.GetDailyRecapAsync("user-1", PastDay, force: false);
        var second = await svc.GetDailyRecapAsync("user-1", PastDay, force: false);

        Assert.Equal("narrative-1", first.Narrative);
        Assert.Equal("narrative-1", second.Narrative);
        Assert.Equal(1, fake.Calls);

        var row = Assert.Single(await db.Recaps.ToListAsync());
        Assert.Equal("fake-model", row.Model);
        Assert.Equal("deadbeef", row.PromptHash);
        Assert.Equal(PastDay.AddHours(11), row.SegmentWatermark);
    }

    [Fact]
    public async Task Force_RegeneratesAndOverwritesCache()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(SystemSegment(PastDay.AddHours(9), PastDay.AddHours(11)));
        await db.SaveChangesAsync();

        var fake = new FakeGenerator();
        var svc = new RecapService(db, fake);

        await svc.GetDailyRecapAsync("user-1", PastDay, force: false);
        var regenerated = await svc.GetDailyRecapAsync("user-1", PastDay, force: true);

        Assert.Equal("narrative-2", regenerated.Narrative);
        Assert.Equal(2, fake.Calls);
        Assert.Single(await db.Recaps.ToListAsync()); // upsert，不是第二行
    }

    [Fact]
    public async Task Today_FreshWatermark_ServedFromCache()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(SystemSegment(FixedDay.AddHours(8), FixedDay.AddHours(11)));
        await db.SaveChangesAsync();

        var fake = new FakeGenerator();
        var svc = new RecapService(db, fake, new FixedClock(FixedNoon));

        await svc.GetDailyRecapAsync("user-1", FixedNoon, force: false);
        await svc.GetDailyRecapAsync("user-1", FixedNoon, force: false);

        Assert.Equal(1, fake.Calls); // 无新数据，水位未落后
    }

    [Fact]
    public async Task Today_StaleWatermark_Regenerates()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(SystemSegment(FixedDay.AddHours(8), FixedDay.AddHours(9)));
        await db.SaveChangesAsync();

        var fake = new FakeGenerator();
        var svc = new RecapService(db, fake, new FixedClock(FixedNoon));
        await svc.GetDailyRecapAsync("user-1", FixedNoon, force: false); // 水位 = 09:00

        // 新段到达：最新段尾 11:30 距缓存水位 09:00 有 2.5h > 1h 阈值
        db.ActivitySegments.Add(SystemSegment(FixedDay.AddHours(10.5), FixedDay.AddHours(11.5)));
        await db.SaveChangesAsync();

        var result = await svc.GetDailyRecapAsync("user-1", FixedNoon, force: false);

        Assert.Equal("narrative-2", result.Narrative);
        Assert.Equal(2, fake.Calls);
    }

    [Fact]
    public async Task CachedOnly_MissingRecap_ReturnsNullWithoutGeneration()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(SystemSegment(PastDay.AddHours(9), PastDay.AddHours(11)));
        await db.SaveChangesAsync();

        var fake = new FakeGenerator();
        var svc = new RecapService(db, fake);

        var result = await svc.GetCachedDailyRecapAsync("user-1", PastDay);

        Assert.Null(result);
        Assert.Equal(0, fake.Calls);
    }

    [Fact]
    public async Task CachedOnly_ExistingRecap_ReturnsItWithoutRegeneration()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(SystemSegment(PastDay.AddHours(9), PastDay.AddHours(11)));
        await db.SaveChangesAsync();

        var fake = new FakeGenerator();
        var svc = new RecapService(db, fake);
        await svc.GetDailyRecapAsync("user-1", PastDay, force: false);

        var result = await svc.GetCachedDailyRecapAsync("user-1", PastDay);

        Assert.NotNull(result);
        Assert.Equal("narrative-1", result.Narrative);
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public async Task GeneratorFailure_NothingCached()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(SystemSegment(PastDay.AddHours(9), PastDay.AddHours(11)));
        await db.SaveChangesAsync();

        var fake = new FakeGenerator { Fail = true };
        var svc = new RecapService(db, fake);

        await Assert.ThrowsAsync<RecapGenerationException>(
            () => svc.GetDailyRecapAsync("user-1", PastDay, force: false));
        Assert.Empty(await db.Recaps.ToListAsync());
    }

    [Fact]
    public async Task OtherOwnersSegments_NotVisible()
    {
        using var db = CreateDbContext();
        var otherDevice = new Device { OwnerId = "user-2", HardwareId = "hw-2", DeviceName = "Other PC" };
        db.Devices.Add(otherDevice);
        await db.SaveChangesAsync();
        db.ActivitySegments.Add(new ActivitySegment
        {
            Id = Guid.CreateVersion7(),
            DeviceId = otherDevice.Id,
            Source = ActivitySources.System,
            IdentityKey = "vscode|",
            AppId = _appId,
            StartTime = PastDay.AddHours(9),
            EndTime = PastDay.AddHours(11)
        });
        await db.SaveChangesAsync();

        var fake = new FakeGenerator();
        var svc = new RecapService(db, fake);

        var result = await svc.GetDailyRecapAsync("user-1", PastDay, force: false);

        Assert.True(result.IsEmpty); // user-2 的数据对 user-1 不可见
        Assert.Equal(0, fake.Calls);
    }
}
