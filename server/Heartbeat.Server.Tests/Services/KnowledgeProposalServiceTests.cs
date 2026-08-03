using Heartbeat.Core;
using Heartbeat.Core.DTOs.Knowledge;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Heartbeat.Server.Services;
using Heartbeat.Server.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Tests.Services;

/// <summary>
/// 两阶段教学编排（ADR-031 §6）：证据引用纪律、proposal 零写入、
/// 以及两个入口提交同一 change set 的领域效果一致性。
/// </summary>
[Collection("postgres")]
public class KnowledgeProposalServiceTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    private long _deviceId;
    private long _appId;

    private static readonly DateTimeOffset PastDay = new(2026, 7, 10, 0, 0, 0, TimeSpan.Zero);

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
            => Task.FromResult<IReadOnlyList<AskingCandidate>?>([new("这是什么？", AppMatcher("sometool"))]);
    }

    private sealed class FakeProposer : IProposalGenerator
    {
        public RawKnowledgeProposal? Result;
        public AskingQuestionResponse? LastQuestion;
        public string? LastAnswer;
        public ProposalContext? LastContext;

        public Task<RawKnowledgeProposal?> ProposeAsync(
            AskingQuestionResponse question, string answer, ProposalContext context, CancellationToken ct = default)
        {
            LastQuestion = question;
            LastAnswer = answer;
            LastContext = context;
            return Task.FromResult(Result);
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

    private (KnowledgeProposalService Proposals, QuestionService Questions, FakeProposer Proposer)
        CreateServices(AppDbContext db)
    {
        var assembler = new DigestAssembler(db);
        var questions = new QuestionService(db, assembler, new FakeAsking());
        var proposer = new FakeProposer();
        return (new KnowledgeProposalService(db, questions, assembler, proposer), questions, proposer);
    }

    private async Task<Guid> ServeQuestionAsync(AppDbContext db, QuestionService questions)
    {
        db.ActivitySegments.Add(Segment(PastDay.AddHours(14), PastDay.AddHours(16)));
        await db.SaveChangesAsync();
        return (await questions.GetDailyQuestionsAsync("user-1", PastDay)).Questions.Single().Id;
    }

    [Fact]
    public async Task Propose_InterpretsServedEvidence_ZeroDatabaseWrites()
    {
        using var db = CreateDbContext();
        var (proposals, questions, proposer) = CreateServices(db);
        var questionId = await ServeQuestionAsync(db, questions);

        proposer.Result = new RawKnowledgeProposal
        {
            Explanation = "你在做 Hyperframes 的调研",
            Operations =
            [
                new() { OpId = "op1", Type = "createStrand", Name = "Hyperframes", Gloss = "动效框架" },
                new() { OpId = "op2", Type = "bindMatcher", StrandOpId = "op1", Matcher = AppMatcher("sometool") },
            ],
        };

        var before = new
        {
            Strands = await db.Strands.CountAsync(),
            Matchers = await db.StrandMatchers.CountAsync(),
            Muted = await db.MutedMatchers.CountAsync(),
            Episodes = await db.Episodes.CountAsync(),
            Probes = await db.RecurrenceProbes.CountAsync(),
        };

        var result = await proposals.ProposeAsync("user-1", questionId, new ProposeFromQuestionRequest
        {
            Date = PastDay,
            Answer = "这是我实习在调研的 Hyperframes",
        });

        Assert.Null(result.Error);
        Assert.Equal(2, result.Proposal!.Operations.Count);
        Assert.Equal("你在做 Hyperframes 的调研", result.Proposal.Explanation);

        // proposal 阶段零写入：知识库五张表全部原样
        Assert.Equal(before.Strands, await db.Strands.CountAsync());
        Assert.Equal(before.Matchers, await db.StrandMatchers.CountAsync());
        Assert.Equal(before.Muted, await db.MutedMatchers.CountAsync());
        Assert.Equal(before.Episodes, await db.Episodes.CountAsync());
        Assert.Equal(before.Probes, await db.RecurrenceProbes.CountAsync());

        // LLM 吃到的是服务端物化的证据卡与用户原话
        Assert.Equal(questionId, proposer.LastQuestion!.Id);
        Assert.Contains("sometool", proposer.LastQuestion.Observations.Select(o => o.Value));
        Assert.Equal("这是我实习在调研的 Hyperframes", proposer.LastAnswer);
    }

    [Fact]
    public async Task Propose_UnknownOrForeignQuestionId_Rejected()
    {
        using var db = CreateDbContext();
        var (proposals, questions, proposer) = CreateServices(db);
        var questionId = await ServeQuestionAsync(db, questions);
        proposer.Result = new RawKnowledgeProposal();

        // 伪造的问题 Id：取不到证据，第二阶段拒绝解释
        var fabricated = await proposals.ProposeAsync("user-1", Guid.CreateVersion7(),
            new ProposeFromQuestionRequest { Date = PastDay, Answer = "回答" });
        Assert.Equal(ProposalErrorCodes.QuestionNotFound, fabricated.Error!.Code);

        // 别人的问题 Id：Owner 隔离（user-2 无段 → 无此问题）
        var foreign = await proposals.ProposeAsync("user-2", questionId,
            new ProposeFromQuestionRequest { Date = PastDay, Answer = "回答" });
        Assert.Equal(ProposalErrorCodes.QuestionNotFound, foreign.Error!.Code);

        Assert.Null(proposer.LastQuestion); // LLM 根本没被调
    }

    [Fact]
    public async Task Propose_EmptyAnswer_OrLlmFailure_NoSideEffects()
    {
        using var db = CreateDbContext();
        var (proposals, questions, proposer) = CreateServices(db);
        var questionId = await ServeQuestionAsync(db, questions);

        var empty = await proposals.ProposeAsync("user-1", questionId,
            new ProposeFromQuestionRequest { Date = PastDay, Answer = "  " });
        Assert.Equal(ProposalErrorCodes.EmptyAnswer, empty.Error!.Code);

        proposer.Result = null; // LLM 失败
        var failed = await proposals.ProposeAsync("user-1", questionId,
            new ProposeFromQuestionRequest { Date = PastDay, Answer = "回答" });
        Assert.Equal(ProposalErrorCodes.GenerationFailed, failed.Error!.Code);

        Assert.Equal(0, await db.Strands.CountAsync());
    }

    [Fact]
    public async Task Propose_ContextCarriesExistingKnowledge_ByUuid_WithVersions()
    {
        using var db = CreateDbContext();
        var knowledge = new KnowledgeService(db);
        var parent = (await knowledge.CreateStrandAsync("user-1", new CreateStrandRequest { Name = "哔哩哔哩实习" })).Strand!;
        var child = (await knowledge.CreateStrandAsync("user-1", new CreateStrandRequest
        {
            Name = "Hyperframes",
            ParentStrandId = parent.Id,
        })).Strand!;
        // 别人的知识不进语境
        await knowledge.CreateStrandAsync("user-2", new CreateStrandRequest { Name = "别人的脉络" });

        var (proposals, questions, proposer) = CreateServices(db);
        var questionId = await ServeQuestionAsync(db, questions);
        proposer.Result = new RawKnowledgeProposal();

        await proposals.ProposeAsync("user-1", questionId,
            new ProposeFromQuestionRequest { Date = PastDay, Answer = "回答" });

        var context = proposer.LastContext!;
        Assert.Equal(2, context.Strands.Count);
        var contextChild = Assert.Single(context.Strands, s => s.Id == child.Id);
        Assert.Equal(["哔哩哔哩实习", "Hyperframes"], contextChild.Path); // path 消歧，绝不按名绑定
        Assert.Equal(child.Version, contextChild.Version); // 读取时版本，供 sanitizer 盖章
        Assert.DoesNotContain(context.Strands, s => s.Path.Contains("别人的脉络"));
    }

    [Fact]
    public async Task BothEntrances_SameChangeSet_SameDomainEffect()
    {
        // 两个入口（主动发问 proposal 流 / 直接投喂 commit 端点，即 Recap 纠正与手动复合操作的路径）
        // 提交同一 change set，领域效果一致。
        using var db = CreateDbContext();
        var (proposals, questions, proposer) = CreateServices(db);
        var questionId = await ServeQuestionAsync(db, questions);

        proposer.Result = new RawKnowledgeProposal
        {
            Operations =
            [
                new() { OpId = "op1", Type = "createStrand", Name = "Hyperframes", Gloss = "动效框架" },
                new() { OpId = "op2", Type = "bindMatcher", StrandOpId = "op1", Matcher = AppMatcher("sometool") },
                new()
                {
                    OpId = "op3", Type = "createEpisode",
                    Text = "做了产品调研", LocalDate = "2026-07-10", RelatedOpId = "op1",
                },
            ],
        };

        var proposal = (await proposals.ProposeAsync("user-1", questionId,
            new ProposeFromQuestionRequest { Date = PastDay, Answer = "实习调研" })).Proposal!;

        var knowledge = new KnowledgeService(db);
        var commit = new KnowledgeCommitService(db, knowledge, new EpisodeService(db, knowledge));

        // 入口 A：user-1 提交 proposal 产物
        var viaProposal = await commit.CommitAsync("user-1", new CommitChangeSetRequest
        {
            Operations = proposal.Operations,
        });
        Assert.Null(viaProposal.Error);

        // 入口 B：另一个 Owner 手工构造同形 change set 直接提交
        var viaManual = await commit.CommitAsync("user-2", new CommitChangeSetRequest
        {
            Operations = proposal.Operations,
        });
        Assert.Null(viaManual.Error);

        // 两个 Owner 库中的领域效果同构：1 Strand + 1 Matcher + 1 关联 Episode
        foreach (var owner in new[] { "user-1", "user-2" })
        {
            var strand = await db.Strands.Include(s => s.Members).SingleAsync(s => s.OwnerId == owner);
            Assert.Equal("Hyperframes", strand.Name);
            Assert.Single(strand.Members);
            var episode = await db.Episodes.SingleAsync(e => e.OwnerId == owner);
            Assert.Equal(strand.Id, episode.RelatedStrandId);
            Assert.Equal("做了产品调研", episode.Text);
        }
    }
}
