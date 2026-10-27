using System.Text.Json;
using Orisumi.Core;

namespace Orisumi.Model.Tests;

public sealed class JsonAmbiguityTests
{
    // SCHEMA-12
    [Test]
    [Arguments("{\"message\":\"first\",\"message\":\"second\"}")]
    [Arguments("{\"message\":\"first\",\"messa\\u0067e\":\"second\"}")]
    public async Task DuplicateContentNamesAreRejected(string properties)
    {
        using var fields = JsonDocument.Parse("""{"message":{"type":"string","description":"Test field value.","required":true,"nullable":false}}""");
        using var content = JsonDocument.Parse(properties);
        await Assert.That(EntitySchemaRules.ValidEntity(fields.RootElement, content.RootElement)).IsFalse();
    }

    // SCHEMA-10, SCHEMA-12, SCHEMA-14
    [Test]
    [Arguments("{\"message\":{\"type\":\"integer\",\"type\":\"string\",\"description\":\"Test field value.\",\"required\":true,\"nullable\":false}}")]
    [Arguments("{\"message\":{\"type\":\"string\",\"description\":\"Test field value.\",\"required\":false,\"required\":true,\"nullable\":false}}")]
    [Arguments("{\"message\":{\"type\":\"string\",\"description\":\"Test field value.\",\"required\":true,\"nullable\":false},\"message\":{\"type\":\"string\",\"description\":\"Test field value.\",\"required\":true,\"nullable\":false}}")]
    [Arguments("{\"payload\":{\"type\":\"object\",\"description\":\"Test field value.\",\"required\":true,\"nullable\":false,\"fields\":{\"v\":{\"type\":\"string\",\"description\":\"Test field value.\",\"required\":true,\"nullable\":false},\"v\":{\"type\":\"string\",\"description\":\"Test field value.\",\"required\":true,\"nullable\":false}}}}")]
    public async Task DuplicateDeclarationNamesAreRejected(string json)
    {
        using var fields = JsonDocument.Parse(json);
        await Assert.That(EntitySchemaRules.ValidFields(fields.RootElement)).IsFalse();
    }

    // SCHEMA-12, SCHEMA-14
    [Test]
    public async Task DuplicateNamesInsideArrayObjectsAreRejected()
    {
        using var fields = JsonDocument.Parse("""{"samples":{"type":"array","description":"Test field value.","required":true,"nullable":false,"items":{"type":"object","description":"Test field value.","nullable":false,"fields":{"value":{"type":"string","description":"Test field value.","required":true,"nullable":false}}}}}""");
        using var content = JsonDocument.Parse("""{"samples":[{"value":"before","value":"after"}]}""");
        await Assert.That(EntitySchemaRules.ValidEntity(fields.RootElement, content.RootElement)).IsFalse();
    }

    // SCHEMA-12, SCHEMA-14
    [Test]
    public async Task CaseDistinctNamesAndRepeatedArrayValuesRemainValid()
    {
        using var fields = JsonDocument.Parse("""{"labels":{"type":"array","description":"Test field value.","required":true,"nullable":false,"items":{"type":"string","description":"Test field value.","nullable":false}},"record":{"type":"object","description":"Test field value.","required":true,"nullable":false,"fields":{"x":{"type":"string","description":"Test field value.","required":true,"nullable":false},"X":{"type":"string","description":"Test field value.","required":true,"nullable":false}}}}""");
        using var content = JsonDocument.Parse("""{"labels":["same","same"],"record":{"x":"one","X":"two"}}""");
        await Assert.That(EntitySchemaRules.ValidFields(fields.RootElement)).IsTrue();
        await Assert.That(EntitySchemaRules.ValidEntity(fields.RootElement, content.RootElement)).IsTrue();
    }
}
