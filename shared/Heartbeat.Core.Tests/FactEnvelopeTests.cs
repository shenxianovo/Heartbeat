using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Heartbeat.Core.DTOs.Facts;
using Heartbeat.Core.Facts;

namespace Heartbeat.Core.Tests;

public sealed class FactEnvelopeTests
{
    [Theory]
    [InlineData("segment", true)]
    [InlineData("segment", false)]
    [InlineData("event", true)]
    [InlineData("event", false)]
    public void Upload_OldRetractionCannotDeserializeAsAnOrdinaryFact(string kind, bool includePayload)
    {
        var fact = new FactSnapshot
        {
            StreamId = Guid.CreateVersion7(), FactId = Guid.CreateVersion7(), Revision = 2, SchemaRevision = 1,
            Start = kind == "segment" ? DateTimeOffset.UnixEpoch : null,
            End = kind == "segment" ? DateTimeOffset.UnixEpoch.AddSeconds(1) : null,
            IsFinal = kind == "segment" ? true : null,
            OccurredAt = kind == "event" ? DateTimeOffset.UnixEpoch : null,
            Payload = JsonSerializer.SerializeToElement(new { identityKey = "work" })
        };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.SerializeToNode(new FactUploadRequest { Facts = [fact] }, options)!;
        var envelope = json["facts"]![0]!.AsObject();
        envelope["recordState"] = "retracted";
        if (!includePayload) envelope.Remove("payload");

        var error = Assert.Throws<JsonException>(() => json.Deserialize<FactUploadRequest>(options));
        Assert.Contains("recordState", error.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Schema_RetiredRetractionSwitchIsRejected(bool allowRetraction)
    {
        var schema = JsonNode.Parse("""
            {"documentVersion":1,"schemaId":"test.segment","schemaMajor":2,"schemaRevision":1,
             "factKind":"segment","evolution":{"mode":"segmentSnapshot"},
             "payloadSchemaDialect":"https://json-schema.org/draft/2020-12/schema","payloadSchema":true}
            """)!;
        schema["evolution"]!["allowRetraction"] = allowRetraction;

        var error = Assert.Throws<FactSchemaException>(() => FactSchemaContract.Parse(
            Encoding.UTF8.GetBytes(schema.ToJsonString()), "test.segment", 2, 1, "segment"));
        Assert.Contains("allowRetraction", error.Message);
    }
}
