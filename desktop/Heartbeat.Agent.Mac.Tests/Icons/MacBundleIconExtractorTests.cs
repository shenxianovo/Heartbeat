using Heartbeat.Agent.Mac.Icons;
using Heartbeat.Agent.Mac.Observations;

namespace Heartbeat.Agent.Mac.Tests.Icons;

public sealed class MacBundleIconExtractorTests
{
    [Fact]
    public void ObservedBundle_CanProvidePngForAppIconUpload()
    {
        var catalog = new MacApplicationCatalog();
        catalog.Observe(
            "mac:com.example.editor",
            new MacApplication(
                "com.example.editor",
                "/Applications/Editor.app/Contents/MacOS/Editor",
                "Editor"));
        var tools = new FakeTools("EditorIcon", [1, 2, 3]);
        var extractor = new MacBundleIconExtractor(catalog, tools);

        var png = extractor.Extract("mac:com.example.editor");

        Assert.Equal([1, 2, 3], png);
        Assert.Equal(
            "/Applications/Editor.app/Contents/Resources/EditorIcon.icns",
            tools.ConvertedPath);
    }

    [Fact]
    public void UnknownOrNonBundleIdentity_DoesNotGuessAnIcon()
    {
        var extractor = new MacBundleIconExtractor(
            new MacApplicationCatalog(),
            new FakeTools("Icon", [1]));

        Assert.Null(extractor.Extract("mac:com.example.unknown"));
        Assert.Null(extractor.Extract("mac:exe.command-line-tool"));
    }

    private sealed class FakeTools(string? iconName, byte[]? png) : IMacIconTools
    {
        public string? ConvertedPath { get; private set; }
        public string? ReadBundleIconName(string infoPlistPath) => iconName;
        public byte[]? ConvertToPng(string iconPath)
        {
            ConvertedPath = iconPath;
            return png;
        }
    }
}
