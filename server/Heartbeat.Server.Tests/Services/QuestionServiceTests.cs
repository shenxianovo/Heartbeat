using Heartbeat.Core;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Heartbeat.Server.Services;
using Heartbeat.Server.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;

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

        // 每个候选一个把手；两者都过 gate 进短名单（选题交给分诊，粗筛不判该不该问）。
        Assert.Contains(candidates, c => c.Handle == new HandleRef(ActivitySources.Browser, "proj.com"));
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

        // 首见：90min 过非复现 gate → 进候选。
        var c = Assert.Single(await svc.GetCandidatesAsync("user-1", Day));
        Assert.Equal(new HandleRef(ActivitySources.Browser, "chat.example.com"), c.Handle);

        // 补往日同把手 → 变复现（无处不在），90min 不再够 → 不进候选（ubiquity 惩罚）。
        db.ActivitySegments.Add(BrowserSeg("chat.example.com", At(-72), At(-71)));
        await db.SaveChangesAsync();
        Assert.Empty(await svc.GetCandidatesAsync("user-1", Day));
    }

    // ===== 分诊路径（LLM 三态）=====

    /// <summary>可编程分诊器：按 token 返回预置裁定，未列出的默认 Ask。记录调用次数验缓存。</summary>
    private sealed class FakeTriage(Dictionary<string, TriageResult> byToken) : ITriageGenerator
    {
        public int Calls { get; private set; }
        public Task<TriageResult> TriageAsync(TriageInput input, TriageAnchors anchors, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(byToken.TryGetValue(input.Token, out var r) ? r : TriageResult.FallbackAsk);
        }
    }

    [Fact]
    public async Task Triage_OnlyAskVerdicts_BecomeQuestions()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.AddRange(
            BrowserSeg("huasheng.cn", At(9), At(12)),      // ask
            BrowserSeg("bilibili.com", At(9), At(11.5)),   // known → 不问
            BrowserSeg("mystery.io", At(9), At(11)));      // silent → 不问
        await db.SaveChangesAsync();

        var fake = new FakeTriage(new()
        {
            ["huasheng.cn"] = new(TriageVerdict.Ask, "花生", "毕设"),
            ["bilibili.com"] = new(TriageVerdict.Known, "哔哩哔哩", "视频站"),
            ["mystery.io"] = new(TriageVerdict.Silent, "", ""),
        });
        var svc = new QuestionService(db, fake);

        var res = await svc.GetDailyQuestionsAsync("user-1", Day);

        var q = Assert.Single(res.Questions);
        Assert.Equal("huasheng.cn", q.Anchor!.Token);
        Assert.Equal("花生", q.ProposedName);
    }

    [Fact]
    public async Task Triage_DecisionsCached_NoRepeatLlmCalls()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(BrowserSeg("huasheng.cn", At(9), At(12)));
        await db.SaveChangesAsync();

        var fake = new FakeTriage(new() { ["huasheng.cn"] = new(TriageVerdict.Ask, "花生", "毕设") });

        await new QuestionService(db, fake).GetDailyQuestionsAsync("user-1", Day);
        Assert.Equal(1, fake.Calls);

        // 第二次：缓存命中，不再调 LLM。
        using var db2 = CreateDbContext();
        await new QuestionService(db2, fake).GetDailyQuestionsAsync("user-1", Day);
        Assert.Equal(1, fake.Calls); // 未增

        var cached = Assert.Single(await db2.TriageDecisions.ToListAsync());
        Assert.Equal("ask", cached.Verdict);
    }

    [Fact]
    public async Task Triage_NoGenerator_AllFallbackToAsk()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(BrowserSeg("huasheng.cn", At(9), At(12)));
        await db.SaveChangesAsync();

        // 无分诊器（LLM 未配置）：不假装认识，退化为 Ask，用户仍可命名。
        var res = await new QuestionService(db).GetDailyQuestionsAsync("user-1", Day);

        Assert.Single(res.Questions);
        // 缺席时不落缓存（等配置好再真判）。
        Assert.Empty(await db.TriageDecisions.ToListAsync());
    }
}
