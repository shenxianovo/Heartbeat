using System.Text.Json;
using Heartbeat.Application.Entities;

namespace Heartbeat.Model.Tests;

public sealed class SchemaBoundaryTests
{
    [Test]
    [Arguments("9223372036854775807", true)]
    [Arguments("-9223372036854775808", true)]
    [Arguments("9223372036854775808", false)]
    [Arguments("-9223372036854775809", false)]
    [Arguments("1.000e3", true)]
    [Arguments("1000e-3", true)]
    [Arguments("1001e-3", false)]
    [Arguments("0e99999999999999999999", true)]
    [Arguments("1e99999999999999999999", false)]
    public async Task IntegersFollowMathematicalValueWithinInt64(string json, bool valid)
    {
        using var document = JsonDocument.Parse(json);
        await Assert.That(EntitySchemaRules.TryInteger(document.RootElement, out _)).IsEqualTo(valid);
    }

    [Test]
    [Arguments(false, false, "{}", true)]
    [Arguments(false, false, "{\"v\":null}", false)]
    [Arguments(false, true, "{\"v\":null}", true)]
    [Arguments(true, true, "{}", false)]
    [Arguments(true, true, "{\"v\":null}", true)]
    public async Task MissingAndNullAreIndependent(bool required, bool nullable, string payload, bool expected)
    {
        using var fields = JsonDocument.Parse(JsonSerializer.Serialize(new { v = new { type = "string", required, nullable } }));
        using var refs = JsonDocument.Parse("{}");
        using var properties = JsonDocument.Parse(payload);
        await Assert.That(EntitySchemaRules.ValidEntity(fields.RootElement, refs.RootElement, properties.RootElement)).IsEqualTo(expected);
    }
}
