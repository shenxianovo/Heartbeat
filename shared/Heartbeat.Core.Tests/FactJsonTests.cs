using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Heartbeat.Core.DTOs.Facts;
using Heartbeat.Core.Facts;

namespace Heartbeat.Core.Tests;

public class FactJsonTests
{
    [Theory]
    [InlineData("{\"a\":1,\"b\":[true,0]}", "{\"b\":[true,-0.0],\"a\":1.00e0}")]
    [InlineData("0.000000000000000000000000000001", "1e-30")]
    [InlineData("9007199254740991", "9007199254740991.0")]
    public void EquivalentPayloadsHaveTheSameCanonicalContent(string left, string right)
    {
        using var first = JsonDocument.Parse(left);
        using var second = JsonDocument.Parse(right);

        Assert.Null(FactJson.Validate(first.RootElement));
        Assert.Null(FactJson.Validate(second.RootElement));
        Assert.Equal(FactJson.Canonicalize(first.RootElement), FactJson.Canonicalize(second.RootElement));
    }

    [Theory]
    [InlineData("{\"attributes\":{\"url\":\"a\",\"url\":\"b\"}}")]
    [InlineData("[9007199254740992]")]
    [InlineData("{\"number\":1e400}")]
    public void AmbiguousOrUnsafePayloadsAreRejected(string json)
    {
        using var document = JsonDocument.Parse(json);

        Assert.NotNull(FactJson.Validate(document.RootElement));
    }

    [Fact]
    public void TransportPreservesTheExactSchemaDocumentBytes()
    {
        const string document = "{\r\n  \"title\": \"网页观测\", \"payloadSchema\": {}\r\n}\n";
        var bytes = Encoding.UTF8.GetBytes(document);
        var definition = new FactSchemaDefinition
        {
            Revision = 1,
            ContentHash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)),
            DocumentJson = document
        };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var received = JsonSerializer.Deserialize<FactSchemaDefinition>(JsonSerializer.Serialize(definition, options), options)!;

        Assert.Equal(bytes, Encoding.UTF8.GetBytes(received.DocumentJson));
        Assert.Equal(received.ContentHash, "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(received.DocumentJson))));
    }
}
