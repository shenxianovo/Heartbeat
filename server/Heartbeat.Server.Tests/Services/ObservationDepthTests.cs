using Heartbeat.Core;
using Heartbeat.Core.DTOs.Collectors;
using Heartbeat.Server.Services;

namespace Heartbeat.Server.Tests.Services;

/// <summary>
/// 声明驱动的观测深度（ADR-030）：校验 / 槽位取值 / 生效规则的纯函数面。
/// 种子声明的解释结果 = 各采集器 v1 契约（切换日行为零断层的基准）。
/// </summary>
public class ObservationDepthTests
{
    // ---- 解释器(种子基准) ----

    [Fact]
    public void Seed_System_TwoLayers_AppThenTitle()
    {
        var readings = DepthTables.Seeds.ReadingsFor(
            ActivitySources.System, "Code", "hyperframes — a.ts", "Code|hyperframes — a.ts");

        DepthReading[] expected = [new(1, "app", "Code"), new(2, "title", "hyperframes — a.ts")];
        Assert.Equal(expected, readings);
    }

    [Fact]
    public void Seed_System_MissingTitle_OnlyL1()
    {
        var readings = DepthTables.Seeds.ReadingsFor(ActivitySources.System, "Code", null, "Code|");

        Assert.Equal([new DepthReading(1, "app", "Code")], readings);
    }

    [Fact]
    public void Seed_System_MissingApp_RootAxisFallsBackToUnknown()
    {
        var readings = DepthTables.Seeds.ReadingsFor(ActivitySources.System, "  ", null, "x");

        Assert.Equal("(unknown)", Assert.Single(readings).Value);
    }

    [Fact]
    public void Seed_Browser_UrlThenTabTitle()
    {
        var readings = DepthTables.Seeds.ReadingsFor(
            ActivitySources.Browser, "chrome", "花生看板", "huasheng.com/dashboard");

        // tab_title 挂 L2(ADR-030 §5):url 下的标题分布是"下一深度分解"的定义本身。
        DepthReading[] expected = [new(1, "url", "huasheng.com/dashboard"), new(2, "tab_title", "花生看板")];
        Assert.Equal(expected, readings);
    }

    [Fact]
    public void UndeclaredSource_FallsBackToIdentityAndTitle()
    {
        var readings = DepthTables.Seeds.ReadingsFor("vscode", null, "Program.cs", "repo-root");

        DepthReading[] expected = [new(1, "identity", "repo-root"), new(2, "title", "Program.cs")];
        Assert.Equal(expected, readings);
    }

    // ---- 槽位取值(attributes.*) ----

    [Fact]
    public void AttributesSlot_ResolvesNestedPath_MissingSkips()
    {
        var tables = new DepthTables([new CollectorDeclarationDto
        {
            Source = "browser",
            Version = 2,
            Layers =
            [
                new() { Readings = [new() { Name = "site", From = "attributes.site" }] },
                new() { Readings = [new() { Name = "url", From = DepthSlots.IdentityKey }] },
            ]
        }]);

        var withSite = tables.ReadingsFor("browser", null, null, "blog.example.com/post",
            """{"site":"example.com","windowId":3}""");
        DepthReading[] expected = [new(1, "site", "example.com"), new(2, "url", "blog.example.com/post")];
        Assert.Equal(expected, withSite);

        // 老段无 attributes.site:该读数缺席,段挂最深可用读数(树构建负责),解释器不造假值——
        // 但首层首读数是树根轴,缺值给 "(unknown)" 保段不消失。
        var withoutSite = tables.ReadingsFor("browser", null, null, "blog.example.com/post", null);
        Assert.Equal([new DepthReading(1, "site", "(unknown)"), new DepthReading(2, "url", "blog.example.com/post")],
            withoutSite);
    }

    // ---- 生效规则 ----

    [Fact]
    public void EffectiveTable_TakesMaxVersionPerSource()
    {
        var v2 = new CollectorDeclarationDto
        {
            Source = "Browser", // 大小写变体也收敛
            Version = 2,
            Layers = [new() { Readings = [new() { Name = "site", From = "attributes.site" }] }]
        };
        var tables = new DepthTables(SeedDeclarations.All.Append(v2));

        Assert.Equal(2, tables.For("browser")!.Version);
        Assert.Equal(1, tables.For("system")!.Version);
    }

    // ---- 声明校验 ----

    [Fact]
    public void Validate_SeedDeclarations_AreValid()
    {
        Assert.All(SeedDeclarations.All, d => Assert.Null(DeclarationValidator.Validate(d)));
    }

    [Fact]
    public void Validate_RejectsDuplicateNames_InvalidSlots_EmptyShapes()
    {
        Assert.NotNull(DeclarationValidator.Validate(new CollectorDeclarationDto
        {
            Source = "x", Version = 1,
            Layers =
            [
                new() { Readings = [new() { Name = "a", From = DepthSlots.Title }] },
                new() { Readings = [new() { Name = "A", From = DepthSlots.AppName }] }, // 重名(大小写不敏感)
            ]
        }));
        Assert.NotNull(DeclarationValidator.Validate(new CollectorDeclarationDto
        {
            Source = "x", Version = 1,
            Layers = [new() { Readings = [new() { Name = "a", From = "bogus" }] }]
        }));
        Assert.NotNull(DeclarationValidator.Validate(new CollectorDeclarationDto
        {
            Source = "x", Version = 1,
            Layers = [new() { Readings = [new() { Name = "a", From = "attributes." }] }] // 空路径
        }));
        Assert.NotNull(DeclarationValidator.Validate(new CollectorDeclarationDto { Source = "x", Version = 1 }));
        Assert.NotNull(DeclarationValidator.Validate(new CollectorDeclarationDto
        {
            Source = "", Version = 1,
            Layers = [new() { Readings = [new() { Name = "a", From = DepthSlots.Title }] }]
        }));
        Assert.NotNull(DeclarationValidator.Validate(new CollectorDeclarationDto
        {
            Source = "x", Version = 0,
            Layers = [new() { Readings = [new() { Name = "a", From = DepthSlots.Title }] }]
        }));
    }
}
