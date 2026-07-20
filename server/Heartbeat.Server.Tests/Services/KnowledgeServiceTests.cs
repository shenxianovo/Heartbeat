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
    public async Task Bind_SameNameWithoutId_ConvergesToSameRow()
    {
        using var db = CreateDbContext();
        var svc = new KnowledgeService(db);

        var first = await svc.BindStrandAsync("user-1", HyperFrames());

        var resubmit = HyperFrames();
        resubmit.Gloss = "改过的释义";
        resubmit.Members.Add(AppMatcher("AfterFX.exe"));
        var second = await svc.BindStrandAsync("user-1", resubmit);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.Id, second.Id); // 幂等收敛，不产第二行
        Assert.Equal("改过的释义", second.Gloss);
        Assert.Equal(3, second.Members.Count);
        Assert.Single(await db.Strands.ToListAsync());
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
        Assert.Equal("AfterFX.exe", Assert.Single(member.Steps).Value);

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
                new() { Layer = 1, Reading = "app", Op = MatcherOps.Equal, Value = "Code" },
            ]
        };

        Assert.True(await svc.MuteMatcherAsync("user-1", forward));
        Assert.True(await svc.MuteMatcherAsync("user-1", reversed)); // 步骤换序 + 大小写/空白差异 → 同一裁决

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
