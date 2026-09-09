namespace Heartbeat.Server.Entities;

/// <summary>Analytics owns the latest complete snapshot; legacy activity/input rows are derived projections.</summary>
public sealed class ObservedFact
{
    public Guid Id { get; set; }
    public string OwnerId { get; set; } = string.Empty;
    public Guid StreamId { get; set; }
    public Guid FactId { get; set; }
    public long Revision { get; set; }
    public string Origin { get; set; } = "native";
    public DateTimeOffset? ObservedAt { get; set; }
    public DateTimeOffset? Start { get; set; }
    public DateTimeOffset? End { get; set; }
    public DateTimeOffset? OccurredAt { get; set; }
    public bool? IsFinal { get; set; }
    public string? Payload { get; set; }
    /// <summary>Exact historical projected row, retained even when a native Fact takes over its identity.</summary>
    public string? LegacyRecord { get; set; }
    public Guid? LegacyId { get; set; }
    public long? LegacyDeviceId { get; set; }
    public string? LegacyKind { get; set; }
    public FactStream Stream { get; set; } = null!;
}

public sealed class FactSubjectRecord
{
    public string OwnerId { get; set; } = string.Empty;
    public Guid SubjectId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public long? DeviceId { get; set; }
    public string? DisplayName { get; set; }
    public Device? Device { get; set; }
}

public sealed class FactStream
{
    public string OwnerId { get; set; } = string.Empty;
    public Guid StreamId { get; set; }
    public Guid SubjectId { get; set; }
    public Guid? CollectorInstanceId { get; set; }
    public string OutputId { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string FactKind { get; set; } = string.Empty;
    public string Dimensions { get; set; } = "{}";
    public string Origin { get; set; } = "native";
    public FactSubjectRecord Subject { get; set; } = null!;
}

public sealed class FactGap
{
    public string OwnerId { get; set; } = string.Empty;
    public Guid StreamId { get; set; }
    public Guid GapId { get; set; }
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
    public string Reason { get; set; } = string.Empty;
    public int? EstimatedFactsLost { get; set; }
    public FactStream Stream { get; set; } = null!;
}
