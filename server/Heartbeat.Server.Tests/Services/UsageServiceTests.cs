using Heartbeat.Core.DTOs.Usage;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Heartbeat.Server.Services;
using Heartbeat.Server.Tests.Fixtures;

namespace Heartbeat.Server.Tests.Services;

[Collection("postgres")]
public class UsageServiceTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    private long _deviceId;

    protected override async Task SeedAsync(AppDbContext db)
    {
        var device = new Device
        {
            OwnerId = "user-1",
            HardwareId = "hw-1",
            DeviceName = "Test PC"
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        _deviceId = device.Id;
    }

    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    private static AppUsageItem Item(string app, DateTimeOffset start, DateTimeOffset end) => new()
    {
        AppName = app,
        StartTime = start,
        EndTime = end
    };

    [Fact]
    public async Task SaveUsage_ValidRecords_CreatesAppsAndUsages()
    {
        using var db = CreateDbContext();
        var svc = new UsageService(db);

        var start = Now.AddMinutes(-5);
        var end = Now.AddMinutes(-2);
        var request = new UsageUploadRequest
        {
            Usages = [Item("VSCode", start, end)]
        };

        await svc.SaveUsageAsync(_deviceId, request);

        Assert.Single(db.Apps);
        Assert.Equal("VSCode", db.Apps.First().Name);
        Assert.Single(db.AppUsages);
    }

    [Fact]
    public async Task SaveUsage_FiltersInvalidRecords()
    {
        using var db = CreateDbContext();
        var svc = new UsageService(db);

        var request = new UsageUploadRequest
        {
            Usages =
            [
                Item("", Now.AddMinutes(-5), Now.AddMinutes(-2)),         // empty name
                Item("App", default, Now),                                 // default start
                Item("App", Now.AddMinutes(-2), Now.AddMinutes(-5)),       // end < start
                Item("App", new DateTimeOffset(2019, 1, 1, 0, 0, 0, TimeSpan.Zero), Now), // year < 2020
                Item("App", Now.AddHours(-25), Now),                       // duration > 24h
                Item("App", Now.AddMinutes(20), Now.AddMinutes(30)),       // future beyond skew
            ]
        };

        await svc.SaveUsageAsync(_deviceId, request);

        Assert.Empty(db.AppUsages);
    }

    [Fact]
    public async Task SaveUsage_MergesWithExistingRecord_WhenOverlapping()
    {
        using var db = CreateDbContext();
        var svc = new UsageService(db);

        var app = new App { Name = "VSCode" };
        db.Apps.Add(app);
        await db.SaveChangesAsync();

        var existingStart = Now.AddMinutes(-10);
        var existingEnd = Now.AddMinutes(-5);
        db.AppUsages.Add(new AppUsage
        {
            DeviceId = _deviceId,
            AppId = app.Id,
            StartTime = existingStart,
            EndTime = existingEnd,
            DurationSeconds = (int)(existingEnd - existingStart).TotalSeconds
        });
        await db.SaveChangesAsync();

        // Upload overlapping record
        var newStart = Now.AddMinutes(-6);
        var newEnd = Now.AddMinutes(-3);
        var request = new UsageUploadRequest
        {
            Usages = [Item("VSCode", newStart, newEnd)]
        };

        await svc.SaveUsageAsync(_deviceId, request);

        var usages = db.AppUsages.ToList();
        Assert.Single(usages);
        Assert.Equal(existingStart, usages[0].StartTime);
        Assert.Equal(newEnd, usages[0].EndTime);
    }

    [Fact]
    public async Task SaveUsage_DoesNotMerge_WhenGapExceedsTolerance()
    {
        using var db = CreateDbContext();
        var svc = new UsageService(db);

        var app = new App { Name = "VSCode" };
        db.Apps.Add(app);
        await db.SaveChangesAsync();

        var existingEnd = Now.AddMinutes(-10);
        db.AppUsages.Add(new AppUsage
        {
            DeviceId = _deviceId,
            AppId = app.Id,
            StartTime = Now.AddMinutes(-15),
            EndTime = existingEnd,
            DurationSeconds = 300
        });
        await db.SaveChangesAsync();

        // New record starts 5 minutes after existing ends — no merge
        var request = new UsageUploadRequest
        {
            Usages = [Item("VSCode", Now.AddMinutes(-4), Now.AddMinutes(-2))]
        };

        await svc.SaveUsageAsync(_deviceId, request);

        Assert.Equal(2, db.AppUsages.Count());
    }

    [Fact]
    public async Task SaveUsage_CreatesApp_WhenNotExists()
    {
        using var db = CreateDbContext();
        var svc = new UsageService(db);

        var request = new UsageUploadRequest
        {
            Usages =
            [
                Item("NewApp1", Now.AddMinutes(-5), Now.AddMinutes(-3)),
                Item("NewApp2", Now.AddMinutes(-3), Now.AddMinutes(-1))
            ]
        };

        await svc.SaveUsageAsync(_deviceId, request);

        Assert.Equal(2, db.Apps.Count());
        Assert.Equal(2, db.AppUsages.Count());
    }

    [Fact]
    public async Task SaveUsage_ReusesExistingApp()
    {
        using var db = CreateDbContext();
        var svc = new UsageService(db);

        db.Apps.Add(new App { Name = "VSCode" });
        await db.SaveChangesAsync();

        var request = new UsageUploadRequest
        {
            Usages = [Item("VSCode", Now.AddMinutes(-5), Now.AddMinutes(-2))]
        };

        await svc.SaveUsageAsync(_deviceId, request);

        Assert.Single(db.Apps);
        Assert.Single(db.AppUsages);
    }

    [Fact]
    public async Task SaveUsage_CalculatesDurationSeconds()
    {
        using var db = CreateDbContext();
        var svc = new UsageService(db);

        var start = Now.AddMinutes(-5);
        var end = Now.AddMinutes(-2);
        var request = new UsageUploadRequest
        {
            Usages = [Item("VSCode", start, end)]
        };

        await svc.SaveUsageAsync(_deviceId, request);

        var usage = db.AppUsages.Single();
        Assert.Equal((int)(end - start).TotalSeconds, usage.DurationSeconds);
    }
}
