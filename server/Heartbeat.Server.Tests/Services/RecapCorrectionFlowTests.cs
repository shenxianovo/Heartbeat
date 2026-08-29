using System.Runtime.CompilerServices;
using Heartbeat.Core;
using Heartbeat.Core.DTOs.Knowledge;
using Heartbeat.Core.DTOs.Recaps;
using Heartbeat.Server.Calendar;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Heartbeat.Server.Services;
using Heartbeat.Server.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Tests.Services;

/// <summary>
/// Recap 纠正闭环（ADR-031 §6，issue 06）：纠正 propose 锁定目标日证据 → 复用共享
/// commit → 提交成功后目标日显式重生成（ADR-042 §2 起是 POST 流式生成，不再是 GET 的 force）。
/// 知识事务与叙事生成是两个独立阶段：提交失败不生成；生成失败不回滚知识、不覆盖上一版成功
/// Recap；其他日期零 LLM 只判脏。
/// </summary>
[Collection("postgres")]
public class RecapCorrectionFlowTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    private long _deviceId;
    private long _appId;

    /// <summary>被纠正的目标日（已结束的历史窗口）。</summary>
    private static readonly DateTimeOffset TargetDay = new(2026, 7, 10, 0, 0, 0, TimeSpan.Zero);

    /// <summary>另一个历史日：验证纠正后零 LLM、读取时惰性判脏。</summary>
    private static readonly DateTimeOffset OtherDay = new(2026, 7, 8, 0, 0, 0, TimeSpan.Zero);

    private static ResolvedCalendarWindow DayWindow(DateTimeOffset day) => new(
        1,
        "day",
        day.ToString("yyyy-MM-dd"),
        "Etc/UTC",
        new DateTimeOffset(day.UtcDateTime.Date, TimeSpan.Zero),
        new DateTimeOffset(day.UtcDateTime.Date.AddDays(1), TimeSpan.Zero),
        NodaTime.LocalDate.FromDateTime(day.UtcDateTime.Date),
        NodaTime.LocalDate.FromDateTime(day.UtcDateTime.Date.AddDays(1)));

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
        public Task<IReadOnlyList<AskingCandidate>?> AskAsync(
            string digest, AskingContext context, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AskingCandidate>?>([]);
    }

    private sealed class FakeProposer : IProposalGenerator
    {
        public RawKnowledgeProposal? Result;
        public string? LastDigest;
        public string? LastCorrection;
        public ProposalContext? LastContext;
        public AskingQuestionResponse? LastQuestion;

        public Task<RawKnowledgeProposal?> ProposeAsync(
            AskingQuestionResponse question, string answer, ProposalContext context, CancellationToken ct = default)
        {
            LastQuestion = question;
            return Task.FromResult(Result);
        }

        public Task<RawKnowledgeProposal?> ProposeCorrectionAsync(
            string digest, string correction, ProposalContext context, CancellationToken ct = default)
        {
            LastDigest = digest;
            LastCorrection = correction;
            LastContext = context;
            return Task.FromResult(Result);
        }
    }

    /// <summary>
    /// 假生成器（流式接口随 ADR-042 §8）：分块吐出 "narrative-N"，拼接后才是完整叙事。
    /// 失败点可选在首块之前或若干块之后——纠正闭环关心的是"生成失败不回滚知识、不覆盖上一版
    /// 正文"，这两种失败形状都必须守住。
    /// </summary>
    private sealed class FakeRecapGenerator : IRecapGenerator
    {
        public int Calls;

        /// <summary>吐满这么多块后抛失败；0 = 首块之前就失败，null = 不失败。</summary>
        public int? FailAfterChunks;

        public string Model => "fake-model";
        public string PromptHash => "deadbeef";

        public async IAsyncEnumerable<LlmChunk> GenerateStreamAsync(
            string digest, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var call = ++Calls;
            await Task.CompletedTask;

            if (FailAfterChunks == 0) throw new RecapGenerationException("upstream down");

            string[] chunks = ["narrative-", $"{call}"];
            for (var i = 0; i < chunks.Length; i++)
            {
                // 纠正闭环只关心正文：思考块的语义由 RecapServiceTests 专门钉。
                yield return LlmChunk.OfContent(chunks[i]);
                if (FailAfterChunks == i + 1) throw new RecapGenerationException("upstream down");
            }
        }
    }

    private static MatcherDto AppMatcher(string app) => new()
    {
        Source = ActivitySources.System,
        Steps = [new() { Reading = "app", Op = MatcherOps.Equal, Value = app }]
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

    private sealed record Env(
        KnowledgeProposalService Proposals,
        KnowledgeCommitService Commit,
        RecapService Recaps,
        FakeProposer Proposer,
        FakeRecapGenerator Generator);

    private Env CreateEnv(AppDbContext db)
    {
        var assembler = new DigestAssembler(db);
        var knowledge = new KnowledgeService(db);
        var proposer = new FakeProposer();
        var generator = new FakeRecapGenerator();
        return new Env(
            new KnowledgeProposalService(db, new QuestionService(db, assembler, new FakeAsking()), assembler, proposer),
            new KnowledgeCommitService(db, knowledge, new EpisodeService(db, knowledge)),
            new RecapService(db, generator, assembler),
            proposer,
            generator);
    }

    private async Task SeedSegmentsAsync(AppDbContext db, params DateTimeOffset[] days)
    {
        foreach (var day in days)
            db.ActivitySegments.Add(Segment(day.AddHours(14), day.AddHours(16)));
        await db.SaveChangesAsync();
    }

    /// <summary>抽干一条生成流，事件按到达顺序返回。</summary>
    private static async Task<List<RecapStreamEvent>> DrainAsync(RecapService svc, DateTimeOffset date)
    {
        var events = new List<RecapStreamEvent>();
        await foreach (var e in svc.GenerateDailyRecapStreamAsync("user-1", DayWindow(date)))
            events.Add(e);
        return events;
    }

    /// <summary>显式重生成一次并要求成功，返回 done 里的 DTO。</summary>
    private static async Task<DailyRecapResponse> RegenerateAsync(RecapService svc, DateTimeOffset date)
    {
        var events = await DrainAsync(svc, date);
        Assert.DoesNotContain(events, e => e.Type == RecapStreamEvent.ErrorType);
        return events.Single(e => e.Type == RecapStreamEvent.DoneType).Recap!;
    }

    // ===== 纠正 propose：证据锁定目标日，零写入 =====

    [Fact]
    public async Task ProposeCorrection_LocksTargetDayDigest_ZeroWrites()
    {
        using var db = CreateDbContext();
        var env = CreateEnv(db);
        await SeedSegmentsAsync(db, TargetDay);
        var knowledge = new KnowledgeService(db);
        var strand = (await knowledge.CreateStrandAsync("user-1", new CreateStrandRequest { Name = "已有脉络" })).Strand!;

        env.Proposer.Result = new RawKnowledgeProposal
        {
            Explanation = "你那天在做调研",
            Operations = [new() { OpId = "op1", Type = "createEpisode", Text = "做了调研", LocalDate = "2026-07-10" }],
        };

        var result = await env.Proposals.ProposeCorrectionAsync("user-1", new ProposeCorrectionRequest
        {
            Date = TargetDay,
            Correction = "那天其实是在做 Hyperframes 的调研，回顾里没提",
        });

        Assert.Null(result.Error);
        Assert.Single(result.Proposal!.Operations);

        // LLM 吃到的是目标日活动摘要（与叙事同一份 digest）+ 用户原话，不是 Recap 散文
        Assert.Contains("sometool", env.Proposer.LastDigest);
        Assert.Equal("那天其实是在做 Hyperframes 的调研，回顾里没提", env.Proposer.LastCorrection);
        Assert.Equal(new DateOnly(2026, 7, 10), env.Proposer.LastContext!.LocalDate);
        Assert.Contains(env.Proposer.LastContext.Strands, s => s.Id == strand.Id);

        // proposal 阶段零写入
        Assert.Equal(0, await db.Episodes.CountAsync());
        Assert.Equal(0, await db.Recaps.CountAsync());
    }

    [Fact]
    public async Task ProposeCorrection_EmptyDayOrEmptyText_Rejected()
    {
        using var db = CreateDbContext();
        var env = CreateEnv(db);

        // 目标日没有任何观察：没有可核对的证据窗口
        var emptyDay = await env.Proposals.ProposeCorrectionAsync("user-1",
            new ProposeCorrectionRequest { Date = TargetDay, Correction = "纠正" });
        Assert.Equal(ProposalErrorCodes.EmptyDay, emptyDay.Error!.Code);
        Assert.Null(env.Proposer.LastDigest); // LLM 根本没被调

        await SeedSegmentsAsync(db, TargetDay);
        var emptyText = await env.Proposals.ProposeCorrectionAsync("user-1",
            new ProposeCorrectionRequest { Date = TargetDay, Correction = "  " });
        Assert.Equal(ProposalErrorCodes.EmptyAnswer, emptyText.Error!.Code);
    }

    [Fact]
    public async Task ProposeCorrection_LlmFailure_NoSideEffects()
    {
        using var db = CreateDbContext();
        var env = CreateEnv(db);
        await SeedSegmentsAsync(db, TargetDay);

        env.Proposer.Result = null; // LLM 失败
        var failed = await env.Proposals.ProposeCorrectionAsync("user-1",
            new ProposeCorrectionRequest { Date = TargetDay, Correction = "纠正" });

        Assert.Equal(ProposalErrorCodes.GenerationFailed, failed.Error!.Code);
        Assert.Equal(0, await db.Episodes.CountAsync());
    }

    [Fact]
    public async Task ProposeCorrection_OwnerIsolation_ContextExcludesOthers()
    {
        using var db = CreateDbContext();
        var env = CreateEnv(db);
        await SeedSegmentsAsync(db, TargetDay);
        var knowledge = new KnowledgeService(db);
        await knowledge.CreateStrandAsync("user-2", new CreateStrandRequest { Name = "别人的脉络" });

        env.Proposer.Result = new RawKnowledgeProposal();
        await env.Proposals.ProposeCorrectionAsync("user-1",
            new ProposeCorrectionRequest { Date = TargetDay, Correction = "纠正" });

        Assert.DoesNotContain(env.Proposer.LastContext!.Strands, s => s.Path.Contains("别人的脉络"));
    }

    // ===== 提交 + 目标日重生成的闭环 =====

    /// <summary>先让目标日有一版成功 Recap（用户正在看、正在纠正的那一版）。</summary>
    private static async Task<string> GenerateInitialRecapAsync(Env env)
    {
        var initial = await RegenerateAsync(env.Recaps, TargetDay);
        Assert.False(initial.IsEmpty);
        return initial.Narrative!;
    }

    [Fact]
    public async Task EpisodeOnlyCorrection_CommitThenRegenerate_TargetDayUpdated()
    {
        using var db = CreateDbContext();
        var env = CreateEnv(db);
        await SeedSegmentsAsync(db, TargetDay);
        var oldNarrative = await GenerateInitialRecapAsync(env);

        // 只建 Episode 的纠正提案 → 用户确认 → 共享 commit
        env.Proposer.Result = new RawKnowledgeProposal
        {
            Operations = [new() { OpId = "op1", Type = "createEpisode", Text = "做了 Hyperframes 调研", LocalDate = "2026-07-10" }],
        };
        var proposal = (await env.Proposals.ProposeCorrectionAsync("user-1",
            new ProposeCorrectionRequest { Date = TargetDay, Correction = "补上调研这件事" })).Proposal!;

        var commit = await env.Commit.CommitAsync("user-1", new CommitChangeSetRequest { Operations = proposal.Operations });
        Assert.Null(commit.Error);
        Assert.Equal("做了 Hyperframes 调研", (await db.Episodes.SingleAsync()).Text);

        // 提交成功后目标日显式重生成：新叙事 + 新知识投影哈希，读取不再判脏
        var regenerated = await RegenerateAsync(env.Recaps, TargetDay);
        Assert.NotEqual(oldNarrative, regenerated.Narrative);
        Assert.False(regenerated.KnowledgeStale);
        Assert.Equal(2, env.Generator.Calls);

        var reread = await env.Recaps.GetDailyRecapAsync("user-1", DayWindow(TargetDay));
        Assert.Equal(regenerated.Narrative, reread.Narrative);
        Assert.False(reread.KnowledgeStale);
        Assert.Equal(2, env.Generator.Calls); // 缓存命中，读取不追加生成
    }

    [Fact]
    public async Task StrandCorrection_OtherDates_ZeroLlm_LazyStaleOnRead()
    {
        using var db = CreateDbContext();
        var env = CreateEnv(db);
        await SeedSegmentsAsync(db, TargetDay, OtherDay);

        // 两天都先有成功 Recap
        await GenerateInitialRecapAsync(env);
        var otherOld = await RegenerateAsync(env.Recaps, OtherDay);
        Assert.Equal(2, env.Generator.Calls);

        // 只改 Strand 的纠正：新脉络 + 指纹（两天的段都命中）
        env.Proposer.Result = new RawKnowledgeProposal
        {
            Operations =
            [
                new() { OpId = "op1", Type = "createStrand", Name = "Hyperframes", Gloss = "产品调研" },
                new() { OpId = "op2", Type = "bindMatcher", StrandOpId = "op1", Matcher = AppMatcher("sometool") },
            ],
        };
        var proposal = (await env.Proposals.ProposeCorrectionAsync("user-1",
            new ProposeCorrectionRequest { Date = TargetDay, Correction = "这些活动属于 Hyperframes 调研" })).Proposal!;
        Assert.Null((await env.Commit.CommitAsync("user-1", new CommitChangeSetRequest { Operations = proposal.Operations })).Error);

        // 只有目标日被显式重生成
        var regenerated = await RegenerateAsync(env.Recaps, TargetDay);
        Assert.False(regenerated.KnowledgeStale);
        Assert.Equal(3, env.Generator.Calls);

        // 其他历史日期：不批量生成，读取时零 LLM、惰性 stale hint，正文原样
        var otherReread = await env.Recaps.GetDailyRecapAsync("user-1", DayWindow(OtherDay));
        Assert.True(otherReread.KnowledgeStale);
        Assert.Equal(otherOld.Narrative, otherReread.Narrative);
        Assert.Equal(3, env.Generator.Calls);
    }

    [Fact]
    public async Task MixedCorrection_StrandAndEpisodeAndProbe_SingleTransaction()
    {
        using var db = CreateDbContext();
        var env = CreateEnv(db);
        await SeedSegmentsAsync(db, TargetDay);
        await GenerateInitialRecapAsync(env);

        // 一次纠正同时：新建脉络 + 指纹 + 当天 Episode 关联它 + 另一件不确定的事 Episode + Probe
        env.Proposer.Result = new RawKnowledgeProposal
        {
            Operations =
            [
                new() { OpId = "op1", Type = "createStrand", Name = "Hyperframes", Gloss = "产品调研" },
                new() { OpId = "op2", Type = "bindMatcher", StrandOpId = "op1", Matcher = AppMatcher("sometool") },
                new() { OpId = "op3", Type = "createEpisode", Text = "做了可行性分析", LocalDate = "2026-07-10", RelatedOpId = "op1" },
                new() { OpId = "op4", Type = "createEpisode", Text = "帮朋友调了一次直播", LocalDate = "2026-07-10" },
                new() { OpId = "op5", Type = "createProbe", EpisodeOpId = "op4", Matcher = AppMatcher("livehime") },
            ],
        };
        var proposal = (await env.Proposals.ProposeCorrectionAsync("user-1",
            new ProposeCorrectionRequest { Date = TargetDay, Correction = "既有持续调研也有一次性帮忙" })).Proposal!;
        Assert.Equal(5, proposal.Operations.Count);

        var commit = await env.Commit.CommitAsync("user-1", new CommitChangeSetRequest { Operations = proposal.Operations });
        Assert.Null(commit.Error);

        var strand = await db.Strands.Include(s => s.Members).SingleAsync();
        Assert.Equal("Hyperframes", strand.Name);
        Assert.Single(strand.Members);
        var episodes = await db.Episodes.OrderBy(e => e.Text).ToListAsync();
        Assert.Equal(2, episodes.Count);
        Assert.Equal(strand.Id, episodes.Single(e => e.Text == "做了可行性分析").RelatedStrandId);
        var probe = await db.RecurrenceProbes.SingleAsync();
        Assert.Equal(episodes.Single(e => e.Text == "帮朋友调了一次直播").Id, probe.EpisodeId);

        var regenerated = await RegenerateAsync(env.Recaps, TargetDay);
        Assert.False(regenerated.KnowledgeStale);
    }

    [Fact]
    public async Task CommitFailure_WholeBatchRollsBack_NoRegeneration()
    {
        using var db = CreateDbContext();
        var env = CreateEnv(db);
        await SeedSegmentsAsync(db, TargetDay);
        var oldNarrative = await GenerateInitialRecapAsync(env);
        Assert.Equal(1, env.Generator.Calls);

        var knowledge = new KnowledgeService(db);
        var strand = (await knowledge.CreateStrandAsync("user-1", new CreateStrandRequest { Name = "已有脉络" })).Strand!;

        // Episode 合法 + 陈旧版本的 updateStrand：整批失败，无部分写入
        var stale = new CommitChangeSetRequest
        {
            Operations =
            [
                new()
                {
                    OpId = "op1", Type = KnowledgeOpTypes.CreateEpisode,
                    CreateEpisode = new CreateEpisodeOpDto { LocalDate = new DateOnly(2026, 7, 10), Text = "会被回滚" },
                },
                new()
                {
                    OpId = "op2", Type = KnowledgeOpTypes.UpdateStrand,
                    UpdateStrand = new UpdateStrandOpDto
                    {
                        StrandId = strand.Id,
                        ExpectedVersion = strand.Version + 41, // 陈旧提案
                        Name = "改名",
                    },
                },
            ],
        };
        var failed = await env.Commit.CommitAsync("user-1", stale);
        Assert.Equal("op2", failed.Error!.FailedOpId);
        Assert.Equal(KnowledgeErrorCodes.VersionConflict, failed.Error.Error.Code);
        Assert.Equal(0, await db.Episodes.CountAsync());

        // 提交失败不得触发重生成（编排契约）：缓存与叙事原样
        var reread = await env.Recaps.GetDailyRecapAsync("user-1", DayWindow(TargetDay));
        Assert.Equal(oldNarrative, reread.Narrative);
        Assert.Equal(1, env.Generator.Calls);
    }

    [Fact]
    public async Task RegenerationFailure_KeepsCommittedKnowledgeAndLastGoodRecap_RetrySucceeds()
    {
        using var db = CreateDbContext();
        var env = CreateEnv(db);
        await SeedSegmentsAsync(db, TargetDay);
        var oldNarrative = await GenerateInitialRecapAsync(env);

        // 知识事务提交成功
        env.Proposer.Result = new RawKnowledgeProposal
        {
            Operations = [new() { OpId = "op1", Type = "createEpisode", Text = "补记的事实", LocalDate = "2026-07-10" }],
        };
        var proposal = (await env.Proposals.ProposeCorrectionAsync("user-1",
            new ProposeCorrectionRequest { Date = TargetDay, Correction = "补一件事" })).Proposal!;
        Assert.Null((await env.Commit.CommitAsync("user-1", new CommitChangeSetRequest { Operations = proposal.Operations })).Error);

        // 重生成失败（首块之后断，半截叙事已发出）：失败以流内 error 抵达，头已发出后 502 不再可能。
        // 不回滚知识，也不覆盖上一版成功 Recap。
        env.Generator.FailAfterChunks = 1;
        var events = await DrainAsync(env.Recaps, TargetDay);
        Assert.Single(events, e => e.Type == RecapStreamEvent.ErrorType);
        Assert.DoesNotContain(events, e => e.Type == RecapStreamEvent.DoneType);
        Assert.Equal(1, await db.Episodes.CountAsync()); // 已确认的知识还在

        var cached = await env.Recaps.GetDailyRecapAsync("user-1", DayWindow(TargetDay));
        Assert.Equal(oldNarrative, cached.Narrative);
        Assert.True(cached.KnowledgeStale); // 知识已保存，Recap 尚未更新

        // 单独重试重生成：成功后收敛
        env.Generator.FailAfterChunks = null;
        var retried = await RegenerateAsync(env.Recaps, TargetDay);
        Assert.NotEqual(oldNarrative, retried.Narrative);
        Assert.False(retried.KnowledgeStale);
    }

    [Fact]
    public async Task PublicRead_UnaffectedByCorrection_CacheOnly()
    {
        using var db = CreateDbContext();
        var env = CreateEnv(db);
        await SeedSegmentsAsync(db, TargetDay);
        var oldNarrative = await GenerateInitialRecapAsync(env);

        // 纠正提交（知识变化使目标日判脏）但尚未重生成
        env.Proposer.Result = new RawKnowledgeProposal
        {
            Operations =
            [
                new() { OpId = "op1", Type = "createStrand", Name = "Hyperframes" },
                new() { OpId = "op2", Type = "bindMatcher", StrandOpId = "op1", Matcher = AppMatcher("sometool") },
            ],
        };
        var proposal = (await env.Proposals.ProposeCorrectionAsync("user-1",
            new ProposeCorrectionRequest { Date = TargetDay, Correction = "属于 Hyperframes" })).Proposal!;
        Assert.Null((await env.Commit.CommitAsync("user-1", new CommitChangeSetRequest { Operations = proposal.Operations })).Error);

        // 公开读取：纯缓存、不判脏、不触发生成
        var pub = await env.Recaps.GetCachedDailyRecapAsync("user-1", DayWindow(TargetDay));
        Assert.Equal(oldNarrative, pub!.Narrative);
        Assert.False(pub.KnowledgeStale);
        Assert.Equal(1, env.Generator.Calls);
    }
}
