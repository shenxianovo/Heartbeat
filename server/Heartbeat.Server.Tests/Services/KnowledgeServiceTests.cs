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
        Steps = [new() { Layer = 1, Reading = "app", Op = MatcherOps.Equal, Value = app }]
    };

    private static MatcherDto UrlContains(string fragment) => new()
    {
        Source = ActivitySources.Browser,
        Steps = [new() { Layer = 1, Reading = "url", Op = MatcherOps.Contains, Value = fragment }]
    };

    private static BindStrandRequest HyperFrames(Guid? id = null) => new()
    {
        Id = id,
        Name = "HyperFrames",
        Gloss = "我在搞的 AI 动效框架",
        Members =
        [
            UrlContains("localhost:5173"),
            AppMatcher("blender.exe"),
        ]
    };

    [Fact]
    public async Task Bind_CreatesStrandWithDedupedMembers()
    {
        using var db = CreateDbContext();
        var svc = new KnowledgeService(db);

        var request = HyperFrames();
        request.Members.Add(AppMatcher("blender.exe")); // 重复成员
        request.Members.Add(AppMatcher(" "));           // 无效（空值步）剔除

        var result = await svc.BindStrandAsync("user-1", request);

        Assert.NotNull(result);
        Assert.Equal("HyperFrames", result.Name);
        Assert.Equal(2, result.Members.Count);

        var row = Assert.Single(await db.Strands.Include(s => s.Members).ToListAsync());
        Assert.Equal("user-1", row.OwnerId);
        Assert.Equal(2, row.Members.Count);
    }

    [Fact]
    public async Task Bind_SameNameWithoutId_MergesIntoExistingStrand()
    {
        using var db = CreateDbContext();
        var svc = new KnowledgeService(db);

        var first = await svc.BindStrandAsync("user-1", HyperFrames());

        var resubmit = HyperFrames();
        resubmit.Gloss = "卡片上 AI 猜的释义";
        resubmit.Members.Add(AppMatcher("AfterFX.exe"));
        var second = await svc.BindStrandAsync("user-1", resubmit);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.Id, second.Id); // 幂等收敛，不产第二行
        Assert.Equal("我在搞的 AI 动效框架", second.Gloss); // 归入不碾压既有释义（身体策展）
        Assert.Equal(3, second.Members.Count); // 指纹并集追加（脊柱聚合）
        Assert.Single(await db.Strands.ToListAsync());
    }

    [Fact]
    public async Task Bind_QuestionCardShape_GrowsFingerprint()
    {
        // 问题卡的真实调用形状（ADR-029 §4/§5）：单 Matcher、无 Id、名字手打（大小写变体）。
        // 撞名归入是指纹生长的唯一主干道——判官每卡只锚一个 Matcher。
        using var db = CreateDbContext();
        var svc = new KnowledgeService(db);
        var created = await svc.BindStrandAsync("user-1", HyperFrames());
        Assert.NotNull(created);

        var card = new BindStrandRequest
        {
            Name = "hyperframes", // 第 5 天手打的大小写变体，不裂出第二条 Strand
            Gloss = "AI 猜的一句话",
            Members = [AppMatcher("Code.EXE")]
        };
        var merged = await svc.BindStrandAsync("user-1", card);

        Assert.NotNull(merged);
        Assert.Equal(created.Id, merged.Id);
        Assert.Equal("HyperFrames", merged.Name); // 保留库中原形
        Assert.Equal("我在搞的 AI 动效框架", merged.Gloss);
        Assert.Equal(3, merged.Members.Count);
        Assert.True(merged.CreatedAt < merged.UpdatedAt); // 前端归入轻提示的判据
        Assert.Contains(merged.Members, m => m.Steps.Any(s => s.Value == "code.exe")); // canonical 小写
    }

    [Fact]
    public async Task Bind_MergeFillsEmptyGloss()
    {
        using var db = CreateDbContext();
        var svc = new KnowledgeService(db);
        var bare = new BindStrandRequest { Name = "花生", Gloss = "", Members = [UrlContains("huasheng")] };
        Assert.NotNull(await svc.BindStrandAsync("user-1", bare));

        var card = new BindStrandRequest
        {
            Name = "花生",
            Gloss = "B 站实习时部门做的产品",
            Members = [AppMatcher("huasheng.exe")]
        };
        var merged = await svc.BindStrandAsync("user-1", card);

        Assert.NotNull(merged);
        Assert.Equal("B 站实习时部门做的产品", merged.Gloss); // 库里为空 → 卡上值补位
    }

    [Fact]
    public async Task Bind_ById_RenamesAndReplacesMembers()
    {
        using var db = CreateDbContext();
        var svc = new KnowledgeService(db);
        var created = await svc.BindStrandAsync("user-1", HyperFrames());
        Assert.NotNull(created);

        var update = new BindStrandRequest
        {
            Id = created.Id,
            Name = "HyperFrames v2",
            Gloss = created.Gloss,
            Members = [AppMatcher("AfterFX.exe")]
        };
        var renamed = await svc.BindStrandAsync("user-1", update);

        Assert.NotNull(renamed);
        Assert.Equal(created.Id, renamed.Id);
        Assert.Equal("HyperFrames v2", renamed.Name);
        var member = Assert.Single(renamed.Members);
        Assert.Equal("afterfx.exe", Assert.Single(member.Steps).Value); // canonical 小写形

        // 旧成员行被整组替换，无残留
        Assert.Single(await db.StrandMatchers.ToListAsync());
    }

    [Fact]
    public async Task Bind_ByIdAcrossOwners_ReturnsNullAndChangesNothing()
    {
        using var db = CreateDbContext();
        var svc = new KnowledgeService(db);
        var created = await svc.BindStrandAsync("user-1", HyperFrames());
        Assert.NotNull(created);

        var hijack = new BindStrandRequest { Id = created.Id, Name = "stolen", Gloss = "", Members = [] };
        var result = await svc.BindStrandAsync("user-2", hijack);

        Assert.Null(result);
        var row = Assert.Single(await db.Strands.Include(s => s.Members).ToListAsync());
        Assert.Equal("HyperFrames", row.Name);
        Assert.Equal(2, row.Members.Count);
    }

    [Fact]
    public async Task Bind_SameNameDifferentOwners_AreSeparateStrands()
    {
        using var db = CreateDbContext();
        var svc = new KnowledgeService(db);

        var mine = await svc.BindStrandAsync("user-1", HyperFrames());
        var theirs = await svc.BindStrandAsync("user-2", HyperFrames());

        Assert.NotNull(mine);
        Assert.NotNull(theirs);
        Assert.NotEqual(mine.Id, theirs.Id);
        Assert.Equal(2, (await db.Strands.ToListAsync()).Count);
    }

    [Fact]
    public async Task Mute_IsIdempotent_StepOrderInsensitive()
    {
        using var db = CreateDbContext();
        var svc = new KnowledgeService(db);

        var forward = new MatcherDto
        {
            Source = ActivitySources.System,
            Steps =
            [
                new() { Layer = 1, Reading = "app", Op = MatcherOps.Equal, Value = "Code" },
                new() { Layer = 2, Reading = "title", Op = MatcherOps.Contains, Value = "news" },
            ]
        };
        var reversed = new MatcherDto
        {
            Source = ActivitySources.System,
            Steps =
            [
                new() { Layer = 2, Reading = "title", Op = "CONTAINS", Value = " news " },
                new() { Layer = 1, Reading = "app", Op = MatcherOps.Equal, Value = "CODE" },
            ]
        };

        Assert.True(await svc.MuteMatcherAsync("user-1", forward));
        Assert.True(await svc.MuteMatcherAsync("user-1", reversed)); // 步骤换序 + 值大小写/空白差异 → 同一裁决

        var row = Assert.Single(await db.MutedMatchers.ToListAsync());
        Assert.Equal("user-1", row.OwnerId);
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
