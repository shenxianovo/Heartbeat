using System.Text.Json;

namespace Heartbeat.Core.DTOs.Facts;

/// <summary>A self-contained, atomically accepted Collection → Analytics batch.</summary>
public sealed class FactUploadRequest
{
    public List<FactStreamDefinition> Streams { get; set; } = [];
    public List<FactSnapshot> Facts { get; set; } = [];
    public List<FactGapSnapshot> Gaps { get; set; } = [];
}

public sealed class FactStreamDefinition
{
    public Guid StreamId { get; set; }
    public Guid CollectorInstanceId { get; set; }
    public FactSubject Subject { get; set; } = new();
    public string OutputId { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string FactKind { get; set; } = string.Empty;
    public Dictionary<string, string> Dimensions { get; set; } = [];
}

public sealed class FactSubject
{
    public Guid SubjectId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string? HardwareId { get; set; }
    public string? DisplayName { get; set; }
}

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed class FactSnapshot
{
    public Guid StreamId { get; set; }
    public Guid FactId { get; set; }
    public long Revision { get; set; }
    public DateTimeOffset? ObservedAt { get; set; }
    public DateTimeOffset? Start { get; set; }
    public DateTimeOffset? End { get; set; }
    public DateTimeOffset? OccurredAt { get; set; }
    public bool? IsFinal { get; set; }
    public JsonElement? Payload { get; set; }
}

public sealed class FactGapSnapshot
{
    public Guid StreamId { get; set; }
    public Guid GapId { get; set; }
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
    public string Reason { get; set; } = string.Empty;
    public int? EstimatedFactsLost { get; set; }
}
