using Heartbeat.Core;
using Heartbeat.Core.DTOs.Knowledge;
using Heartbeat.Server.Services;
using Heartbeat.Server.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Tests.Services;

[Collection("postgres")]
public class KnowledgeServiceTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    private static HandleDto Handle(string source, string token) => new() { Source = source, Token = token };

    private static BindStrandRequest HyperFrames(Guid? id = null) => new()
    {
        Id = id,
        Name = "HyperFrames",
        Gloss = "我在搞的 AI 动效框架",
        Members =
        [
            Handle(ActivitySources.Browser, "localhost"),
            Handle(ActivitySources.System, "blender.exe"),
        ]
    };

    [Fact]
    public async Task Bind_CreatesStrandWithDedupedMembers()
    {
        using var db = CreateDbContext();
        var svc = new KnowledgeService(db);

        var request = HyperFrames();
        request.Members.Add(Handle(ActivitySources.System, "blender.exe")); // 重复成员
        request.Members.Add(Handle(ActivitySources.System, " "));           // 空 token 剔除

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
        resubmit.Members.Add(Handle(ActivitySources.System, "AfterFX.exe"));
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
            Members = [Handle(ActivitySources.System, "AfterFX.exe")]
        };
        var renamed = await svc.BindStrandAsync("user-1", update);

        Assert.NotNull(renamed);
        Assert.Equal(created.Id, renamed.Id);
        Assert.Equal("HyperFrames v2", renamed.Name);
        var member = Assert.Single(renamed.Members);
        Assert.Equal("AfterFX.exe", member.Token);

        // 旧成员行被整组替换，无残留
        Assert.Single(await db.StrandHandles.ToListAsync());
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
    public async Task Mute_IsIdempotent()
    {
        using var db = CreateDbContext();
        var svc = new KnowledgeService(db);

        await svc.MuteHandleAsync("user-1", ActivitySources.Browser, "news.example.com");
        await svc.MuteHandleAsync("user-1", ActivitySources.Browser, "news.example.com");

        var row = Assert.Single(await db.MutedHandles.ToListAsync());
        Assert.Equal("user-1", row.OwnerId);
        Assert.Equal("news.example.com", row.Token);
    }

    [Fact]
    public async Task Mute_OwnersAreIsolated()
    {
        using var db = CreateDbContext();
        var svc = new KnowledgeService(db);

        await svc.MuteHandleAsync("user-1", ActivitySources.Browser, "news.example.com");
        await svc.MuteHandleAsync("user-2", ActivitySources.Browser, "news.example.com");

        Assert.Equal(2, (await db.MutedHandles.ToListAsync()).Count);
    }
}
