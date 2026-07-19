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
    private long _explorerAppId;

    private static readonly DateTimeOffset Day = new(2026, 7, 8, 0, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(double hour) => Day.AddHours(hour);

    protected override async Task SeedAsync(AppDbContext db)
    {
        var device = new Device { OwnerId = "user-1", HardwareId = "hw-1", DeviceName = "PC" };
        var code = new App { Name = "code.exe" };
        var explorer = new App { Name = "explorer" };
        db.Devices.Add(device);
        db.Apps.AddRange(code, explorer);
        await db.SaveChangesAsync();
        _deviceId = device.Id;
        _codeAppId = code.Id;
        _explorerAppId = explorer.Id;
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
    public async Task DerivesHandles_SingleAnchorPerCandidate()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.AddRange(
            SystemSeg("code.exe", _codeAppId, At(9), At(11)),
            BrowserSeg("proj.com", At(9.2), At(10.8)));
        await db.SaveChangesAsync();

        var candidates = await new QuestionService(db).GetCandidatesAsync("user-1", Day);

        // 单锚点：每个候选一个锚点把手，域名比裸系统进程更该问（特异性先验）。
        Assert.Contains(candidates, c => c.Anchor == new HandleRef(ActivitySources.Browser, "proj.com"));
        Assert.Equal(new HandleRef(ActivitySources.Browser, "proj.com"), candidates[0].Anchor);
    }

    [Fact]
    public async Task ShellApp_NeverAsked_EvenAllDay()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(SystemSeg("explorer", _explorerAppId, At(6), At(22)));
        await db.SaveChangesAsync();

        Assert.Empty(await new QuestionService(db).GetCandidatesAsync("user-1", Day));
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
    public async Task RecurringHandle_HasHigherGate_UbiquityPenalty()
    {
        using var db = CreateDbContext();
        // 今天 90 分钟。
        db.ActivitySegments.Add(BrowserSeg("chat.example.com", At(9), At(10.5)));
        await db.SaveChangesAsync();
        var svc = new QuestionService(db);

        // 首见：90min 过非复现 gate → 问。
        var q = Assert.Single(await svc.GetCandidatesAsync("user-1", Day));
        Assert.Equal(new HandleRef(ActivitySources.Browser, "chat.example.com"), q.Anchor);

        // 补往日同把手 → 变复现（无处不在），90min 不再够 → 不问（ubiquity 惩罚）。
        db.ActivitySegments.Add(BrowserSeg("chat.example.com", At(-72), At(-71)));
        await db.SaveChangesAsync();
        Assert.Empty(await svc.GetCandidatesAsync("user-1", Day));
    }
}
