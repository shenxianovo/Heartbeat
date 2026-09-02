using System.Text.Json;
using Heartbeat.Core.DTOs.Collectors;
using Heartbeat.Server.Entities;
using Heartbeat.Server.Services;
using Heartbeat.Server.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Tests.Services;

/// <summary>声明表（ADR-030 §4）：生效 = BuiltIn 种子 + DB 的运行时声明；启动种子幂等。</summary>
[Collection("postgres")]
public class CollectorDeclarationStoreTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task EffectiveTables_BuiltInSeedAndReportedDeclaration()
    {
        using var db = CreateDbContext();
        var v2 = new CollectorDeclarationDto
        {
            Source = "browser",
            Version = 2,
            Layers =
            [
                new() { Readings = [new() { Name = "site", From = "attributes.site" }] },
                new() { Readings = [new() { Name = "url", From = DepthSlots.IdentityKey }] },
            ]
        };
        db.CollectorDeclarations.Add(new CollectorDeclaration
        {
            Source = "browser",
            Version = 2,
            PayloadJson = JsonSerializer.Serialize(v2),
            ReportedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        await SeedDeclarations.SeedAsync(db);

        Assert.Equal(
            ["browser", "system"],
            await db.CollectorDeclarations.OrderBy(d => d.Source).Select(d => d.Source).ToArrayAsync());

        var tables = await new DigestAssembler(db).LoadDepthTablesAsync();

        Assert.Equal(2, tables.For("browser")!.Version); // 非内置 Collector 只由 DB 声明生效
        Assert.Equal(1, tables.For("system")!.Version);  // BuiltIn 种子地板兜底
        Assert.Equal([new DepthReading(1, "site", "example.com"), new DepthReading(2, "url", "blog.example.com/p")],
            tables.ReadingsFor("browser", null, null, "blog.example.com/p", """{"site":"example.com"}"""));
    }

    [Fact]
    public async Task SeedAsync_InsertsOnce_IsIdempotent()
    {
        using var db = CreateDbContext();

        await SeedDeclarations.SeedAsync(db);
        await SeedDeclarations.SeedAsync(db);

        var rows = await db.CollectorDeclarations.OrderBy(d => d.Source).ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal("system", row.Source);
        Assert.All(rows, r => Assert.Equal(1, r.Version));
    }
}
