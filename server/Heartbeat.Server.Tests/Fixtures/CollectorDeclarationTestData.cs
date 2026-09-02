using Heartbeat.Core;
using Heartbeat.Core.DTOs.Collectors;
using Heartbeat.Server.Services;

namespace Heartbeat.Server.Tests.Fixtures;

internal static class CollectorDeclarationTestData
{
    public static CollectorDeclarationDto Create(
        string source,
        int version,
        params DepthLayerDto[] layers) => new()
        {
            Source = source,
            Version = version,
            Layers = [.. layers],
        };

    public static DepthLayerDto Layer(string name, string from, string? label = null) => new()
    {
        Readings = [new() { Name = name, From = from, Label = label }],
    };

    public static CollectorDeclarationDto BrowserV1() => Create(
        ActivitySources.Browser,
        1,
        Layer("url", DepthSlots.IdentityKey, "网址"),
        Layer("tab_title", DepthSlots.Title, "标签页"));

    public static DepthTables With(params CollectorDeclarationDto[] declarations)
        => new(SeedDeclarations.All.Concat(declarations));
}
