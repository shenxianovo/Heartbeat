using Heartbeat.Core;
using Heartbeat.Core.DTOs.Knowledge;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Heartbeat.Server.Services;
using Heartbeat.Server.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Tests.Services;

/// <summary>
/// Recap 纠正闭环（ADR-031 §6，issue 06）：纠正 propose 锁定目标日证据 → 复用共享
/// commit → 提交成功后目标日显式 force 重生成。知识事务与叙事生成是两个独立阶段：
/// 提交失败不生成；生成失败不回滚知识、不覆盖上一版成功 Recap；其他日期零 LLM 只判脏。
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

    private sealed class FakeRecapGenerator : IRecapGenerator
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

    // ===== 提交 + 目标日 force 重生成的闭环 =====

    /// <summary>先让目标日有一版成功 Recap（用户正在看、正在纠正的那一版）。</summary>
    private async Task<string> GenerateInitialRecapAsync(Env env)
    {
        var initial = await env.Recaps.GetDailyRecapAsync("user-1", TargetDay, force: false);
        Assert.False(initial.IsEmpty);
        return initial.Narrative!;
    }

    [Fact]
    public async Task EpisodeOnlyCorrection_CommitThenForceRegenerate_TargetDayUpdated()
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

        // 提交成功后目标日显式 force 重生成：新叙事 + 新知识投影哈希，读取不再判脏
        var regenerated = await env.Recaps.GetDailyRecapAsync("user-1", TargetDay, force: true);
        Assert.NotEqual(oldNarrative, regenerated.Narrative);
        Assert.False(regenerated.KnowledgeStale);
        Assert.Equal(2, env.Generator.Calls);

        var reread = await env.Recaps.GetDailyRecapAsync("user-1", TargetDay, force: false);
        Assert.Equal(regenerated.Narrative, reread.Narrative);
        Assert.False(reread.KnowledgeStale);
        Assert.Equal(2, env.Generator.Calls); // 缓存命中，无追加生成
    }

    [Fact]
    public async Task StrandCorrection_OtherDates_ZeroLlm_LazyStaleOnRead()
    {
        using var db = CreateDbContext();
        var env = CreateEnv(db);
        await SeedSegmentsAsync(db, TargetDay, OtherDay);

        // 两天都先有成功 Recap
        await GenerateInitialRecapAsync(env);
        var otherOld = await env.Recaps.GetDailyRecapAsync("user-1", OtherDay, force: false);
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

        // 只有目标日 force 重生成
        var regenerated = await env.Recaps.GetDailyRecapAsync("user-1", TargetDay, force: true);
        Assert.False(regenerated.KnowledgeStale);
        Assert.Equal(3, env.Generator.Calls);

        // 其他历史日期：不批量生成，读取时零 LLM、惰性 stale hint，正文原样
        var otherReread = await env.Recaps.GetDailyRecapAsync("user-1", OtherDay, force: false);
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

        var regenerated = await env.Recaps.GetDailyRecapAsync("user-1", TargetDay, force: true);
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
        var reread = await env.Recaps.GetDailyRecapAsync("user-1", TargetDay, force: false);
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

        // force 重生成失败：不回滚知识，不覆盖上一版成功 Recap
        env.Generator.Fail = true;
        await Assert.ThrowsAsync<RecapGenerationException>(
            () => env.Recaps.GetDailyRecapAsync("user-1", TargetDay, force: true));
        Assert.Equal(1, await db.Episodes.CountAsync()); // 已确认的知识还在

        var cached = await env.Recaps.GetDailyRecapAsync("user-1", TargetDay, force: false);
        Assert.Equal(oldNarrative, cached.Narrative);
        Assert.True(cached.KnowledgeStale); // 知识已保存，Recap 尚未更新

        // 单独重试重生成：成功后收敛
        env.Generator.Fail = false;
        var retried = await env.Recaps.GetDailyRecapAsync("user-1", TargetDay, force: true);
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
        var pub = await env.Recaps.GetCachedDailyRecapAsync("user-1", TargetDay);
        Assert.Equal(oldNarrative, pub!.Narrative);
        Assert.False(pub.KnowledgeStale);
        Assert.Equal(1, env.Generator.Calls);
    }
}
