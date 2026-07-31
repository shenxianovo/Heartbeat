using Heartbeat.Core;
using Heartbeat.Core.DTOs.Knowledge;
using Heartbeat.Server.Services;
using Heartbeat.Server.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Tests.Services;

[Collection("postgres")]
public class KnowledgeServiceTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
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

    private static CreateStrandRequest Create(
        string name, Guid? parentId = null, DateOnly? start = null, DateOnly? end = null,
        List<MatcherDto>? members = null) => new()
    {
        Name = name,
        Gloss = "",
        ParentStrandId = parentId,
        StartedOn = start,
        EndedOn = end,
        Members = members ?? [],
    };

    private static DateOnly D(int m, int d) => new(2026, m, d);

    // ===== Create =====

    [Fact]
    public async Task Create_TopLevel_DedupesMembers_GeneratesUuidV7()
    {
        using var db = CreateDbContext();
        var svc = new KnowledgeService(db);

        var request = Create("HyperFrames", members:
            [UrlContains("localhost:5173"), AppMatcher("blender.exe"), AppMatcher("BLENDER.EXE"), AppMatcher(" ")]);
        var result = await svc.CreateStrandAsync("user-1", request);

        Assert.NotNull(result.Strand);
        Assert.Equal(7, result.Strand.Id.Version); // UUIDv7
        Assert.Null(result.Strand.ParentStrandId);
        Assert.Equal(["HyperFrames"], result.Strand.Path);
        Assert.Equal(1, result.Strand.Version);
        Assert.Equal(2, result.Strand.Members.Count); // 大小写变体收敛、无效步剔除

        var matcherRow = (await db.StrandMatchers.ToListAsync()).First();
        Assert.Equal(7, matcherRow.Id.Version); // 成员也是应用层 UUIDv7
    }

    [Fact]
    public async Task Create_Child_InheritsPath_ParentMayHaveZeroMatchers()
    {
        using var db = CreateDbContext();
        var svc = new KnowledgeService(db);

        var internship = (await svc.CreateStrandAsync("user-1",
            Create("哔哩哔哩实习", start: D(6, 1)))).Strand!;
        var project = (await svc.CreateStrandAsync("user-1",
            Create("HyperFrames", internship.Id, start: D(6, 15)))).Strand!;
        var research = (await svc.CreateStrandAsync("user-1",
            Create("产品调研与可行性分析", project.Id, start: D(7, 1)))).Strand!;

        Assert.Equal(["哔哩哔哩实习", "HyperFrames", "产品调研与可行性分析"], research.Path);
        Assert.Empty(internship.Members); // 父可零 Matcher，纯语境容器
    }

    [Fact]
    public async Task Create_SameNameNonOverlappingPeriods_Coexist_OverlapRejected()
    {
        using var db = CreateDbContext();
        var svc = new KnowledgeService(db);

        var spring = await svc.CreateStrandAsync("user-1", Create("实习", start: D(1, 1), end: D(3, 31)));
        Assert.NotNull(spring.Strand);

        // 相邻但不重叠（4/1 起）：合法
        var summer = await svc.CreateStrandAsync("user-1", Create("实习", start: D(4, 1), end: D(6, 30)));
        Assert.NotNull(summer.Strand);

        // 与春季重叠：拒绝，且冲突清单指向重叠方
        var overlap = await svc.CreateStrandAsync("user-1", Create("实习", start: D(3, 31), end: D(5, 1)));
        Assert.Equal(KnowledgeErrorCodes.Overlap, overlap.Error!.Code);
        Assert.Contains(overlap.Error.Strands, s => s.Id == spring.Strand!.Id);
    }

    [Fact]
    public async Task Create_UnknownEndpointsAreUnbounded_ForOverlap()
    {
        using var db = CreateDbContext();
        var svc = new KnowledgeService(db);

        // 无起点无终点 = 双向无界，与任何同名时期都重叠
        Assert.NotNull((await svc.CreateStrandAsync("user-1", Create("读书", start: D(5, 1)))).Strand);
        var unbounded = await svc.CreateStrandAsync("user-1", Create("读书"));
        Assert.Equal(KnowledgeErrorCodes.Overlap, unbounded.Error!.Code);

        // 只有终点（向过去无界）但终点早于既有起点：不重叠
        Assert.NotNull((await svc.CreateStrandAsync("user-1", Create("读书", end: D(4, 30)))).Strand);
    }

    [Fact]
    public async Task Create_ChildDatesOutsideParentKnownRange_Rejected()
    {
        using var db = CreateDbContext();
        var svc = new KnowledgeService(db);
        var parent = (await svc.CreateStrandAsync("user-1", Create("实习", start: D(6, 1), end: D(9, 30)))).Strand!;

        var early = await svc.CreateStrandAsync("user-1", Create("项目", parent.Id, start: D(5, 1)));
        Assert.Equal(KnowledgeErrorCodes.OutsideParentRange, early.Error!.Code);

        // 部分已知：子只有终点，落在父范围内 → 合法（未知起点不构成越界）
        var partial = await svc.CreateStrandAsync("user-1", Create("项目", parent.Id, end: D(8, 1)));
        Assert.NotNull(partial.Strand);
    }

    [Fact]
    public async Task Create_InvalidInputs_Rejected()
    {
        using var db = CreateDbContext();
        var svc = new KnowledgeService(db);

        Assert.Equal(KnowledgeErrorCodes.InvalidName,
            (await svc.CreateStrandAsync("user-1", Create("  "))).Error!.Code);
        Assert.Equal(KnowledgeErrorCodes.InvalidDates,
            (await svc.CreateStrandAsync("user-1", Create("x", start: D(7, 2), end: D(7, 1)))).Error!.Code);
        Assert.Equal(KnowledgeErrorCodes.ParentNotFound,
            (await svc.CreateStrandAsync("user-1", Create("x", Guid.CreateVersion7()))).Error!.Code);
        Assert.Empty(await db.Strands.ToListAsync());
    }

    [Fact]
    public async Task Create_CrossOwnerParent_Rejected()
    {
        using var db = CreateDbContext();
        var svc = new KnowledgeService(db);
        var theirs = (await svc.CreateStrandAsync("user-2", Create("their-root"))).Strand!;

        var result = await svc.CreateStrandAsync("user-1", Create("mine", theirs.Id));

        Assert.Equal(KnowledgeErrorCodes.ParentNotFound, result.Error!.Code); // 跨 Owner 不可见，等同不存在
    }

    // ===== Update =====

    [Fact]
    public async Task Update_ById_ReplacesFieldsAndMembers_BumpsVersion()
    {
        using var db = CreateDbContext();
        var svc = new KnowledgeService(db);
        var created = (await svc.CreateStrandAsync("user-1",
            Create("HyperFrames", members: [UrlContains("localhost:5173")]))).Strand!;

        var result = await svc.UpdateStrandAsync("user-1", created.Id, new UpdateStrandRequest
        {
            ExpectedVersion = created.Version,
            Name = "HyperFrames v2",
            Gloss = "改过的释义",
            StartedOn = D(6, 1),
            Members = [AppMatcher("AfterFX.exe")],
        });

        Assert.NotNull(result.Strand);
        Assert.Equal(created.Id, result.Strand.Id);
        Assert.Equal("HyperFrames v2", result.Strand.Name);
        Assert.Equal(2, result.Strand.Version);
        var member = Assert.Single(result.Strand.Members);
        Assert.Equal("afterfx.exe", Assert.Single(member.Steps).Value); // canonical 小写形
        Assert.Single(await db.StrandMatchers.ToListAsync()); // 整组替换无残留
    }

    [Fact]
    public async Task Update_StaleVersion_ConflictsWithoutOverwrite()
    {
        using var db = CreateDbContext();
        var svc = new KnowledgeService(db);
        var created = (await svc.CreateStrandAsync("user-1", Create("花生"))).Strand!;

        var fresh = await svc.UpdateStrandAsync("user-1", created.Id, new UpdateStrandRequest
        { ExpectedVersion = created.Version, Name = "花生", Gloss = "新编辑" });
        Assert.NotNull(fresh.Strand);

        // 陈旧提案（还带着 Version=1）不得覆盖新编辑
        var stale = await svc.UpdateStrandAsync("user-1", created.Id, new UpdateStrandRequest
        { ExpectedVersion = created.Version, Name = "花生", Gloss = "陈旧提案" });

        Assert.Equal(KnowledgeErrorCodes.VersionConflict, stale.Error!.Code);
        Assert.Equal("新编辑", (await db.Strands.SingleAsync()).Gloss);
    }

    [Fact]
    public async Task Update_ShrinkingRangeBelowChildren_Rejected()
    {
        using var db = CreateDbContext();
        var svc = new KnowledgeService(db);
        var parent = (await svc.CreateStrandAsync("user-1", Create("实习", start: D(6, 1), end: D(9, 30)))).Strand!;
        var child = (await svc.CreateStrandAsync("user-1", Create("项目", parent.Id, start: D(8, 1)))).Strand!;

        var result = await svc.UpdateStrandAsync("user-1", parent.Id, new UpdateStrandRequest
        { ExpectedVersion = parent.Version, Name = "实习", Gloss = "", StartedOn = D(6, 1), EndedOn = D(7, 31) });

        Assert.Equal(KnowledgeErrorCodes.ChildrenOutsideRange, result.Error!.Code);
        Assert.Contains(result.Error.Strands, s => s.Id == child.Id);
    }

    [Fact]
    public async Task Update_CrossOwner_NotFound()
    {
        using var db = CreateDbContext();
        var svc = new KnowledgeService(db);
        var created = (await svc.CreateStrandAsync("user-1", Create("HyperFrames"))).Strand!;

        var result = await svc.UpdateStrandAsync("user-2", created.Id, new UpdateStrandRequest
        { ExpectedVersion = created.Version, Name = "stolen", Gloss = "" });

        Assert.Equal(KnowledgeErrorCodes.NotFound, result.Error!.Code);
        Assert.Equal("HyperFrames", (await db.Strands.SingleAsync()).Name);
    }

    // ===== Move =====

    [Fact]
    public async Task Move_ToDescendantOrSelf_RejectedAsCycle()
    {
        using var db = CreateDbContext();
        var svc = new KnowledgeService(db);
        var root = (await svc.CreateStrandAsync("user-1", Create("root"))).Strand!;
        var mid = (await svc.CreateStrandAsync("user-1", Create("mid", root.Id))).Strand!;
        var leaf = (await svc.CreateStrandAsync("user-1", Create("leaf", mid.Id))).Strand!;

        var toLeaf = await svc.MoveStrandAsync("user-1", root.Id, new MoveStrandRequest
        { ExpectedVersion = root.Version, NewParentStrandId = leaf.Id });
        Assert.Equal(KnowledgeErrorCodes.Cycle, toLeaf.Error!.Code);

        var toSelf = await svc.MoveStrandAsync("user-1", mid.Id, new MoveStrandRequest
        { ExpectedVersion = mid.Version, NewParentStrandId = mid.Id });
        Assert.Equal(KnowledgeErrorCodes.Cycle, toSelf.Error!.Code);
    }

    [Fact]
    public async Task Move_ValidReparent_UpdatesPath_MoveToTopLevel()
    {
        using var db = CreateDbContext();
        var svc = new KnowledgeService(db);
        var a = (await svc.CreateStrandAsync("user-1", Create("A"))).Strand!;
        var b = (await svc.CreateStrandAsync("user-1", Create("B"))).Strand!;
        var child = (await svc.CreateStrandAsync("user-1", Create("child", a.Id))).Strand!;

        var moved = (await svc.MoveStrandAsync("user-1", child.Id, new MoveStrandRequest
        { ExpectedVersion = child.Version, NewParentStrandId = b.Id })).Strand!;
        Assert.Equal(["B", "child"], moved.Path);

        var top = (await svc.MoveStrandAsync("user-1", child.Id, new MoveStrandRequest
        { ExpectedVersion = moved.Version, NewParentStrandId = null })).Strand!;
        Assert.Null(top.ParentStrandId);
        Assert.Equal(["child"], top.Path);
    }

    [Fact]
    public async Task Move_CrossOwnerParent_Rejected()
    {
        using var db = CreateDbContext();
        var svc = new KnowledgeService(db);
        var mine = (await svc.CreateStrandAsync("user-1", Create("mine"))).Strand!;
        var theirs = (await svc.CreateStrandAsync("user-2", Create("theirs"))).Strand!;

        var result = await svc.MoveStrandAsync("user-1", mine.Id, new MoveStrandRequest
        { ExpectedVersion = mine.Version, NewParentStrandId = theirs.Id });

        Assert.Equal(KnowledgeErrorCodes.ParentNotFound, result.Error!.Code);
    }

    // ===== End =====

    [Fact]
    public async Task End_WithActiveChildren_ExplicitConflictNoCascade()
    {
        using var db = CreateDbContext();
        var svc = new KnowledgeService(db);
        var parent = (await svc.CreateStrandAsync("user-1", Create("实习", start: D(6, 1)))).Strand!;
        var active = (await svc.CreateStrandAsync("user-1", Create("项目", parent.Id))).Strand!;

        var result = await svc.EndStrandAsync("user-1", parent.Id, new EndStrandRequest
        { ExpectedVersion = parent.Version, EndedOn = D(9, 30) });

        Assert.Equal(KnowledgeErrorCodes.ActiveChildren, result.Error!.Code);
        Assert.Contains(result.Error.Strands, s => s.Id == active.Id); // 冲突清单供 UI 引导
        Assert.Null((await db.Strands.SingleAsync(s => s.Id == parent.Id)).EndedOn); // 未静默级联
    }

    [Fact]
    public async Task End_AfterChildrenEnded_Succeeds()
    {
        using var db = CreateDbContext();
        var svc = new KnowledgeService(db);
        var parent = (await svc.CreateStrandAsync("user-1", Create("实习", start: D(6, 1)))).Strand!;
        var child = (await svc.CreateStrandAsync("user-1", Create("项目", parent.Id))).Strand!;

        Assert.NotNull((await svc.EndStrandAsync("user-1", child.Id, new EndStrandRequest
        { ExpectedVersion = child.Version, EndedOn = D(9, 15) })).Strand);

        var ended = (await svc.EndStrandAsync("user-1", parent.Id, new EndStrandRequest
        { ExpectedVersion = parent.Version, EndedOn = D(9, 30) })).Strand!;
        Assert.Equal(D(9, 30), ended.EndedOn);
    }

    [Fact]
    public async Task End_BeforeStart_Rejected()
    {
        using var db = CreateDbContext();
        var svc = new KnowledgeService(db);
        var strand = (await svc.CreateStrandAsync("user-1", Create("实习", start: D(6, 1)))).Strand!;

        var result = await svc.EndStrandAsync("user-1", strand.Id, new EndStrandRequest
        { ExpectedVersion = strand.Version, EndedOn = D(5, 1) });

        Assert.Equal(KnowledgeErrorCodes.InvalidDates, result.Error!.Code);
    }

    // ===== Tree read =====

    [Fact]
    public async Task GetStrands_ReturnsStablePathsAndParentIds_OwnerIsolated()
    {
        using var db = CreateDbContext();
        var svc = new KnowledgeService(db);
        var root = (await svc.CreateStrandAsync("user-1", Create("root"))).Strand!;
        var child = (await svc.CreateStrandAsync("user-1", Create("child", root.Id))).Strand!;
        await svc.CreateStrandAsync("user-2", Create("other-owner"));

        var tree = await svc.GetStrandsAsync("user-1");

        Assert.Equal(2, tree.Count);
        var childNode = tree.Single(s => s.Id == child.Id);
        Assert.Equal(root.Id, childNode.ParentStrandId);
        Assert.Equal(["root", "child"], childNode.Path);
    }

    // ===== Mute（行为随 ADR-029 不变，Id 换 UUIDv7）=====

    [Fact]
    public async Task Mute_IsIdempotent_StepOrderInsensitive_UuidV7Identity()
    {
        using var db = CreateDbContext();
        var svc = new KnowledgeService(db);

        var forward = new MatcherDto
        {
            Source = ActivitySources.System,
            Steps =
            [
                new() { Reading = "app", Op = MatcherOps.Equal, Value = "Code" },
                new() { Reading = "title", Op = MatcherOps.Contains, Value = "news" },
            ]
        };
        var reversed = new MatcherDto
        {
            Source = ActivitySources.System,
            Steps =
            [
                new() { Reading = "title", Op = "CONTAINS", Value = " news " },
                new() { Reading = "app", Op = MatcherOps.Equal, Value = "CODE" },
            ]
        };

        Assert.True(await svc.MuteMatcherAsync("user-1", forward));
        Assert.True(await svc.MuteMatcherAsync("user-1", reversed)); // 步骤换序 + 值大小写/空白差异 → 同一裁决

        var row = Assert.Single(await db.MutedMatchers.ToListAsync());
        Assert.Equal("user-1", row.OwnerId);
        Assert.Equal(7, row.Id.Version);
    }

    [Fact]
    public async Task Mute_OwnersAreIsolated()
    {
        using var db = CreateDbContext();
        var svc = new KnowledgeService(db);

        Assert.True(await svc.MuteMatcherAsync("user-1", UrlContains("news.example.com")));
        Assert.True(await svc.MuteMatcherAsync("user-2", UrlContains("news.example.com")));

        Assert.Equal(2, (await db.MutedMatchers.ToListAsync()).Count);
    }

    [Fact]
    public async Task Mute_InvalidMatcher_ReturnsFalseWritesNothing()
    {
        using var db = CreateDbContext();
        var svc = new KnowledgeService(db);

        Assert.False(await svc.MuteMatcherAsync("user-1", new MatcherDto { Source = "system", Steps = [] }));
        Assert.Empty(await db.MutedMatchers.ToListAsync());
    }
}
