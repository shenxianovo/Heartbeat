using System.Text.Json.Serialization;

namespace Heartbeat.Collector.VRChat;

public sealed class SegmentSnapshot
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = "vrchat.account";

    [JsonPropertyName("identityKey")]
    public string IdentityKey { get; set; } = string.Empty;

    [JsonPropertyName("appName")]
    public string AppName { get; set; } = "VRChat";

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("startTime")]
    public DateTimeOffset StartTime { get; set; }

    [JsonPropertyName("endTime")]
    public DateTimeOffset EndTime { get; set; }

    [JsonPropertyName("attributes")]
    public VRChatAttributes? Attributes { get; set; }
}

public sealed class VRChatAttributes
{
    [JsonPropertyName("worldId")]
    public string? WorldId { get; set; }

    [JsonPropertyName("worldName")]
    public string? WorldName { get; set; }

    [JsonPropertyName("instanceId")]
    public string? InstanceId { get; set; }
}
