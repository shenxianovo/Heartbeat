using System.Text.Json;

namespace Heartbeat.Core.DTOs.Facts;

/// <summary>Latest fact, its object, and relations evidenced by this exact fact.</summary>
public sealed class FactResponse : ObservationResponse
{
    public Guid? StreamId { get; set; }
    public Guid? FactId { get; set; }
    public long Revision { get; set; }
    public string? Aspect { get; set; }


    public long? DeviceId { get; set; }
    public long? AppId { get; set; }
    public string? Source { get; set; }
    public string Kind { get; set; } = string.Empty;
    public JsonElement Result { get => Payload; set => Payload = value; }
    public DateTimeOffset? Start { get; set; }
    public DateTimeOffset? End { get; set; }
    public DateTimeOffset? OccurredAt { get; set; }
    public JsonElement Payload { get; set; }
}
