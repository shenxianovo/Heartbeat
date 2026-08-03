using Heartbeat.Core;
using Heartbeat.Core.DTOs.Knowledge;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Heartbeat.Server.Services;
using Heartbeat.Server.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Tests.Services;

/// <summary>
/// 共享事务提交端（ADR-031 §6）：不变量重校验、原子性、错误定位、临时 ID 映射与并发冲突。
/// </summary>
[Collection("postgres")]
public class KnowledgeCommitServiceTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    private static KnowledgeCommitService CreateService(AppDbContext db)
    {
        var knowledge = new KnowledgeService(db);
        return new KnowledgeCommitService(db, knowledge, new EpisodeService(db, knowledge));
    }

    private static MatcherDto AppMatcher(string app) => new()
    {
        Source = ActivitySources.System,
        Steps = [new() { Reading = "app", Op = MatcherOps.Equal, Value = app }]
    };

    private static KnowledgeOperationDto CreateStrandOp(string opId, string name, string? parentOpId = null) => new()
    {
        OpId = opId,
        Type = KnowledgeOpTypes.CreateStrand,
        CreateStrand = new CreateStrandOpDto
        {
            Name = name,
            Parent = parentOpId == null ? null : new StrandRefDto { OpId = parentOpId },
        },
    };

    // ===== 多操作依赖 + 临时 ID 映射 =====

    [Fact]
    public async Task MultiOpChangeSet_TempIdChain_CommitsAtomically_MapsRealIds()
    {
        using var db = CreateDbContext();
        var svc = CreateService(db);

        // 一次确认的完整教学产物：建父子脉络 + 绑指纹 + 当天 Episode 关联新脉络
        var result = await svc.CommitAsync("user-1", new CommitChangeSetRequest
        {
            Operations =
            [
                CreateStrandOp("op1", "哔哩哔哩实习"),
                CreateStrandOp("op2", "Hyperframes", parentOpId: "op1"),
                new()
                {
                    OpId = "op3",
                    Type = KnowledgeOpTypes.BindMatcher,
                    BindMatcher = new BindMatcherOpDto
                    {
                        Strand = new StrandRefDto { OpId = "op2" },
                        Matcher = AppMatcher("blender"),
                    },
                },
                new()
                {
                    OpId = "op4",
                    Type = KnowledgeOpTypes.CreateEpisode,
                    CreateEpisode = new CreateEpisodeOpDto
                    {
                        LocalDate = new DateOnly(2026, 7, 24),
                        Text = "做 Hyperframes 的产品调研",
                        RelatedStrand = new StrandRefDto { OpId = "op2" },
                    },
                },
            ],
        });

        Assert.Null(result.Error);
        Assert.Equal(4, result.Response!.Results.Count);

        // OpId → 真实 UUIDv7 / 版本 / 路径，供 UI 替换临时引用
        var parent = result.Response.Results[0].Strand!;
        var child = result.Response.Results[1].Strand!;
        Assert.Equal("op1", result.Response.Results[0].OpId);
        Assert.Equal(parent.Id, child.ParentStrandId);
        Assert.Equal(["哔哩哔哩实习", "Hyperframes"], child.Path);

        var bound = result.Response.Results[2].Strand!;
        Assert.Equal(child.Id, bound.Id);
        Assert.Single(bound.Members);
        Assert.True(bound.Version > child.Version); // 绑定后版本推进，响应是提交后的真实版本

        var episode = result.Response.Results[3].Episode!;
        Assert.Equal(child.Id, episode.RelatedStrandId);
        Assert.Equal(["哔哩哔哩实习", "Hyperframes"], episode.RelatedStrandPath);

        Assert.Equal(2, await db.Strands.CountAsync());
        Assert.Equal(1, await db.Episodes.CountAsync());
    }

    // ===== 原子性：中途失败整批回滚 =====

    [Fact]
    public async Task MidwayFailure_RollsBackEverything_LocatesFailedOp()
    {
        using var db = CreateDbContext();
        var svc = CreateService(db);

        var result = await svc.CommitAsync("user-1", new CommitChangeSetRequest
        {
            Operations =
            [
                CreateStrandOp("op1", "会成功的脉络"),
                new()
                {
                    OpId = "op2",
                    Type = KnowledgeOpTypes.CreateEpisode,
                    CreateEpisode = new CreateEpisodeOpDto
                    {
                        LocalDate = new DateOnly(2026, 7, 24),
                        Text = "", // InvalidText：领域校验失败
                    },
                },
            ],
        });

        Assert.NotNull(result.Error);
        Assert.Equal("op2", result.Error!.FailedOpId); // 错误定位到具体 operation
        Assert.Equal(EpisodeErrorCodes.InvalidText, result.Error.Error.Code);

        // 无部分写入：op1 的 Strand 也被回滚
        Assert.Equal(0, await db.Strands.CountAsync());
        Assert.Equal(0, await db.Episodes.CountAsync());
    }

    [Fact]
    public async Task DomainInvariants_ReValidatedInsideChangeSet()
    {
        using var db = CreateDbContext();
        var svc = CreateService(db);

        // 同父同名重叠时期：前端/LLM 编辑绕不过服务端不变量
        var result = await svc.CommitAsync("user-1", new CommitChangeSetRequest
        {
            Operations =
            [
                CreateStrandOp("op1", "同名脉络"),
                CreateStrandOp("op2", "同名脉络"),
            ],
        });

        Assert.NotNull(result.Error);
        Assert.Equal("op2", result.Error!.FailedOpId);
        Assert.Equal(KnowledgeErrorCodes.Overlap, result.Error.Error.Code);
        Assert.Equal(0, await db.Strands.CountAsync()); // op1 一并回滚
    }

    // ===== Owner 隔离与虚构 ID =====

    [Fact]
    public async Task ForeignOrFabricatedIds_RejectedByOwnershipChecks()
    {
        using var db = CreateDbContext();
        var knowledge = new KnowledgeService(db);
        var theirs = (await knowledge.CreateStrandAsync("user-2", new CreateStrandRequest { Name = "别人的" })).Strand!;
        var svc = CreateService(db);

        // 跨 Owner 引用
        var foreign = await svc.CommitAsync("user-1", new CommitChangeSetRequest
        {
            Operations =
            [
                new()
                {
                    OpId = "op1",
                    Type = KnowledgeOpTypes.UpdateStrand,
                    UpdateStrand = new UpdateStrandOpDto
                    {
                        StrandId = theirs.Id,
                        ExpectedVersion = theirs.Version,
                        Name = "改名",
                    },
                },
            ],
        });
        Assert.Equal(KnowledgeErrorCodes.NotFound, foreign.Error!.Error.Code);

        // 纯虚构 UUID
        var fabricated = await svc.CommitAsync("user-1", new CommitChangeSetRequest
        {
            Operations =
            [
                new()
                {
                    OpId = "op1",
                    Type = KnowledgeOpTypes.EndStrand,
                    EndStrand = new EndStrandOpDto
                    {
                        StrandId = Guid.CreateVersion7(),
                        ExpectedVersion = 1,
                        EndedOn = new DateOnly(2026, 7, 24),
                    },
                },
            ],
        });
        Assert.Equal(KnowledgeErrorCodes.NotFound, fabricated.Error!.Error.Code);

        // 未被触碰
        var reloaded = await db.Strands.SingleAsync();
        Assert.Equal("别人的", reloaded.Name);
    }

    // ===== 并发版本 =====

    [Fact]
    public async Task StaleVersion_ReturnsConflict_NoLastWriteWins()
    {
        using var db = CreateDbContext();
        var knowledge = new KnowledgeService(db);
        var strand = (await knowledge.CreateStrandAsync("user-1", new CreateStrandRequest { Name = "脉络" })).Strand!;

        // 提案读取后用户又改了一次（版本推进）
        await knowledge.UpdateStrandAsync("user-1", strand.Id, new UpdateStrandRequest
        {
            ExpectedVersion = strand.Version,
            Name = "用户更新的名字",
            Gloss = "",
        });

        var svc = CreateService(db);
        var stale = await svc.CommitAsync("user-1", new CommitChangeSetRequest
        {
            Operations =
            [
                new()
                {
                    OpId = "op1",
                    Type = KnowledgeOpTypes.UpdateStrand,
                    UpdateStrand = new UpdateStrandOpDto
                    {
                        StrandId = strand.Id,
                        ExpectedVersion = strand.Version, // 陈旧版本
                        Name = "陈旧提案的名字",
                    },
                },
            ],
        });

        Assert.Equal("op1", stale.Error!.FailedOpId);
        Assert.Equal(KnowledgeErrorCodes.VersionConflict, stale.Error.Error.Code);
        Assert.Equal("用户更新的名字", (await db.Strands.SingleAsync()).Name); // 新编辑未被覆盖
    }

    [Fact]
    public async Task ModifyingExisting_WithoutVersion_RejectedAtShapeCheck()
    {
        using var db = CreateDbContext();
        var knowledge = new KnowledgeService(db);
        var strand = (await knowledge.CreateStrandAsync("user-1", new CreateStrandRequest { Name = "脉络" })).Strand!;
        var svc = CreateService(db);

        var result = await svc.CommitAsync("user-1", new CommitChangeSetRequest
        {
            Operations =
            [
                new()
                {
                    OpId = "op1",
                    Type = KnowledgeOpTypes.BindMatcher,
                    BindMatcher = new BindMatcherOpDto
                    {
                        Strand = new StrandRefDto { StrandId = strand.Id },
                        ExpectedVersion = null, // 已有对象缺读取时版本
                        Matcher = AppMatcher("x"),
                    },
                },
            ],
        });

        Assert.Equal(ChangeSetErrorCodes.MissingVersion, result.Error!.Error.Code);
    }

    // ===== set 级形状校验 =====

    [Fact]
    public async Task ShapeErrors_EmptySet_DuplicateOpId_ForwardReference_UnknownType()
    {
        using var db = CreateDbContext();
        var svc = CreateService(db);

        var empty = await svc.CommitAsync("user-1", new CommitChangeSetRequest());
        Assert.Equal(ChangeSetErrorCodes.EmptyChangeSet, empty.Error!.Error.Code);

        var duplicate = await svc.CommitAsync("user-1", new CommitChangeSetRequest
        {
            Operations = [CreateStrandOp("op1", "甲"), CreateStrandOp("op1", "乙")],
        });
        Assert.Equal(ChangeSetErrorCodes.DuplicateOpId, duplicate.Error!.Error.Code);

        // 前向引用：op1 引用了排在后面的 op2
        var forward = await svc.CommitAsync("user-1", new CommitChangeSetRequest
        {
            Operations = [CreateStrandOp("op1", "子级", parentOpId: "op2"), CreateStrandOp("op2", "父级")],
        });
        Assert.Equal(ChangeSetErrorCodes.UnresolvedReference, forward.Error!.Error.Code);

        var unknown = await svc.CommitAsync("user-1", new CommitChangeSetRequest
        {
            Operations = [new() { OpId = "op1", Type = "dropTable" }],
        });
        Assert.Equal(ChangeSetErrorCodes.UnknownOpType, unknown.Error!.Error.Code);

        var missingPayload = await svc.CommitAsync("user-1", new CommitChangeSetRequest
        {
            Operations = [new() { OpId = "op1", Type = KnowledgeOpTypes.CreateStrand }],
        });
        Assert.Equal(ChangeSetErrorCodes.MissingPayload, missingPayload.Error!.Error.Code);

        Assert.Equal(0, await db.Strands.CountAsync()); // 形状校验全在事务前，零写入
    }

    // ===== 部分取消：只提交选中的操作 =====

    [Fact]
    public async Task PartialSelection_OnlySubmittedOpsCommit()
    {
        using var db = CreateDbContext();
        var svc = CreateService(db);

        // 提案给了 3 个操作，用户取消了 Episode——提交的 set 里就只有 2 个
        var result = await svc.CommitAsync("user-1", new CommitChangeSetRequest
        {
            Operations =
            [
                CreateStrandOp("op1", "脉络"),
                new()
                {
                    OpId = "op2",
                    Type = KnowledgeOpTypes.BindMatcher,
                    BindMatcher = new BindMatcherOpDto
                    {
                        Strand = new StrandRefDto { OpId = "op1" },
                        Matcher = AppMatcher("tool"),
                    },
                },
            ],
        });

        Assert.Null(result.Error);
        Assert.Equal(1, await db.Strands.CountAsync());
        Assert.Equal(0, await db.Episodes.CountAsync()); // 未确认项没有进库
    }

    // ===== Episode / Probe / 提升链 =====

    [Fact]
    public async Task EpisodeProbeChain_AndPromotion_WorkThroughChangeSets()
    {
        using var db = CreateDbContext();
        var svc = CreateService(db);

        // 第一天：不确定是否持续 → Episode + Probe
        var day1 = await svc.CommitAsync("user-1", new CommitChangeSetRequest
        {
            Operations =
            [
                new()
                {
                    OpId = "op1",
                    Type = KnowledgeOpTypes.CreateEpisode,
                    CreateEpisode = new CreateEpisodeOpDto
                    {
                        LocalDate = new DateOnly(2026, 7, 20),
                        Text = "试了一个陌生工具",
                    },
                },
                new()
                {
                    OpId = "op2",
                    Type = KnowledgeOpTypes.CreateProbe,
                    CreateProbe = new CreateProbeOpDto
                    {
                        Episode = new EpisodeRefDto { OpId = "op1" },
                        Matcher = AppMatcher("strange-tool"),
                    },
                },
            ],
        });
        Assert.Null(day1.Error);
        var episode = day1.Response!.Results[0].Episode!;
        var probe = day1.Response.Results[1].Probe!;
        Assert.Equal(episode.Id, probe.EpisodeId);

        // 复现后：新建脉络并提升（createStrand + promoteEpisode 的 OpId 引用），绑 Probe 谓词
        var day2 = await svc.CommitAsync("user-1", new CommitChangeSetRequest
        {
            Operations =
            [
                CreateStrandOp("op1", "陌生工具研究"),
                new()
                {
                    OpId = "op2",
                    Type = KnowledgeOpTypes.PromoteEpisode,
                    PromoteEpisode = new PromoteEpisodeOpDto
                    {
                        Episode = new EpisodeRefDto { EpisodeId = episode.Id },
                        ExpectedVersion = episode.Version,
                        Strand = new StrandRefDto { OpId = "op1" },
                        ProbeId = probe.Id,
                        BindProbeMatcher = true,
                    },
                },
            ],
        });

        Assert.Null(day2.Error);
        var promotion = day2.Response!.Results[1].Promotion!;
        Assert.Equal(episode.Id, promotion.Episode.Id); // Episode 保留（非破坏性）
        Assert.Equal(promotion.Strand.Id, promotion.Episode.RelatedStrandId);
        Assert.Single(promotion.Strand.Members); // Probe 谓词绑为 Matcher

        var resolvedProbe = await db.RecurrenceProbes.SingleAsync();
        Assert.Equal(ProbeStatuses.Promoted, resolvedProbe.Status);
    }

    [Fact]
    public async Task ResolveProbe_AndMute_ThroughChangeSet()
    {
        using var db = CreateDbContext();
        var knowledge = new KnowledgeService(db);
        var episodes = new EpisodeService(db, knowledge);
        var episode = (await episodes.CreateEpisodeAsync("user-1", new CreateEpisodeRequest
        {
            LocalDate = new DateOnly(2026, 7, 20),
            Text = "一次性的事",
        })).Episode!;
        var probe = (await episodes.CreateProbeAsync("user-1", episode.Id, new CreateProbeRequest
        {
            Matcher = AppMatcher("once-tool"),
        })).Probe!;

        var svc = CreateService(db);
        var result = await svc.CommitAsync("user-1", new CommitChangeSetRequest
        {
            Operations =
            [
                new()
                {
                    OpId = "op1",
                    Type = KnowledgeOpTypes.ResolveProbe,
                    ResolveProbe = new ResolveProbeOpDto { ProbeId = probe.Id, Resolution = "denied" },
                },
                new()
                {
                    OpId = "op2",
                    Type = KnowledgeOpTypes.MuteMatcher,
                    MuteMatcher = new MuteMatcherOpDto { Matcher = AppMatcher("noisy-tool") },
                },
            ],
        });

        Assert.Null(result.Error);
        Assert.Equal(ProbeStatuses.Denied, (await db.RecurrenceProbes.SingleAsync()).Status);
        Assert.Equal(1, await db.MutedMatchers.CountAsync());
    }

    [Fact]
    public async Task MoveAndEndStrand_ThroughChangeSet()
    {
        using var db = CreateDbContext();
        var knowledge = new KnowledgeService(db);
        var parent = (await knowledge.CreateStrandAsync("user-1", new CreateStrandRequest { Name = "父级" })).Strand!;
        var stray = (await knowledge.CreateStrandAsync("user-1", new CreateStrandRequest { Name = "游离节点" })).Strand!;

        var svc = CreateService(db);
        var result = await svc.CommitAsync("user-1", new CommitChangeSetRequest
        {
            Operations =
            [
                new()
                {
                    OpId = "op1",
                    Type = KnowledgeOpTypes.MoveStrand,
                    MoveStrand = new MoveStrandOpDto
                    {
                        StrandId = stray.Id,
                        ExpectedVersion = stray.Version,
                        NewParent = new StrandRefDto { StrandId = parent.Id },
                    },
                },
                new()
                {
                    OpId = "op2",
                    Type = KnowledgeOpTypes.EndStrand,
                    EndStrand = new EndStrandOpDto
                    {
                        StrandId = stray.Id,
                        ExpectedVersion = stray.Version + 1, // op1 推进了版本；编辑器按 set 内顺序可预期
                        EndedOn = new DateOnly(2026, 7, 24),
                    },
                },
            ],
        });

        Assert.Null(result.Error);
        var moved = result.Response!.Results[1].Strand!;
        Assert.Equal(parent.Id, moved.ParentStrandId);
        Assert.Equal(new DateOnly(2026, 7, 24), moved.EndedOn);
        Assert.Equal(["父级", "游离节点"], moved.Path);
    }
}
