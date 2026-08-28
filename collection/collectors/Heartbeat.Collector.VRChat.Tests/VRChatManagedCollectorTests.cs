using System.Text.Json;

namespace Heartbeat.Collector.VRChat.Tests;

public sealed class VRChatManagedCollectorTests
{
    [Fact]
    public void ToFact_WorldNameIsUnknown_OmitsOptionalProperty()
    {
        var now = new DateTimeOffset(2026, 8, 28, 11, 34, 17, TimeSpan.Zero);
        var presence = new VRChatPresenceFact(
            Guid.CreateVersion7(),
            1,
            now,
            now,
            false,
            "traveling|traveling",
            "traveling",
            "traveling",
            null,
            "traveling");

        var fact = VRChatManagedCollector.ToFact(presence);

        Assert.Equal(JsonValueKind.Object, fact.Payload.ValueKind);
        Assert.False(fact.Payload.TryGetProperty("worldName", out _));
    }
}
