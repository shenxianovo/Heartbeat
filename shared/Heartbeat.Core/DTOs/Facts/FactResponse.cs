using System.Text.Json;

namespace Heartbeat.Core.DTOs.Facts;

/// <summary>Latest family fact; TargetId refers to owner-scoped Analytics business data.</summary>
public sealed class FactResponse
{
    public Guid Id { get; set; }
    public Guid StreamId { get; set; }
    public Guid FactId { get; set; }
    public long Revision { get; set; }
    public Guid? ObserverId { get; set; }
    public string? TargetKind { get; set; }
    public long? TargetId { get; set; }
    public string Source { get; set; } = string.Empty;
    public DateTimeOffset? Start { get; set; }
    public DateTimeOffset? End { get; set; }
    public DateTimeOffset? OccurredAt { get; set; }
    public JsonElement Payload { get; set; }
}
