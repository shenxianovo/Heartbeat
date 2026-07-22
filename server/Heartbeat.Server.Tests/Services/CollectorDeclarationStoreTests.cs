using System.Text.Json;
using Heartbeat.Core.DTOs.Collectors;
using Heartbeat.Server.Entities;
using Heartbeat.Server.Services;
using Heartbeat.Server.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Tests.Services;

/// <summary>声明表（ADR-030 §4）：生效 = 种子地板 + DB 按 max(Version) 覆盖；启动种子幂等。</summary>
[Collection("postgres")]
public class CollectorDeclarationStoreTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task EffectiveTables_SeedFloor_DbOverridesByVersion()
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

        var tables = await new DigestAssembler(db).LoadDepthTablesAsync();

        Assert.Equal(2, tables.For("browser")!.Version); // DB 覆盖种子
        Assert.Equal(1, tables.For("system")!.Version);  // 种子地板兜底(干净库不失明)
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
        Assert.Equal(2, rows.Count);
        Assert.Equal(["browser", "system"], rows.Select(r => r.Source).ToArray());
        Assert.All(rows, r => Assert.Equal(1, r.Version));
    }
}
