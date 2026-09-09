using System.Text.Json;
using Heartbeat.Core.DTOs.Facts;

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
            StreamId = Guid.CreateVersion7(), FactId = Guid.CreateVersion7(), Revision = 2,
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

}
