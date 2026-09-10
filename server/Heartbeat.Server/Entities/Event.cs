using System.Text.Json;

namespace Heartbeat.Server.Entities;

public sealed class Event : IFactRecord
{
    public Guid Id { get; set; }
    public string OwnerId { get; set; } = string.Empty;
    public Guid StreamId { get; set; }
    public Guid FactId { get; set; }
    public long Revision { get; set; }
    public Guid? ObserverId { get; set; }
    public string? TargetKind { get; set; }
    public long? TargetId { get; set; }
    public string Source { get; set; } = string.Empty;
    public long? AppIdentityId { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public JsonDocument Payload { get; set; } = null!;
    public FactStream Stream { get; set; } = null!;
    public AppIdentity? AppIdentity { get; set; }
}
