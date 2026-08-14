using System.Text;
using Heartbeat.Server.AppCatalog;

namespace Heartbeat.Server.Tests.Services;

public class AppCatalogLoaderTests
{
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
