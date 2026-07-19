using Heartbeat.Core;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Heartbeat.Server.Services;
using Heartbeat.Server.Tests.Fixtures;

namespace Heartbeat.Server.Tests.Services;

[Collection("postgres")]
public class QuestionServiceTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    private long _deviceId;
    private long _codeAppId;

    private static readonly DateTimeOffset Day = new(2026, 7, 8, 0, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(double hour) => Day.AddHours(hour);

    protected override async Task SeedAsync(AppDbContext db)
    {
        var device = new Device { OwnerId = "user-1", HardwareId = "hw-1", DeviceName = "PC" };
        var code = new App { Name = "code.exe" };
        db.Devices.Add(device);
        db.Apps.Add(code);
        await db.SaveChangesAsync();
        _deviceId = device.Id;
        _codeAppId = code.Id;
    }

    private ActivitySegment SystemSeg(string appName, long appId, DateTimeOffset start, DateTimeOffset end) => new()
    {
        Id = Guid.CreateVersion7(),
        DeviceId = _deviceId,
        Source = ActivitySources.System,
        IdentityKey = SystemIdentity.Key(appName, null),
        AppId = appId,
        StartTime = start,
        EndTime = end
    };

    private ActivitySegment BrowserSeg(string domain, DateTimeOffset start, DateTimeOffset end) => new()
    {
        Id = Guid.CreateVersion7(),
        DeviceId = _deviceId,
        Source = ActivitySources.Browser,
        IdentityKey = $"https://{domain}/x",
        Attributes = $$"""{"domain":"{{domain}}","url":"https://{{domain}}/x"}""",
        StartTime = start,
        EndTime = end
    };

    [Fact]
    public async Task DerivesHandlesFromSegments_ClustersCoOccurring()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.AddRange(
            SystemSeg("code.exe", _codeAppId, At(9), At(11)),
            BrowserSeg("proj.com", At(9.2), At(10.8)));
        await db.SaveChangesAsync();

        var clusters = await new QuestionService(db).GetCandidatesAsync("user-1", Day);

        var cluster = Assert.Single(clusters);
        Assert.Contains(new HandleRef(ActivitySources.System, "code.exe"), cluster.Handles);
        Assert.Contains(new HandleRef(ActivitySources.Browser, "proj.com"), cluster.Handles);
    }

    [Fact]
    public async Task MutedHandle_ExcludedFromCandidates()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(BrowserSeg("news.com", At(9), At(12)));
        db.MutedHandles.Add(new MutedHandle
        {
            OwnerId = "user-1",
            Source = ActivitySources.Browser,
            Token = "news.com",
            CreatedAt = At(0)
        });
        await db.SaveChangesAsync();

        Assert.Empty(await new QuestionService(db).GetCandidatesAsync("user-1", Day));
    }

    [Fact]
    public async Task OtherOwnersSegments_NotVisible()
    {
        using var db = CreateDbContext();
        var other = new Device { OwnerId = "user-2", HardwareId = "hw-2", DeviceName = "Other" };
        db.Devices.Add(other);
        await db.SaveChangesAsync();
        db.ActivitySegments.Add(new ActivitySegment
        {
            Id = Guid.CreateVersion7(),
            DeviceId = other.Id,
            Source = ActivitySources.Browser,
            IdentityKey = "https://proj.com/x",
            Attributes = """{"domain":"proj.com"}""",
            StartTime = At(9),
            EndTime = At(12)
        });
        await db.SaveChangesAsync();

        Assert.Empty(await new QuestionService(db).GetCandidatesAsync("user-1", Day));
    }

    [Fact]
    public async Task ShortToday_BecomesCandidate_OnlyWhenSeenOnPriorDay()
    {
        using var db = CreateDbContext();
        // 今天只有 5 分钟，低于有意义时长。
        db.ActivitySegments.Add(BrowserSeg("ritual.com", At(9), At(9 + 5.0 / 60)));
        await db.SaveChangesAsync();
        var svc = new QuestionService(db);

        Assert.Empty(await svc.GetCandidatesAsync("user-1", Day)); // 非复现 → 不问

        // 补一条往日（3 天前，落在回看窗内）同把手的段。
        db.ActivitySegments.Add(BrowserSeg("ritual.com", At(-72), At(-71)));
        await db.SaveChangesAsync();

        var cluster = Assert.Single(await svc.GetCandidatesAsync("user-1", Day)); // 复现 → 放行
        Assert.Equal(new HandleRef(ActivitySources.Browser, "ritual.com"), cluster.Anchor);
    }
}
