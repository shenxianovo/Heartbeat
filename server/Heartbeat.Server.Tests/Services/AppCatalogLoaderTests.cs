using System.Text;
using Heartbeat.Server.AppCatalog;

namespace Heartbeat.Server.Tests.Services;

public class AppCatalogLoaderTests
{
    [Fact]
    public void ProductionArtifact_IsCanonicalReviewedVersion2Snapshot()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "AppCatalog", "app-catalog.json");
        var snapshot = AppCatalogLoader.LoadFile(path);
        var expected = """
            {
              "schemaVersion": 1,
              "catalogVersion": 2,
              "products": [
                {
                  "key": "chrome",
                  "displayName": "Google Chrome",
                  "identities": [
                    "mac:com.google.chrome",
                    "win:chrome"
                  ]
                },
                {
                  "key": "feishu",
                  "displayName": "Feishu",
                  "identities": [
                    "mac:com.electron.lark",
                    "win:feishu"
                  ]
                },
                {
                  "key": "finder",
                  "displayName": "Finder",
                  "identities": [
                    "mac:com.apple.finder"
                  ]
                },
                {
                  "key": "heartbeat",
                  "displayName": "Heartbeat",
                  "identities": [
                    "mac:com.shenxianovo.heartbeat",
                    "mac:exe.heartbeat.desktop.mac",
                    "win:heartbeat.desktop.windows",
                    "win:heartbeat.wpf"
                  ]
                },
                {
                  "key": "qq",
                  "displayName": "QQ",
                  "identities": [
                    "mac:com.tencent.qq",
                    "win:qq"
                  ]
                },
                {
                  "key": "vscode",
                  "displayName": "Visual Studio Code",
                  "identities": [
                    "mac:com.microsoft.vscode",
                    "win:code"
                  ]
                }
              ]
            }

            """.Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Equal(expected, Encoding.UTF8.GetString(snapshot.CanonicalBytes));
        Assert.Equal(snapshot.CanonicalBytes, File.ReadAllBytes(path));

        // Feishu verification (2026-08-14): real Windows facts report win:feishu;
        // /Applications/Lark.app reports bundle com.electron.lark and is signed by
        // Beijing Feishu Technology Co., Ltd. mac:com.electron.lark.iron is the
        // separately observed Feishu Meeting product and intentionally stays provisional.
        var feishu = Assert.Single(snapshot.Document.Products, x => x.Key == "feishu");
        Assert.Equal("Feishu", feishu.DisplayName);
        Assert.DoesNotContain(
            snapshot.Document.Products.SelectMany(x => x.Identities),
            x => x is "mac:com.electron.lark.iron" or "mac:com.openai.codex");

        // Heartbeat verification: current releases package Heartbeat.Desktop.Windows.exe
        // and bundle com.shenxianovo.heartbeat; the released WPF predecessor used
        // Heartbeat.WPF.exe. Direct macOS `dotnet run` is observed through the adapter's
        // executable fallback as mac:exe.heartbeat.desktop.mac. The hover prototype has
        // no shipped/source identity contract and is intentionally excluded.
        var heartbeat = Assert.Single(snapshot.Document.Products, x => x.Key == "heartbeat");
        Assert.Equal(
            [
                "mac:com.shenxianovo.heartbeat",
                "mac:exe.heartbeat.desktop.mac",
                "win:heartbeat.desktop.windows",
                "win:heartbeat.wpf"
            ],
            heartbeat.Identities);
        Assert.DoesNotContain("mac:exe.heartbeat-hover-mac", heartbeat.Identities);
    }

    [Fact]
    public void Parse_ProducesCanonicalBytesAndStableHash()
    {
        const string unordered = """
            {
              "products": [
                { "identities": ["win:zeta", "mac:com.example.zeta"], "displayName": "Zeta", "key": "zeta" },
                { "displayName": "Alpha", "key": "alpha", "identities": ["win:alpha"] }
              ],
              "catalogVersion": 7,
              "schemaVersion": 1
            }
            """;
        const string differentlyOrdered = """
            {
              "schemaVersion": 1,
              "catalogVersion": 7,
              "products": [
                { "key": "alpha", "displayName": "Alpha", "identities": ["win:alpha"] },
                { "key": "zeta", "displayName": "Zeta", "identities": ["mac:com.example.zeta", "win:zeta"] }
              ]
            }
            """;

        var first = AppCatalogLoader.Parse(unordered, requireCanonicalOrdering: false);
        var second = AppCatalogLoader.Parse(differentlyOrdered, requireCanonicalOrdering: false);

        Assert.Equal(first.ContentHash, second.ContentHash);
        Assert.Equal(first.CanonicalBytes, second.CanonicalBytes);
        Assert.Equal(
            """
            {
              "schemaVersion": 1,
              "catalogVersion": 7,
              "products": [
                {
                  "key": "alpha",
                  "displayName": "Alpha",
                  "identities": [
                    "win:alpha"
                  ]
                },
                {
                  "key": "zeta",
                  "displayName": "Zeta",
                  "identities": [
                    "mac:com.example.zeta",
                    "win:zeta"
                  ]
                }
              ]
            }

            """.Replace("\r\n", "\n", StringComparison.Ordinal),
            Encoding.UTF8.GetString(first.CanonicalBytes));
    }

    [Theory]
    [InlineData("""{"schemaVersion":2,"catalogVersion":1,"products":[]}""", "Unsupported")]
    [InlineData("""{"schemaVersion":1,"catalogVersion":0,"products":[]}""", "at least 1")]
    [InlineData("""{"schemaVersion":1,"catalogVersion":1,"products":[{"key":"Chrome","displayName":"Chrome","identities":["win:chrome"]}]}""", "normalized")]
    [InlineData("""{"schemaVersion":1,"catalogVersion":1,"products":[{"key":"chrome","displayName":" ","identities":["win:chrome"]}]}""", "displayName")]
    [InlineData("""{"schemaVersion":1,"catalogVersion":1,"products":[{"key":"chrome","displayName":"Chrome","identities":[]}]}""", "at least one")]
    [InlineData("""{"schemaVersion":1,"catalogVersion":1,"products":[{"key":"chrome","displayName":"Chrome","identities":["WIN:Chrome.exe"]}]}""", "not normalized")]
    [InlineData("""{"schemaVersion":1,"catalogVersion":1,"products":[{"key":"chrome","displayName":"Chrome","identities":["browser:chrome"]}]}""", "invalid")]
    [InlineData("""{"schemaVersion":1,"catalogVersion":1,"products":[{"key":"chrome","displayName":"Chrome","identities":["win:chrome"]},{"key":"chrome","displayName":"Chrome 2","identities":["mac:com.google.chrome"]}]}""", "Duplicate App Catalog product key")]
    [InlineData("""{"schemaVersion":1,"catalogVersion":1,"products":[{"key":"chrome","displayName":"Chrome","identities":["win:chrome"]},{"key":"chromium","displayName":"Chromium","identities":["win:chrome"]}]}""", "Duplicate App Catalog identity")]
    public void Parse_RejectsInvalidDocuments(string json, string expectedMessage)
    {
        var exception = Assert.Throws<AppCatalogException>(() => AppCatalogLoader.Parse(json));
        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_RejectsNonCanonicalProductAndIdentityOrdering()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "catalogVersion": 1,
              "products": [
                { "key": "zeta", "displayName": "Zeta", "identities": ["win:zeta", "mac:com.example.zeta"] },
                { "key": "alpha", "displayName": "Alpha", "identities": ["win:alpha"] }
              ]
            }
            """;

        var exception = Assert.Throws<AppCatalogException>(() => AppCatalogLoader.Parse(json));
        Assert.Contains("canonical ordinal ordering", exception.Message);
    }

    [Fact]
    public void Parse_RejectsUnknownAndDuplicateProperties()
    {
        Assert.Throws<AppCatalogException>(() => AppCatalogLoader.Parse(
            """{"schemaVersion":1,"catalogVersion":1,"products":[],"extra":true}"""));
        Assert.Throws<AppCatalogException>(() => AppCatalogLoader.Parse(
            """{"schemaVersion":1,"schemaVersion":1,"catalogVersion":1,"products":[]}"""));
    }
}
