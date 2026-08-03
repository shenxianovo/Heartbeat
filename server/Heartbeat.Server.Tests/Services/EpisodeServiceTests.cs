using Heartbeat.Core;
using Heartbeat.Core.DTOs.Knowledge;
using Heartbeat.Server.Services;
using Heartbeat.Server.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Tests.Services;

[Collection("postgres")]
public class EpisodeServiceTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    private static MatcherDto AppMatcher(string app) => new()
    {
        Source = ActivitySources.System,
        Steps = [new() { Reading = "app", Op = MatcherOps.Equal, Value = app }]
    };

    private static MatcherDto UrlContains(string fragment) => new()
    {
        Source = ActivitySources.Browser,
        Steps = [new() { Reading = "url", Op = MatcherOps.Contains, Value = fragment }]
    };

    private static DateOnly D(int m, int d) => new(2026, m, d);

    private static DateTimeOffset T(int m, int d, int h, int offsetHours = 8)
        => new(2026, m, d, h, 0, 0, TimeSpan.FromHours(offsetHours));

    private static CreateEpisodeRequest CreateEpisode(
        string text = "调研了 Hyperframes 的竞品", DateOnly? date = null,
        DateTimeOffset? start = null, DateTimeOffset? end = null, Guid? strandId = null) => new()
    {
        LocalDate = date ?? D(7, 24),
        Text = text,
        ApproximateStart = start,
        ApproximateEnd = end,
        RelatedStrandId = strandId,
    };

    private (EpisodeService Episodes, KnowledgeService Knowledge) Services(Data.AppDbContext db)
    {
        var knowledge = new KnowledgeService(db);
        return (new EpisodeService(db, knowledge), knowledge);
    }

    // ===== Episode create =====

    [Fact]
    public async Task CreateEpisode_GeneratesUuidV7_TrimsText_VersionOne()
    {
        using var db = CreateDbContext();
        var (svc, _) = Services(db);

        var result = await svc.CreateEpisodeAsync("user-1", CreateEpisode(
            text: "  调研了竞品  ", start: T(7, 24, 14), end: T(7, 24, 17)));

        Assert.NotNull(result.Episode);
        Assert.Equal(7, result.Episode.Id.Version); // UUIDv7
        Assert.Equal("调研了竞品", result.Episode.Text);
        Assert.Equal(D(7, 24), result.Episode.LocalDate);
        Assert.Equal(1, result.Episode.Version);
        Assert.Null(result.Episode.RelatedStrandId);
        Assert.Empty(result.Episode.RelatedStrandPath);
    }

    [Fact]
    public async Task CreateEpisode_WithRelatedStrand_ReturnsAncestorPath()
    {
        using var db = CreateDbContext();
        var (svc, knowledge) = Services(db);
        var root = (await knowledge.CreateStrandAsync("user-1", new CreateStrandRequest { Name = "哔哩哔哩实习" })).Strand!;
        var child = (await knowledge.CreateStrandAsync("user-1", new CreateStrandRequest
        { Name = "HyperFrames", ParentStrandId = root.Id })).Strand!;

        var result = await svc.CreateEpisodeAsync("user-1", CreateEpisode(strandId: child.Id));

        Assert.Equal(child.Id, result.Episode!.RelatedStrandId);
        Assert.Equal(["哔哩哔哩实习", "HyperFrames"], result.Episode.RelatedStrandPath);
    }

    [Fact]
    public async Task CreateEpisode_CrossOwnerStrand_Rejected()
    {
        using var db = CreateDbContext();
        var (svc, knowledge) = Services(db);
        var theirs = (await knowledge.CreateStrandAsync("user-2", new CreateStrandRequest { Name = "theirs" })).Strand!;

        var result = await svc.CreateEpisodeAsync("user-1", CreateEpisode(strandId: theirs.Id));

        Assert.Equal(EpisodeErrorCodes.StrandNotFound, result.Error!.Code); // 跨 Owner 不可见，等同不存在
        Assert.Empty(await db.Episodes.ToListAsync());
    }

    [Fact]
    public async Task CreateEpisode_InvalidInputs_Rejected()
    {
        using var db = CreateDbContext();
        var (svc, _) = Services(db);

        Assert.Equal(EpisodeErrorCodes.InvalidText,
            (await svc.CreateEpisodeAsync("user-1", CreateEpisode(text: "  "))).Error!.Code);
        // 起点晚于终点
        Assert.Equal(EpisodeErrorCodes.InvalidTimes,
            (await svc.CreateEpisodeAsync("user-1", CreateEpisode(
                start: T(7, 24, 17), end: T(7, 24, 14)))).Error!.Code);
        // LocalDate 与近似区间不一致
        Assert.Equal(EpisodeErrorCodes.InvalidTimes,
            (await svc.CreateEpisodeAsync("user-1", CreateEpisode(
                date: D(7, 20), start: T(7, 24, 14), end: T(7, 24, 17)))).Error!.Code);
        Assert.Empty(await db.Episodes.ToListAsync());
    }

    [Fact]
    public async Task CreateEpisode_TimeBoundaries_MidnightSpanAndPartialTimes_Accepted()
    {
        using var db = CreateDbContext();
        var (svc, _) = Services(db);

        // 跨午夜：LocalDate 可归属起或止任一叙事日
        Assert.NotNull((await svc.CreateEpisodeAsync("user-1", CreateEpisode(
            date: D(7, 25), start: T(7, 24, 23), end: T(7, 25, 1)))).Episode);
        // 只有起点：不做区间一致性校验（近似时间只服务叙事）
        Assert.NotNull((await svc.CreateEpisodeAsync("user-1", CreateEpisode(
            date: D(7, 24), start: T(7, 24, 14)))).Episode);
    }

    // ===== Episode update / relate / delete =====

    [Fact]
    public async Task UpdateEpisode_ReplacesFields_BumpsVersion_StaleConflicts()
    {
        using var db = CreateDbContext();
        var (svc, _) = Services(db);
        var created = (await svc.CreateEpisodeAsync("user-1", CreateEpisode())).Episode!;

        var updated = await svc.UpdateEpisodeAsync("user-1", created.Id, new UpdateEpisodeRequest
        { ExpectedVersion = created.Version, LocalDate = D(7, 25), Text = "改过的事实" });
        Assert.Equal(2, updated.Episode!.Version);
        Assert.Equal("改过的事实", updated.Episode.Text);

        // 陈旧提案（还带着 Version=1）不得覆盖新编辑
        var stale = await svc.UpdateEpisodeAsync("user-1", created.Id, new UpdateEpisodeRequest
        { ExpectedVersion = created.Version, LocalDate = D(7, 25), Text = "陈旧提案" });
        Assert.Equal(EpisodeErrorCodes.VersionConflict, stale.Error!.Code);
        Assert.Equal("改过的事实", (await db.Episodes.SingleAsync()).Text);
    }

    [Fact]
    public async Task RelateEpisode_SetAndClear_SingleStrandOnly()
    {
        using var db = CreateDbContext();
        var (svc, knowledge) = Services(db);
        var a = (await knowledge.CreateStrandAsync("user-1", new CreateStrandRequest { Name = "A" })).Strand!;
        var b = (await knowledge.CreateStrandAsync("user-1", new CreateStrandRequest { Name = "B" })).Strand!;
        var episode = (await svc.CreateEpisodeAsync("user-1", CreateEpisode(strandId: a.Id))).Episode!;

        // 换绑 = 覆盖唯一关联，不产生多对多
        var rebound = (await svc.RelateEpisodeAsync("user-1", episode.Id, new RelateEpisodeRequest
        { ExpectedVersion = episode.Version, RelatedStrandId = b.Id })).Episode!;
        Assert.Equal(b.Id, rebound.RelatedStrandId);

        var cleared = (await svc.RelateEpisodeAsync("user-1", episode.Id, new RelateEpisodeRequest
        { ExpectedVersion = rebound.Version, RelatedStrandId = null })).Episode!;
        Assert.Null(cleared.RelatedStrandId);
    }

    [Fact]
    public async Task RelateEpisode_CrossOwnerStrand_Rejected()
    {
        using var db = CreateDbContext();
        var (svc, knowledge) = Services(db);
        var theirs = (await knowledge.CreateStrandAsync("user-2", new CreateStrandRequest { Name = "theirs" })).Strand!;
        var episode = (await svc.CreateEpisodeAsync("user-1", CreateEpisode())).Episode!;

        var result = await svc.RelateEpisodeAsync("user-1", episode.Id, new RelateEpisodeRequest
        { ExpectedVersion = episode.Version, RelatedStrandId = theirs.Id });

        Assert.Equal(EpisodeErrorCodes.StrandNotFound, result.Error!.Code);
        Assert.Null((await db.Episodes.SingleAsync()).RelatedStrandId);
    }

    [Fact]
    public async Task DeleteEpisode_CascadesProbes_KeepsStrand_VersionGuarded()
    {
        using var db = CreateDbContext();
        var (svc, knowledge) = Services(db);
        var strand = (await knowledge.CreateStrandAsync("user-1", new CreateStrandRequest { Name = "A" })).Strand!;
        var episode = (await svc.CreateEpisodeAsync("user-1", CreateEpisode(strandId: strand.Id))).Episode!;
        await svc.CreateProbeAsync("user-1", episode.Id, new CreateProbeRequest { Matcher = AppMatcher("blender.exe") });

        Assert.Equal(EpisodeErrorCodes.VersionConflict,
            (await svc.DeleteEpisodeAsync("user-1", episode.Id, expectedVersion: 99))!.Code);

        Assert.Null(await svc.DeleteEpisodeAsync("user-1", episode.Id, episode.Version));
        Assert.Empty(await db.Episodes.ToListAsync());
        Assert.Empty(await db.RecurrenceProbes.ToListAsync()); // 级联
        Assert.Single(await db.Strands.ToListAsync());          // 关联 Strand 不动
    }

    [Fact]
    public async Task GetEpisodes_FiltersByDateAndStrand_OwnerIsolated()
    {
        using var db = CreateDbContext();
        var (svc, knowledge) = Services(db);
        var strand = (await knowledge.CreateStrandAsync("user-1", new CreateStrandRequest { Name = "A" })).Strand!;
        await svc.CreateEpisodeAsync("user-1", CreateEpisode(date: D(7, 24), strandId: strand.Id));
        await svc.CreateEpisodeAsync("user-1", CreateEpisode(date: D(7, 25)));
        await svc.CreateEpisodeAsync("user-2", CreateEpisode(date: D(7, 24)));

        Assert.Equal(2, (await svc.GetEpisodesAsync("user-1")).Count);
        Assert.Single(await svc.GetEpisodesAsync("user-1", date: D(7, 24)));
        Assert.Single(await svc.GetEpisodesAsync("user-1", strandId: strand.Id));
        Assert.Empty(await svc.GetEpisodesAsync("user-3"));
    }

    // ===== Probe lifecycle =====

    [Fact]
    public async Task CreateProbe_CanonicalIdentity_IdempotentWhileActive()
    {
        using var db = CreateDbContext();
        var (svc, _) = Services(db);
        var episode = (await svc.CreateEpisodeAsync("user-1", CreateEpisode())).Episode!;

        var first = (await svc.CreateProbeAsync("user-1", episode.Id, new CreateProbeRequest
        { Matcher = AppMatcher("Blender.EXE") })).Probe!;
        Assert.Equal(7, first.Id.Version);
        Assert.Equal(ProbeStatuses.Active, first.Status);
        Assert.Equal("blender.exe", Assert.Single(first.Matcher.Steps).Value); // canonical 小写形

        // 大小写/空白变体 → 同一 canonical 谓词，幂等返回既有活跃行
        var again = (await svc.CreateProbeAsync("user-1", episode.Id, new CreateProbeRequest
        { Matcher = AppMatcher(" blender.exe ") })).Probe!;
        Assert.Equal(first.Id, again.Id);
        Assert.Single(await db.RecurrenceProbes.ToListAsync());
    }

    [Fact]
    public async Task CreateProbe_AfterResolution_RejectedNoReAsk()
    {
        using var db = CreateDbContext();
        var (svc, _) = Services(db);
        var episode = (await svc.CreateEpisodeAsync("user-1", CreateEpisode())).Episode!;
        var probe = (await svc.CreateProbeAsync("user-1", episode.Id, new CreateProbeRequest
        { Matcher = AppMatcher("blender.exe") })).Probe!;
        await svc.ResolveProbeAsync("user-1", probe.Id, new ResolveProbeRequest { Resolution = "denied" });

        // 任何解决结果都钉住该谓词：不允许重开活跃 Probe 重复发问
        var reopened = await svc.CreateProbeAsync("user-1", episode.Id, new CreateProbeRequest
        { Matcher = AppMatcher("blender.exe") });

        Assert.Equal(EpisodeErrorCodes.ProbeResolved, reopened.Error!.Code);
        Assert.Single(await db.RecurrenceProbes.ToListAsync());
    }

    [Fact]
    public async Task CreateProbe_InvalidMatcher_CrossOwnerEpisode_Rejected()
    {
        using var db = CreateDbContext();
        var (svc, _) = Services(db);
        var episode = (await svc.CreateEpisodeAsync("user-1", CreateEpisode())).Episode!;

        Assert.Equal(EpisodeErrorCodes.InvalidMatcher,
            (await svc.CreateProbeAsync("user-1", episode.Id, new CreateProbeRequest
            { Matcher = new MatcherDto { Source = "system", Steps = [] } })).Error!.Code);
        Assert.Equal(EpisodeErrorCodes.NotFound,
            (await svc.CreateProbeAsync("user-2", episode.Id, new CreateProbeRequest
            { Matcher = AppMatcher("blender.exe") })).Error!.Code);
        Assert.Empty(await db.RecurrenceProbes.ToListAsync());
    }

    [Fact]
    public async Task ResolveProbe_DeniedOrMuted_TerminalNoReResolve()
    {
        using var db = CreateDbContext();
        var (svc, _) = Services(db);
        var episode = (await svc.CreateEpisodeAsync("user-1", CreateEpisode())).Episode!;
        var probe = (await svc.CreateProbeAsync("user-1", episode.Id, new CreateProbeRequest
        { Matcher = AppMatcher("blender.exe") })).Probe!;

        Assert.Equal(EpisodeErrorCodes.InvalidResolution,
            (await svc.ResolveProbeAsync("user-1", probe.Id, new ResolveProbeRequest
            { Resolution = "promoted" })).Error!.Code); // promoted 只由提升事务写

        var denied = (await svc.ResolveProbeAsync("user-1", probe.Id, new ResolveProbeRequest
        { Resolution = "Denied" })).Probe!;
        Assert.Equal(ProbeStatuses.Denied, denied.Status);
        Assert.NotNull(denied.ResolvedAt);

        Assert.Equal(EpisodeErrorCodes.ProbeResolved,
            (await svc.ResolveProbeAsync("user-1", probe.Id, new ResolveProbeRequest
            { Resolution = "muted" })).Error!.Code); // 已解决不可改判
    }

    [Fact]
    public async Task GetActiveProbes_ExcludesResolved_OwnerIsolated()
    {
        using var db = CreateDbContext();
        var (svc, _) = Services(db);
        var mine = (await svc.CreateEpisodeAsync("user-1", CreateEpisode())).Episode!;
        var theirs = (await svc.CreateEpisodeAsync("user-2", CreateEpisode())).Episode!;
        var active = (await svc.CreateProbeAsync("user-1", mine.Id, new CreateProbeRequest
        { Matcher = AppMatcher("blender.exe") })).Probe!;
        var resolved = (await svc.CreateProbeAsync("user-1", mine.Id, new CreateProbeRequest
        { Matcher = UrlContains("localhost:5173") })).Probe!;
        await svc.ResolveProbeAsync("user-1", resolved.Id, new ResolveProbeRequest { Resolution = "muted" });
        await svc.CreateProbeAsync("user-2", theirs.Id, new CreateProbeRequest { Matcher = AppMatcher("code.exe") });

        var probes = await svc.GetActiveProbesAsync("user-1");

        Assert.Equal(active.Id, Assert.Single(probes).Id);
    }

    // ===== Promotion =====

    [Fact]
    public async Task Promote_NewStrand_KeepsEpisode_BindsMatcher_ResolvesProbe_Atomically()
    {
        using var db = CreateDbContext();
        var (svc, _) = Services(db);
        var episode = (await svc.CreateEpisodeAsync("user-1", CreateEpisode())).Episode!;
        var probe = (await svc.CreateProbeAsync("user-1", episode.Id, new CreateProbeRequest
        { Matcher = UrlContains("localhost:5173") })).Probe!;

        var result = await svc.PromoteEpisodeAsync("user-1", episode.Id, new PromoteEpisodeRequest
        {
            ExpectedVersion = episode.Version,
            NewStrand = new CreateStrandRequest { Name = "HyperFrames", Gloss = "AI 动效框架" },
            ProbeId = probe.Id,
            BindProbeMatcher = true,
        });

        Assert.NotNull(result.Promotion);
        var promoted = result.Promotion;
        // Episode 保留且关联到新 Strand——不是类型转换
        Assert.Equal(episode.Id, promoted.Episode.Id);
        Assert.Equal(promoted.Strand.Id, promoted.Episode.RelatedStrandId);
        Assert.Equal("调研了 Hyperframes 的竞品", promoted.Episode.Text);
        // Probe 谓词成为 Strand Matcher；Probe 解决为 promoted
        var member = Assert.Single(promoted.Strand.Members);
        Assert.Equal("localhost:5173", Assert.Single(member.Steps).Value);
        Assert.Equal(ProbeStatuses.Promoted, Assert.Single(promoted.Episode.Probes).Status);
    }

    [Fact]
    public async Task Promote_ExistingStrand_ById_MatcherConvergesIfPresent()
    {
        using var db = CreateDbContext();
        var (svc, knowledge) = Services(db);
        var strand = (await knowledge.CreateStrandAsync("user-1", new CreateStrandRequest
        { Name = "HyperFrames", Members = [UrlContains("localhost:5173")] })).Strand!;
        var episode = (await svc.CreateEpisodeAsync("user-1", CreateEpisode())).Episode!;
        var probe = (await svc.CreateProbeAsync("user-1", episode.Id, new CreateProbeRequest
        { Matcher = UrlContains("LOCALHOST:5173") })).Probe!; // canonical 同形

        var result = await svc.PromoteEpisodeAsync("user-1", episode.Id, new PromoteEpisodeRequest
        {
            ExpectedVersion = episode.Version,
            ExistingStrandId = strand.Id,
            ProbeId = probe.Id,
            BindProbeMatcher = true,
        });

        Assert.NotNull(result.Promotion);
        Assert.Equal(strand.Id, result.Promotion.Episode.RelatedStrandId);
        Assert.Single(result.Promotion.Strand.Members); // canonical 已存在 → 收敛，不重复不报错
        Assert.Single(await db.StrandMatchers.ToListAsync());
    }

    [Fact]
    public async Task Promote_NewStrandViolatesConstraint_RollsBackWholeBatch()
    {
        using var db = CreateDbContext();
        var (svc, knowledge) = Services(db);
        // 既有同名无界 Strand：新建必然 overlap
        await knowledge.CreateStrandAsync("user-1", new CreateStrandRequest { Name = "HyperFrames" });
        var episode = (await svc.CreateEpisodeAsync("user-1", CreateEpisode())).Episode!;
        var probe = (await svc.CreateProbeAsync("user-1", episode.Id, new CreateProbeRequest
        { Matcher = UrlContains("localhost:5173") })).Probe!;

        var result = await svc.PromoteEpisodeAsync("user-1", episode.Id, new PromoteEpisodeRequest
        {
            ExpectedVersion = episode.Version,
            NewStrand = new CreateStrandRequest { Name = "hyperframes" },
            ProbeId = probe.Id,
            BindProbeMatcher = true,
        });

        Assert.Equal(KnowledgeErrorCodes.Overlap, result.Error!.Code);
        // 整批回滚：无新 Strand、Episode 未关联、Probe 仍活跃
        Assert.Single(await db.Strands.ToListAsync());
        Assert.Null((await db.Episodes.SingleAsync()).RelatedStrandId);
        Assert.Equal(ProbeStatuses.Active, (await db.RecurrenceProbes.SingleAsync()).Status);
    }

    [Fact]
    public async Task Promote_InvalidShapes_Rejected()
    {
        using var db = CreateDbContext();
        var (svc, knowledge) = Services(db);
        var strand = (await knowledge.CreateStrandAsync("user-1", new CreateStrandRequest { Name = "A" })).Strand!;
        var episode = (await svc.CreateEpisodeAsync("user-1", CreateEpisode())).Episode!;

        // 两个目标都给 / 都不给
        Assert.Equal(EpisodeErrorCodes.InvalidPromotion,
            (await svc.PromoteEpisodeAsync("user-1", episode.Id, new PromoteEpisodeRequest
            { ExpectedVersion = 1, ExistingStrandId = strand.Id, NewStrand = new CreateStrandRequest { Name = "B" } })).Error!.Code);
        Assert.Equal(EpisodeErrorCodes.InvalidPromotion,
            (await svc.PromoteEpisodeAsync("user-1", episode.Id, new PromoteEpisodeRequest
            { ExpectedVersion = 1 })).Error!.Code);
        // 绑 Matcher 但没给 Probe
        Assert.Equal(EpisodeErrorCodes.InvalidPromotion,
            (await svc.PromoteEpisodeAsync("user-1", episode.Id, new PromoteEpisodeRequest
            { ExpectedVersion = 1, ExistingStrandId = strand.Id, BindProbeMatcher = true })).Error!.Code);
        // 陈旧 Episode 版本
        Assert.Equal(EpisodeErrorCodes.VersionConflict,
            (await svc.PromoteEpisodeAsync("user-1", episode.Id, new PromoteEpisodeRequest
            { ExpectedVersion = 99, ExistingStrandId = strand.Id })).Error!.Code);
        // 跨 Owner Episode
        Assert.Equal(EpisodeErrorCodes.NotFound,
            (await svc.PromoteEpisodeAsync("user-2", episode.Id, new PromoteEpisodeRequest
            { ExpectedVersion = 1, ExistingStrandId = strand.Id })).Error!.Code);
    }

    [Fact]
    public async Task Promote_AlreadyResolvedProbe_Rejected()
    {
        using var db = CreateDbContext();
        var (svc, knowledge) = Services(db);
        var strand = (await knowledge.CreateStrandAsync("user-1", new CreateStrandRequest { Name = "A" })).Strand!;
        var episode = (await svc.CreateEpisodeAsync("user-1", CreateEpisode())).Episode!;
        var probe = (await svc.CreateProbeAsync("user-1", episode.Id, new CreateProbeRequest
        { Matcher = AppMatcher("blender.exe") })).Probe!;
        await svc.ResolveProbeAsync("user-1", probe.Id, new ResolveProbeRequest { Resolution = "denied" });

        var result = await svc.PromoteEpisodeAsync("user-1", episode.Id, new PromoteEpisodeRequest
        { ExpectedVersion = episode.Version, ExistingStrandId = strand.Id, ProbeId = probe.Id });

        Assert.Equal(EpisodeErrorCodes.ProbeResolved, result.Error!.Code);
        Assert.Null((await db.Episodes.SingleAsync()).RelatedStrandId);
    }

    [Fact]
    public async Task Promote_DoesNotAutoRelateOtherEpisodes()
    {
        using var db = CreateDbContext();
        var (svc, _) = Services(db);
        var promotedEpisode = (await svc.CreateEpisodeAsync("user-1", CreateEpisode())).Episode!;
        var bystander = (await svc.CreateEpisodeAsync("user-1", CreateEpisode(
            text: "另一天的类似发生", date: D(7, 20)))).Episode!;

        var result = await svc.PromoteEpisodeAsync("user-1", promotedEpisode.Id, new PromoteEpisodeRequest
        { ExpectedVersion = promotedEpisode.Version, NewStrand = new CreateStrandRequest { Name = "HyperFrames" } });

        Assert.NotNull(result.Promotion);
        var untouched = await db.Episodes.SingleAsync(e => e.Id == bystander.Id);
        Assert.Null(untouched.RelatedStrandId); // 历史 Episode 不被自动关联
        Assert.Equal(1, untouched.Version);
    }

    // ===== 负向自动化边界（ADR-031 §4/§5）=====

    [Fact]
    public async Task ProbeHit_ProducesCandidateOnly_WritesNothing()
    {
        using var db = CreateDbContext();
        var (svc, _) = Services(db);
        var episode = (await svc.CreateEpisodeAsync("user-1", CreateEpisode())).Episode!;
        await svc.CreateProbeAsync("user-1", episode.Id, new CreateProbeRequest
        { Matcher = AppMatcher("blender.exe") });

        // 模拟 Asking 侧的命中求值：读活跃 Probe + MatcherEval——只产生候选
        var probes = await svc.GetActiveProbesAsync("user-1");
        var hit = probes.Single(p => MatcherEval.Hits(
            ActivitySources.System, [new DepthReading(1, "app", "blender.exe")], p.Matcher));
        Assert.NotNull(hit);

        // 命中本身不创建 Episode / Strand / 关联，也不解决 Probe
        Assert.Single(await db.Episodes.ToListAsync());
        Assert.Empty(await db.Strands.ToListAsync());
        Assert.Null((await db.Episodes.SingleAsync()).RelatedStrandId);
        Assert.Equal(ProbeStatuses.Active, (await db.RecurrenceProbes.SingleAsync()).Status);
    }

    [Fact]
    public async Task MatcherHit_DoesNotCreateEpisode()
    {
        using var db = CreateDbContext();
        var (svc, knowledge) = Services(db);
        var strand = (await knowledge.CreateStrandAsync("user-1", new CreateStrandRequest
        { Name = "HyperFrames", Members = [AppMatcher("blender.exe")] })).Strand!;

        // Strand Matcher 命中当日观测：检索触发器，不制造"今天做了 X"的 Episode
        var hits = strand.Members.Any(m => MatcherEval.Hits(
            ActivitySources.System, [new DepthReading(1, "app", "blender.exe")], m));
        Assert.True(hits);

        Assert.Empty(await db.Episodes.ToListAsync());
        Assert.Empty(await db.RecurrenceProbes.ToListAsync());
    }
}
