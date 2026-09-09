using System.Text.Json;
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

}
