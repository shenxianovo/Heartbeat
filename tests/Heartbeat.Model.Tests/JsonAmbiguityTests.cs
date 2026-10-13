using System.Text.Json;
using Heartbeat.Application.Entities;

namespace Heartbeat.Model.Tests;

public sealed class JsonAmbiguityTests
{
    [Test]
    [Arguments("{\"message\":\"first\",\"message\":\"second\"}")]
    [Arguments("{\"message\":\"first\",\"messa\\u0067e\":\"second\"}")]
    public async Task DuplicateContentNamesAreRejected(string properties)
    {
        using var fields = JsonDocument.Parse("""{"message":{"type":"string","required":true,"nullable":false}}""");
        using var refs = JsonDocument.Parse("{}");
        using var content = JsonDocument.Parse(properties);
        await Assert.That(EntitySchemaRules.ValidEntity(fields.RootElement, refs.RootElement, content.RootElement)).IsFalse();
    }

    [Test]
    [Arguments("{\"message\":{\"type\":\"integer\",\"type\":\"string\",\"required\":true,\"nullable\":false}}")]
    [Arguments("{\"message\":{\"type\":\"string\",\"required\":false,\"required\":true,\"nullable\":false}}")]
    [Arguments("{\"message\":{\"type\":\"string\",\"required\":true,\"nullable\":false},\"message\":{\"type\":\"string\",\"required\":true,\"nullable\":false}}")]
    [Arguments("{\"payload\":{\"type\":\"object\",\"required\":true,\"nullable\":false,\"fields\":{\"v\":{\"type\":\"string\",\"required\":true,\"nullable\":false},\"v\":{\"type\":\"string\",\"required\":true,\"nullable\":false}}}}")]
    public async Task DuplicateDeclarationNamesAreRejected(string json)
    {
        using var fields = JsonDocument.Parse(json);
        await Assert.That(EntitySchemaRules.ValidFields(fields.RootElement)).IsFalse();
    }

    [Test]
    public async Task DuplicateNamesInsideArrayObjectsAreRejected()
    {
        using var fields = JsonDocument.Parse("""{"samples":{"type":"array","required":true,"nullable":false,"items":{"type":"object","nullable":false,"fields":{"value":{"type":"string","required":true,"nullable":false}}}}}""");
        using var refs = JsonDocument.Parse("{}");
        using var content = JsonDocument.Parse("""{"samples":[{"value":"before","value":"after"}]}""");
        await Assert.That(EntitySchemaRules.ValidEntity(fields.RootElement, refs.RootElement, content.RootElement)).IsFalse();
    }

    [Test]
    public async Task CaseDistinctNamesAndRepeatedArrayValuesRemainValid()
    {
        using var fields = JsonDocument.Parse("""{"labels":{"type":"array","required":true,"nullable":false,"items":{"type":"string","nullable":false}},"record":{"type":"object","required":true,"nullable":false,"fields":{"x":{"type":"string","required":true,"nullable":false},"X":{"type":"string","required":true,"nullable":false}}}}""");
        using var refs = JsonDocument.Parse("{}");
        using var content = JsonDocument.Parse("""{"labels":["same","same"],"record":{"x":"one","X":"two"}}""");
        await Assert.That(EntitySchemaRules.ValidFields(fields.RootElement)).IsTrue();
        await Assert.That(EntitySchemaRules.ValidEntity(fields.RootElement, refs.RootElement, content.RootElement)).IsTrue();
    }
}
