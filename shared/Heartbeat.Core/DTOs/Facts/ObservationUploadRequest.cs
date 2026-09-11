using System.Text.Json;
using System.Text.Json.Serialization;

namespace Heartbeat.Core.DTOs.Facts;

/// <summary>Independent observations; no delivery Stream or Subject is required.</summary>
public sealed class ObservationUploadRequest
{
    public List<ObservationSnapshot> Facts { get; set; } = [];
}

/// <summary>A producer-owned identity and its complete, revisioned observation.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ObservationSnapshot
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = string.Empty;
    public Guid CollectorId { get; set; }
    public ObservationObjectReference? Foi { get; set; }
    public string? Aspect { get; set; }
    public JsonElement? Result { get; set; }
    public long Revision { get; set; }
    public DateTimeOffset? Start { get; set; }
    public DateTimeOffset? End { get; set; }
    public DateTimeOffset? OccurredAt { get; set; }
    public string? Source { get; set; }
    public List<FactRelationSnapshot> Relations { get; set; } = [];
}
