using System.Text.Json;

namespace Heartbeat.Server.Entities;

/// <summary>Shared update contract; each concrete family owns its EF mapping and time fields.</summary>
public interface IFactRecord
{
    Guid Id { get; set; }
    string OwnerId { get; set; }
    Guid StreamId { get; set; }
    Guid FactId { get; set; }
    long Revision { get; set; }
    string? Aspect { get; set; }
    Guid? ObserverId { get; set; }
    string? TargetKind { get; set; }
    long? TargetId { get; set; }
    string Source { get; set; }
    long? AppIdentityId { get; set; }
    JsonDocument Payload { get; set; }
    FactStream Stream { get; set; }
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
