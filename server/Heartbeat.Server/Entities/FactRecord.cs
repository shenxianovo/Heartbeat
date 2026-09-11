using System.Text.Json;

namespace Heartbeat.Server.Entities;

/// <summary>One stored result; family types retain their own public time vocabulary.</summary>
public abstract class FactRecord : IFactRecord
{
    public Guid Id { get; set; }
    public string OwnerId { get; set; } = string.Empty;
    public string Kind { get; set; } = null!;
    public Guid StreamId { get; set; }
    public Guid FactId { get; set; }
    public long Revision { get; set; }
    public Guid? ObserverId { get; set; }
    public Guid? FoiId { get; set; }
    public string? Aspect { get; set; }
    public string? TargetKind { get; set; }
    public long? TargetId { get; set; }
    public string Source { get; set; } = string.Empty;
    public long? AppIdentityId { get; set; }
    public JsonDocument Payload { get; set; } = null!;
    public FactStream Stream { get; set; } = null!;
    public AppIdentity? AppIdentity { get; set; }
}
