using Heartbeat.Core;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Heartbeat.Server.Services;
using Heartbeat.Server.Tests.Fixtures;

namespace Heartbeat.Server.Tests.Services;

[Collection("postgres")]
public class ReportServiceTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    private long _deviceId;
    private long _appId;

    protected override async Task SeedAsync(AppDbContext db)
    {
        var device = new Device { OwnerId = "user-1", HardwareId = "hw-1", DeviceName = "Test PC" };
        var app = new App { Name = "VSCode" };
        db.Devices.Add(device);
        db.Apps.Add(app);
        await db.SaveChangesAsync();
        _deviceId = device.Id;
        _appId = app.Id;
    }

    private ActivitySegment SystemSegment(DateTimeOffset start, DateTimeOffset end) => new()
    {
        Id = Guid.CreateVersion7(),
        DeviceId = _deviceId,
        Source = ActivitySources.System,
        IdentityKey = SystemIdentity.Key("VSCode", null),
        AppId = _appId,
        StartTime = start,
        EndTime = end
    };

    [Fact]
    public async Task DailyReport_MidnightCrossingSegment_ClippedPerDay_NoDoubleCount()
    {
        using var db = CreateDbContext();
        var svc = new ReportService(db);

        // 跨午夜段（ADR-018 后长段不再被 flush 截断，跨窗成为常态，如整夜 away）
        var day1 = new DateTimeOffset(2025, 6, 15, 0, 0, 0, TimeSpan.Zero);
        var day2 = day1.AddDays(1);
        db.ActivitySegments.Add(SystemSegment(day1.AddHours(23), day2.AddHours(1))); // 23:00–次日 01:00
        db.ActivitySegments.Add(SystemSegment(day2.AddHours(10), day2.AddHours(11)));
        await db.SaveChangesAsync();

        var report1 = await svc.GetDailyReportAsync("user-1", null, day1);
        var item1 = Assert.Single(report1.Apps);
        Assert.Equal(3600, item1.DurationSeconds); // 只计 23:00–24:00

        var report2 = await svc.GetDailyReportAsync("user-1", null, day2);
        var item2 = Assert.Single(report2.Apps);
        Assert.Equal(3600 + 3600, item2.DurationSeconds); // 00:00–01:00 + 10:00–11:00
    }

    [Fact]
    public async Task DailyReport_SegmentOutsideWindow_NotCounted()
    {
        using var db = CreateDbContext();
        var svc = new ReportService(db);

        var day = new DateTimeOffset(2025, 6, 15, 0, 0, 0, TimeSpan.Zero);
        // 恰好在窗口边界结束/开始的段：半开窗口 [day, day+1)，零重叠不计
        db.ActivitySegments.Add(SystemSegment(day.AddHours(-2), day));
        db.ActivitySegments.Add(SystemSegment(day.AddDays(1), day.AddDays(1).AddHours(2)));
        await db.SaveChangesAsync();

        var report = await svc.GetDailyReportAsync("user-1", null, day);
        Assert.Empty(report.Apps);
    }
}
