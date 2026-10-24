using System.Text.Json;
using Heartbeat.Core;

namespace Heartbeat.Model.Tests;

public sealed class SchemaBoundaryTests
{
    // SCHEMA-2
    [Test]
    [Arguments("9223372036854775807", true)]
    [Arguments("-9223372036854775808", true)]
    [Arguments("9223372036854775808", true)]
    [Arguments("-9223372036854775809", true)]
    [Arguments("1.000e3", true)]
    [Arguments("1000e-3", true)]
    [Arguments("1001e-3", false)]
    [Arguments("0e99999999999999999999", true)]
    [Arguments("1e99999999999999999999", true)]
    public async Task IntegersFollowMathematicalValueWithoutStorageLimits(string json, bool valid)
    {
        using var document = JsonDocument.Parse(json);
        await Assert.That(EntitySchemaRules.IsInteger(document.RootElement)).IsEqualTo(valid);
    }

    // SCHEMA-2
    [Test]
    [Arguments("string", "\"text\"", true)]
    [Arguments("string", "1", false)]
    [Arguments("boolean", "true", true)]
    [Arguments("boolean", "false", true)]
    [Arguments("boolean", "\"true\"", false)]
    [Arguments("number", "1.25e100", true)]
    [Arguments("number", "\"1\"", false)]
    [Arguments("integer", "-12.00e2", true)]
    [Arguments("integer", "1e-10000000000000000000000", false)]
    public async Task PrimitiveValuesMatchTheirDeclaredType(string type, string value, bool valid)
    {
        var fields = JsonSerializer.SerializeToElement(new { value = new { type, required = true, nullable = false } });
        var content = ModelFixture.Json($$"""{"value":{{value}}}""");
        await Assert.That(EntitySchemaRules.ValidEntity(fields, content)).IsEqualTo(valid);
    }

    // SCHEMA-10: all combinations of required/nullable, including valid non-null values.
    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task RequiredAndNullableHaveIndependentTruthTables(bool required, bool nullable)
    {
        var fields = JsonSerializer.SerializeToElement(new { value = new { type = "string", required, nullable } });
        await Assert.That(EntitySchemaRules.ValidEntity(fields, ModelFixture.Json("{}"))).IsEqualTo(!required);
        await Assert.That(EntitySchemaRules.ValidEntity(fields, ModelFixture.Json("{\"value\":null}"))).IsEqualTo(nullable);
        await Assert.That(EntitySchemaRules.ValidEntity(fields, ModelFixture.Json("{\"value\":\"text\"}"))).IsTrue();
    }
}
