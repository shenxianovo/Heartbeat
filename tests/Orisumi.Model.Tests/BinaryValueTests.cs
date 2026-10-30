using System.Text.Json;
using Orisumi.Core;

namespace Orisumi.Model.Tests;

public sealed class BinaryValueTests
{
    [Test, Property("caseId", "api-binary-content")]
    [Arguments("3.0", true)]
    [Arguments("3e0", true)]
    [Arguments("3.00000000000000000000000000001", false)]
    [Arguments("9223372036854775808", false)]
    public async Task BinarySizeUsesIntegerValueRatherThanNumberSpelling(string size, bool valid)
    {
        var value = JsonSerializer.Deserialize<JsonElement>("{\"digest\":\"" + BinaryValue.ComputeDigest([1, 2, 3])
            + "\",\"mediaType\":\"image/png\",\"size\":" + size + "}");
        await Assert.That(BinaryValue.IsValid(value)).IsEqualTo(valid);
        if (valid) await Assert.That(BinaryValue.Read(value).Size).IsEqualTo(3);
    }

    [Test, Property("caseId", "api-binary-content")]
    public async Task BinaryFieldsAreEnumeratedOnlyAtSchemaDeclaredPositions()
    {
        var descriptor = new BinaryValue(BinaryValue.ComputeDigest([1, 2, 3]), "image/png", 3);
        var fields = JsonSerializer.Deserialize<JsonElement>("""
            {"file":{"type":"binary","description":"File","required":true,"nullable":false},
             "text":{"type":"string","description":"Text","required":true,"nullable":false},
             "files":{"type":"array","description":"Files","required":true,"nullable":false,
               "items":{"type":"object","description":"Item","nullable":false,"fields":{
                 "file":{"type":"binary","description":"File","required":true,"nullable":true}}}}}
            """);
        var schema = new EntitySchema("Example", "Example", "example", fields);
        var content = JsonSerializer.SerializeToElement(new { file = descriptor, text = descriptor.Digest,
            files = new[] { new { file = (BinaryValue?)descriptor }, new { file = (BinaryValue?)null } } }, JsonSerializerOptions.Web);
        var version = new VersionSubmission(EntityId.New(), VersionId.New(), null, VersionId.New(), new BusinessContent(content));
        VersionRules.Validate(version, schema);
        var found = BinaryValue.Collect(version, schema);
        await Assert.That(found.Count).IsEqualTo(2);
        await Assert.That(found.All(value => value == descriptor)).IsTrue();
        await Assert.That(VersionRules.GetReferences(version, schema).Count).IsEqualTo(1);
    }
}
