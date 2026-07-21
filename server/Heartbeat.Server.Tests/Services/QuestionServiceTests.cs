using System.Text.Json;
using Heartbeat.Core;
using Heartbeat.Core.DTOs.Knowledge;
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
    private long _appId;

    protected override async Task SeedAsync(AppDbContext db)
    {
        var device = new Device { OwnerId = "user-1", HardwareId = "hw-1", DeviceName = "Test PC" };
        var app = new App { Name = "sometool" };
        db.Devices.Add(device);
        db.Apps.Add(app);
        await db.SaveChangesAsync();
        _deviceId = device.Id;
        _appId = app.Id;
    }

    private sealed class FakeAsking : IAskingGenerator
    {
        public int Calls;
        public IReadOnlyList<QuestionItemResponse>? Result = [];
        public string? LastDigest;

        public Task<IReadOnlyList<QuestionItemResponse>?> AskAsync(
            string digest, AskingContext context, CancellationToken ct = default)
        {
            Calls++;
            LastDigest = digest;
            return Task.FromResult(Result);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>已结束的历史日窗口：命中即回路径。</summary>
    private static readonly DateTimeOffset PastDay = new(2026, 7, 10, 0, 0, 0, TimeSpan.Zero);

    private static MatcherDto AppMatcher(string app) => new()
    {
        Source = ActivitySources.System,
        Steps = [new() { Layer = 1, Reading = "app", Op = MatcherOps.Equal, Value = app }]
    };

    private static QuestionItemResponse Question(string app, string question = "这是什么？") => new()
    {
        Matcher = AppMatcher(app),
        Question = question,
        Evidence = "整段下午",
        ProposedName = "猜想",
        ProposedGloss = ""
    };

    private ActivitySegment Segment(DateTimeOffset start, DateTimeOffset end) => new()
    {
        Id = Guid.CreateVersion7(),
        DeviceId = _deviceId,
        Source = ActivitySources.System,
        IdentityKey = "sometool|",
        AppId = _appId,
        StartTime = start,
        EndTime = end
    };

    private QuestionService CreateService(AppDbContext db, FakeAsking fake, TimeProvider? clock = null)
        => new(db, new DigestAssembler(db), fake, clock);

    [Fact]
    public async Task PastDay_GeneratesOnce_SecondReadHitsCache()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(Segment(PastDay.AddHours(14), PastDay.AddHours(16)));
        await db.SaveChangesAsync();

        var fake = new FakeAsking { Result = [Question("sometool")] };
        var svc = CreateService(db, fake);

        var first = await svc.GetDailyQuestionsAsync("user-1", PastDay);
        var second = await svc.GetDailyQuestionsAsync("user-1", PastDay);

        Assert.Single(first.Questions);
        Assert.Single(second.Questions);
        Assert.Equal(1, fake.Calls); // 历史窗口命中即回，零重调
        Assert.NotNull(fake.LastDigest);
        Assert.Contains("sometool", fake.LastDigest);
    }

    [Fact]
    public async Task EmptyDay_NoLlmCall_NoCacheWrite()
    {
        using var db = CreateDbContext();
        var fake = new FakeAsking { Result = [Question("sometool")] };
        var svc = CreateService(db, fake);

        var result = await svc.GetDailyQuestionsAsync("user-1", PastDay);

        Assert.Empty(result.Questions);
        Assert.Equal(0, fake.Calls);
        Assert.Empty(await db.DailyQuestionSets.ToListAsync());
    }

    [Fact]
    public async Task JudgeFailure_NoCacheWrite_NextReadRetries()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(Segment(PastDay.AddHours(14), PastDay.AddHours(16)));
        await db.SaveChangesAsync();

        var fake = new FakeAsking { Result = null };
        var svc = CreateService(db, fake);

        var failed = await svc.GetDailyQuestionsAsync("user-1", PastDay);
        Assert.Empty(failed.Questions);
        Assert.Empty(await db.DailyQuestionSets.ToListAsync()); // 失败不写缓存

        fake.Result = [Question("sometool")];
        var retried = await svc.GetDailyQuestionsAsync("user-1", PastDay);
        Assert.Single(retried.Questions);
        Assert.Equal(2, fake.Calls); // 无毒缓存，下次读重试
    }

    [Fact]
    public async Task JudgeOutput_CappedAtThree()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(Segment(PastDay.AddHours(14), PastDay.AddHours(16)));
        await db.SaveChangesAsync();

        var fake = new FakeAsking
        {
            Result = [.. Enumerable.Range(0, 5).Select(i => Question($"tool-{i}"))]
        };
        var svc = CreateService(db, fake);

        var result = await svc.GetDailyQuestionsAsync("user-1", PastDay);

        Assert.Equal(3, result.Questions.Count);
        var cached = Assert.Single(await db.DailyQuestionSets.ToListAsync());
        Assert.Equal(3, JsonSerializer.Deserialize<List<QuestionItemResponse>>(cached.PayloadJson)!.Count);
    }

    [Fact]
    public async Task AdjudicatedMatchers_FilteredOnRead_ZeroRecall()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(Segment(PastDay.AddHours(14), PastDay.AddHours(16)));
        await db.SaveChangesAsync();

        var fake = new FakeAsking { Result = [Question("tool-a"), Question("tool-b")] };
        var svc = CreateService(db, fake);
        Assert.Equal(2, (await svc.GetDailyQuestionsAsync("user-1", PastDay)).Questions.Count);

        // 用户裁决：tool-a 绑进 Strand，tool-b 静音——两个出口都要把问题从队列里 diff 掉
        var knowledge = new KnowledgeService(db);
        await knowledge.BindStrandAsync("user-1", new BindStrandRequest
        {
            Name = "工具甲",
            Gloss = "",
            Members = [AppMatcher("tool-a")]
        });
        await knowledge.MuteMatcherAsync("user-1", AppMatcher("tool-b"));

        var after = await svc.GetDailyQuestionsAsync("user-1", PastDay);

        Assert.Empty(after.Questions);
        Assert.Equal(1, fake.Calls); // diff 是读时确定性过滤，零 LLM 重调
    }

    [Fact]
    public async Task Today_WatermarkLag_TriggersReask()
    {
        var day = new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);
        using var db = CreateDbContext();
        db.ActivitySegments.Add(Segment(day.AddHours(9), day.AddHours(10)));
        await db.SaveChangesAsync();

        var fake = new FakeAsking { Result = [Question("sometool")] };
        var svc = CreateService(db, fake, new FixedClock(day.AddHours(12)));

        await svc.GetDailyQuestionsAsync("user-1", day);
        Assert.Equal(1, fake.Calls);

        // 水位 10:00，新段推进到 11:30 → 落后 1.5h 过阈值，重新发问
        db.ActivitySegments.Add(Segment(day.AddHours(10), day.AddHours(11.5)));
        await db.SaveChangesAsync();

        await svc.GetDailyQuestionsAsync("user-1", day);
        Assert.Equal(2, fake.Calls);

        var cached = Assert.Single(await db.DailyQuestionSets.ToListAsync());
        Assert.Equal(day.AddHours(11.5).UtcDateTime, cached.SegmentWatermark);
    }

    [Fact]
    public async Task Owners_AreIsolated()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(Segment(PastDay.AddHours(14), PastDay.AddHours(16)));
        await db.SaveChangesAsync();

        var fake = new FakeAsking { Result = [Question("sometool")] };
        var svc = CreateService(db, fake);

        var mine = await svc.GetDailyQuestionsAsync("user-1", PastDay);
        var theirs = await svc.GetDailyQuestionsAsync("user-2", PastDay);

        Assert.Single(mine.Questions);
        Assert.Empty(theirs.Questions); // user-2 无段 → 空日，不问
    }
}
