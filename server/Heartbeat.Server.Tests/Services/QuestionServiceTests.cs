using System.Text.Json;
using Heartbeat.Core;
using Heartbeat.Core.DTOs.Knowledge;
using Heartbeat.Server.Calendar;
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
        public IReadOnlyList<AskingCandidate>? Result = [];
        public string? LastDigest;

        public Task<IReadOnlyList<AskingCandidate>?> AskAsync(
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
        Steps = [new() { Reading = "app", Op = MatcherOps.Equal, Value = app }]
    };

    private static AskingCandidate Candidate(string app, string question = "这是什么？")
        => new(question, AppMatcher(app));

    private async Task<App> EnsureAppAsync(AppDbContext db, string name)
    {
        var app = await db.Apps.FirstOrDefaultAsync(a => a.DisplayName == name);
        if (app == null)
        {
            app = new App { Name = name };
            db.Apps.Add(app);
            await db.SaveChangesAsync();
        }
        return app;
    }

    private ActivitySegment Segment(DateTimeOffset start, DateTimeOffset end, long? appId = null, string identity = "sometool|") => new()
    {
        Id = Guid.CreateVersion7(),
        DeviceId = _deviceId,
        Source = ActivitySources.System,
        IdentityKey = identity,
        AppId = appId ?? _appId,
        StartTime = start,
        EndTime = end
    };

    private QuestionService CreateService(AppDbContext db, FakeAsking fake, TimeProvider? clock = null)
        => new(db, new DigestAssembler(db), fake, clock);

    private static ResolvedCalendarWindow ResolveDay(
        string timeZone, string start, string endExclusive)
    {
        var result = LocalCalendarWindowValidator.ResolveDay(new LocalCalendarWindowEnvelope
        {
            Version = 1,
            Kind = "day",
            LocalDate = "2026-03-08",
            TimeZone = timeZone,
            Start = DateTimeOffset.Parse(start),
            EndExclusive = DateTimeOffset.Parse(endExclusive),
        });
        return result.Window!;
    }

    private static ResolvedCalendarWindow UtcDay(DateTimeOffset date) =>
        LocalCalendarWindowValidator.ResolveDay(LocalCalendarWindowTestData.UtcDay(date)).Window!;

    [Fact]
    public async Task SameLocalDateInDifferentTimeZonesUsesDistinctQuestionCaches()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(Segment(
            DateTimeOffset.Parse("2026-03-08T05:00:00Z"),
            DateTimeOffset.Parse("2026-03-08T06:00:00Z")));
        await db.SaveChangesAsync();

        var fake = new FakeAsking { Result = [Candidate("sometool")] };
        var svc = CreateService(db, fake);
        var newYork = ResolveDay(
            "America/New_York", "2026-03-08T05:00:00Z", "2026-03-09T04:00:00Z");
        var utc = ResolveDay(
            "UTC", "2026-03-08T00:00:00Z", "2026-03-09T00:00:00Z");

        var easternQuestions = await svc.GetDailyQuestionsAsync("user-1", newYork);
        var utcQuestions = await svc.GetDailyQuestionsAsync("user-1", utc);

        Assert.Equal(2, fake.Calls);
        var caches = await db.DailyQuestionSets.OrderBy(q => q.TimeZone).ToListAsync();
        Assert.Equal(2, caches.Count);
        Assert.All(caches, cache =>
        {
            Assert.Equal(1, cache.WindowVersion);
            Assert.Equal("day", cache.WindowKind);
            Assert.Equal("2026-03-08", cache.LocalDate);
            Assert.NotNull(cache.WindowEndExclusive);
        });
        Assert.Equal(newYork.WindowKey.Value, Assert.Single(easternQuestions.Questions).WindowKey);
        Assert.Equal(utc.WindowKey.Value, Assert.Single(utcQuestions.Questions).WindowKey);
        Assert.NotEqual(easternQuestions.Questions[0].WindowKey, utcQuestions.Questions[0].WindowKey);
    }

    [Fact]
    public async Task PastDay_GeneratesOnce_SecondReadHitsCache()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(Segment(PastDay.AddHours(14), PastDay.AddHours(16)));
        await db.SaveChangesAsync();

        var fake = new FakeAsking { Result = [Candidate("sometool")] };
        var svc = CreateService(db, fake);

        var first = await svc.GetDailyQuestionsAsync("user-1", UtcDay(PastDay));
        var second = await svc.GetDailyQuestionsAsync("user-1", UtcDay(PastDay));

        Assert.Single(first.Questions);
        Assert.Single(second.Questions);
        Assert.Equal(1, fake.Calls); // 历史窗口命中即回，零重调
        Assert.NotNull(fake.LastDigest);
        Assert.Contains("sometool", fake.LastDigest);
        // 缓存的问题身份稳定：第二阶段凭它取证
        Assert.Equal(first.Questions[0].Id, second.Questions[0].Id);
    }

    [Fact]
    public async Task Question_CarriesMaterializedEvidence_FromRealSegments()
    {
        using var db = CreateDbContext();
        var other = await EnsureAppAsync(db, "chrome");
        // 命中段 14:00–16:00；同时段并行的 chrome 是旁证；时段外的 chrome 不进证据卡
        db.ActivitySegments.Add(Segment(PastDay.AddHours(14), PastDay.AddHours(16)));
        db.ActivitySegments.Add(Segment(PastDay.AddHours(14), PastDay.AddHours(15), other.Id, "chrome|"));
        db.ActivitySegments.Add(Segment(PastDay.AddHours(20), PastDay.AddHours(21), other.Id, "chrome|"));
        await db.SaveChangesAsync();

        var fake = new FakeAsking { Result = [Candidate("sometool")] };
        var svc = CreateService(db, fake);

        var result = await svc.GetDailyQuestionsAsync("user-1", UtcDay(PastDay));

        var q = Assert.Single(result.Questions);
        Assert.Equal(AskingQuestionKinds.Cluster, q.Kind);
        Assert.Equal(PastDay.AddHours(14), q.ApproximateStart);
        Assert.Equal(PastDay.AddHours(16), q.ApproximateEnd);

        var tool = Assert.Single(q.Observations, o => o.Value == "sometool");
        Assert.True(tool.MatchesFingerprint);
        Assert.Equal(7200, tool.Seconds, 1.0);
        var chrome = Assert.Single(q.Observations, o => o.Value == "chrome");
        Assert.False(chrome.MatchesFingerprint);
        Assert.Equal(3600, chrome.Seconds, 1.0); // 20:00 的段被时段裁掉
    }

    [Fact]
    public async Task Candidate_WithNoMatchingEvidence_Dropped()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(Segment(PastDay.AddHours(14), PastDay.AddHours(16)));
        await db.SaveChangesAsync();

        // 判官编造了当日不存在的活动：物化零命中，问题整个丢弃
        var fake = new FakeAsking { Result = [Candidate("ghost-tool"), Candidate("sometool")] };
        var svc = CreateService(db, fake);

        var result = await svc.GetDailyQuestionsAsync("user-1", UtcDay(PastDay));

        var q = Assert.Single(result.Questions);
        Assert.Equal("sometool", q.Matcher.Steps[0].Value);
    }

    [Fact]
    public async Task EmptyDay_NoLlmCall_NoCacheWrite()
    {
        using var db = CreateDbContext();
        var fake = new FakeAsking { Result = [Candidate("sometool")] };
        var svc = CreateService(db, fake);

        var result = await svc.GetDailyQuestionsAsync("user-1", UtcDay(PastDay));

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

        var failed = await svc.GetDailyQuestionsAsync("user-1", UtcDay(PastDay));
        Assert.Empty(failed.Questions);
        Assert.Empty(await db.DailyQuestionSets.ToListAsync()); // 失败不写缓存

        fake.Result = [Candidate("sometool")];
        var retried = await svc.GetDailyQuestionsAsync("user-1", UtcDay(PastDay));
        Assert.Single(retried.Questions);
        Assert.Equal(2, fake.Calls); // 无毒缓存，下次读重试
    }

    [Fact]
    public async Task JudgeOutput_CappedAtThree()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(Segment(PastDay.AddHours(14), PastDay.AddHours(16)));
        foreach (var i in Enumerable.Range(0, 5))
        {
            var app = await EnsureAppAsync(db, $"tool-{i}");
            db.ActivitySegments.Add(Segment(PastDay.AddHours(10 + i), PastDay.AddHours(10.5 + i), app.Id, $"tool-{i}|"));
        }
        await db.SaveChangesAsync();

        var fake = new FakeAsking
        {
            Result = [.. Enumerable.Range(0, 5).Select(i => Candidate($"tool-{i}"))]
        };
        var svc = CreateService(db, fake);

        var result = await svc.GetDailyQuestionsAsync("user-1", UtcDay(PastDay));

        Assert.Equal(3, result.Questions.Count);
        var cached = Assert.Single(await db.DailyQuestionSets.ToListAsync());
        Assert.Equal(DailyQuestionSet.CurrentPayloadVersion, cached.PayloadVersion);
        Assert.Equal(3, JsonSerializer.Deserialize<List<AskingQuestionResponse>>(cached.PayloadJson)!.Count);
    }

    [Fact]
    public async Task AdjudicatedMatchers_FilteredOnRead_ZeroRecall()
    {
        using var db = CreateDbContext();
        var toolA = await EnsureAppAsync(db, "tool-a");
        var toolB = await EnsureAppAsync(db, "tool-b");
        db.ActivitySegments.Add(Segment(PastDay.AddHours(14), PastDay.AddHours(16), toolA.Id, "tool-a|"));
        db.ActivitySegments.Add(Segment(PastDay.AddHours(16), PastDay.AddHours(18), toolB.Id, "tool-b|"));
        await db.SaveChangesAsync();

        var fake = new FakeAsking { Result = [Candidate("tool-a"), Candidate("tool-b")] };
        var svc = CreateService(db, fake);
        Assert.Equal(2, (await svc.GetDailyQuestionsAsync("user-1", UtcDay(PastDay))).Questions.Count);

        // 用户裁决：tool-a 绑进 Strand，tool-b 静音——两个出口都要把问题从队列里 diff 掉
        var knowledge = new KnowledgeService(db);
        await knowledge.CreateStrandAsync("user-1", new CreateStrandRequest
        {
            Name = "工具甲",
            Gloss = "",
            Members = [AppMatcher("tool-a")]
        });
        await knowledge.MuteMatcherAsync("user-1", AppMatcher("tool-b"));

        var after = await svc.GetDailyQuestionsAsync("user-1", UtcDay(PastDay));

        Assert.Empty(after.Questions);
        Assert.Equal(1, fake.Calls); // diff 是读时确定性过滤，零 LLM 重调
    }

    [Fact]
    public async Task LegacyFixedOffsetRowIsPreservedAndCannotSatisfyANewWindowKey()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(Segment(PastDay.AddHours(14), PastDay.AddHours(16)));
        // ADR-044 前的 fixed-offset row：没有可可靠回填的 IANA timezone / end / WindowKey。
        db.DailyQuestionSets.Add(new DailyQuestionSet
        {
            OwnerId = "user-1",
            WindowStart = DateRange.Day(PastDay).UtcStart,
            SegmentWatermark = PastDay.AddHours(16).UtcDateTime,
            GeneratedAt = PastDay.AddHours(16),
            PayloadVersion = DailyQuestionSet.CurrentPayloadVersion,
            PayloadJson = """[{"matcher":{"source":"system","steps":[{"reading":"app","op":"equals","value":"sometool"}]},"question":"旧表单问题","evidence":"…","proposedName":"预填名字","proposedGloss":"预填释义"}]""",
        });
        await db.SaveChangesAsync();

        var fake = new FakeAsking { Result = [Candidate("sometool", "新证据卡问题")] };
        var svc = CreateService(db, fake);

        var result = await svc.GetDailyQuestionsAsync("user-1", UtcDay(PastDay));

        // legacy row 即使 payload 版本当前也绝不命中新 WindowKey，并且原样保留。
        Assert.Equal(1, fake.Calls);
        var q = Assert.Single(result.Questions);
        Assert.Equal("新证据卡问题", q.Question);
        var rows = await db.DailyQuestionSets.OrderBy(row => row.Id).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Null(rows[0].WindowKey);
        Assert.Contains("预填名字", rows[0].PayloadJson);
        Assert.Equal(UtcDay(PastDay).WindowKey.Value, rows[1].WindowKey);
        Assert.DoesNotContain("预填名字", rows[1].PayloadJson);
    }

    [Fact]
    public async Task Today_WatermarkLag_TriggersReask()
    {
        var day = new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);
        using var db = CreateDbContext();
        db.ActivitySegments.Add(Segment(day.AddHours(9), day.AddHours(10)));
        await db.SaveChangesAsync();

        var fake = new FakeAsking { Result = [Candidate("sometool")] };
        var svc = CreateService(db, fake, new FixedClock(day.AddHours(12)));

        await svc.GetDailyQuestionsAsync("user-1", UtcDay(day));
        Assert.Equal(1, fake.Calls);

        // 水位 10:00，新段推进到 11:30 → 落后 1.5h 过阈值，重新发问
        db.ActivitySegments.Add(Segment(day.AddHours(10), day.AddHours(11.5)));
        await db.SaveChangesAsync();

        await svc.GetDailyQuestionsAsync("user-1", UtcDay(day));
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

        var fake = new FakeAsking { Result = [Candidate("sometool")] };
        var svc = CreateService(db, fake);

        var mine = await svc.GetDailyQuestionsAsync("user-1", UtcDay(PastDay));
        var theirs = await svc.GetDailyQuestionsAsync("user-2", UtcDay(PastDay));

        Assert.Single(mine.Questions);
        Assert.Empty(theirs.Questions); // user-2 无段 → 空日，不问
    }

    [Fact]
    public async Task ActiveProbeHit_YieldsRecurrenceQuestion_Deterministically()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(Segment(PastDay.AddHours(14), PastDay.AddHours(16)));
        await db.SaveChangesAsync();

        // 用户此前确认过一个 Episode + Probe（谓词命中今天的 sometool）
        var episodes = new EpisodeService(db, new KnowledgeService(db));
        var episode = (await episodes.CreateEpisodeAsync("user-1", new CreateEpisodeRequest
        {
            LocalDate = new DateOnly(2026, 7, 1),
            Text = "帮朋友调那个内网工具",
        })).Episode!;
        var probe = (await episodes.CreateProbeAsync("user-1", episode.Id, new CreateProbeRequest
        {
            Matcher = AppMatcher("sometool"),
        })).Probe!;

        // 判官对这个活动没意见（空数组）——recurrence 问题不占判官配额、零 LLM 依赖
        var fake = new FakeAsking { Result = [] };
        var svc = CreateService(db, fake);

        var result = await svc.GetDailyQuestionsAsync("user-1", UtcDay(PastDay));

        var q = Assert.Single(result.Questions);
        Assert.Equal(AskingQuestionKinds.Recurrence, q.Kind);
        Assert.Equal(probe.Id, q.Id); // 身份 = ProbeId：确定性、可校验 Owner
        Assert.Equal(probe.Id, q.ProbeId);
        Assert.Equal(episode.Id, q.EpisodeId);
        Assert.Contains("帮朋友调那个内网工具", q.Question);
        Assert.NotEmpty(q.Observations);

        // 解决 Probe 后不再发问
        await episodes.ResolveProbeAsync("user-1", probe.Id, new ResolveProbeRequest { Resolution = "denied" });
        Assert.Empty((await svc.GetDailyQuestionsAsync("user-1", UtcDay(PastDay))).Questions);
        Assert.Equal(1, fake.Calls); // 全程只有首次生成调了判官
    }

    [Fact]
    public async Task ClusterQuestion_SamePredicateAsActiveProbe_YieldsToRecurrence()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(Segment(PastDay.AddHours(14), PastDay.AddHours(16)));
        await db.SaveChangesAsync();

        var episodes = new EpisodeService(db, new KnowledgeService(db));
        var episode = (await episodes.CreateEpisodeAsync("user-1", new CreateEpisodeRequest
        {
            LocalDate = new DateOnly(2026, 7, 1),
            Text = "上次那件事",
        })).Episode!;
        await episodes.CreateProbeAsync("user-1", episode.Id, new CreateProbeRequest
        {
            Matcher = AppMatcher("sometool"),
        });

        // 判官恰好也提了同谓词的 cluster 问题：让位给 recurrence，不重复问两遍
        var fake = new FakeAsking { Result = [Candidate("sometool")] };
        var svc = CreateService(db, fake);

        var result = await svc.GetDailyQuestionsAsync("user-1", UtcDay(PastDay));

        var q = Assert.Single(result.Questions);
        Assert.Equal(AskingQuestionKinds.Recurrence, q.Kind);
    }

    [Fact]
    public async Task FindQuestion_ReturnsServedCard_RejectsForeignId()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(Segment(PastDay.AddHours(14), PastDay.AddHours(16)));
        await db.SaveChangesAsync();

        var fake = new FakeAsking { Result = [Candidate("sometool")] };
        var svc = CreateService(db, fake);

        var window = UtcDay(PastDay);
        var served = (await svc.GetDailyQuestionsAsync("user-1", window)).Questions.Single();

        var found = await svc.FindQuestionAsync("user-1", window, served.Id);
        Assert.NotNull(found);
        Assert.Equal(served.Question, found.Question);

        // 伪造 Id / 跨 Owner 都取不到证据——第二阶段只能解释服务端发出过的证据卡
        Assert.Null(await svc.FindQuestionAsync("user-1", window, Guid.CreateVersion7()));
        Assert.Null(await svc.FindQuestionAsync("user-2", window, served.Id));
    }
}
