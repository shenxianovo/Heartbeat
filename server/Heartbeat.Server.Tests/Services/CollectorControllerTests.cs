using Heartbeat.Core.DTOs.Collectors;
using Heartbeat.Server.Controllers;
using Heartbeat.Server.Services;
using Heartbeat.Server.Tests.Fixtures;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Tests.Services;

/// <summary>声明上行端点（ADR-030 §3）：落表、同版幂等覆盖、坏声明 400。</summary>
[Collection("postgres")]
public class CollectorControllerTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    private static CollectorDeclarationDto BrowserV2(string collectorVersion = "1.4.0") => new()
    {
        Source = "Browser", // 大小写变体:入库应归一小写
        Version = 2,
        CollectorVersion = collectorVersion,
        Layers =
        [
            new() { Readings = [new() { Name = "site", From = "attributes.site", Label = "站点" }] },
            new() { Readings = [new() { Name = "url", From = DepthSlots.IdentityKey, Label = "网址" }] },
        ]
    };

    [Fact]
    public async Task Report_PersistsCanonical_SameVersionOverwrites()
    {
        using var db = CreateDbContext();
        var controller = new CollectorController(db);

        var first = await controller.ReportDeclarations([BrowserV2("1.4.0")]);
        var second = await controller.ReportDeclarations([BrowserV2("1.4.1")]); // 同 (source, version) 重报

        Assert.IsType<NoContentResult>(first);
        Assert.IsType<NoContentResult>(second);

        var row = Assert.Single(await db.CollectorDeclarations.ToListAsync());
        Assert.Equal("browser", row.Source); // canonical 小写
        Assert.Equal(2, row.Version);
        Assert.Contains("\"1.4.1\"", row.PayloadJson); // 幂等覆盖取后写

        // 生效表按 max(Version) 吃到新声明
        var tables = await new DigestAssembler(db).LoadDepthTablesAsync();
        Assert.Equal(2, tables.For("browser")!.Version);
    }

    [Fact]
    public async Task Report_InvalidDeclaration_Returns400WritesNothing()
    {
        using var db = CreateDbContext();
        var controller = new CollectorController(db);

        var bad = BrowserV2();
        bad.Layers[0].Readings[0].From = "shell command"; // 非法槽位

        var result = await controller.ReportDeclarations([bad]);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(await db.CollectorDeclarations.ToListAsync());
    }

    [Fact]
    public async Task Report_EmptyBatch_Returns400()
    {
        using var db = CreateDbContext();
        var controller = new CollectorController(db);

        Assert.IsType<BadRequestObjectResult>(await controller.ReportDeclarations([]));
    }
}
