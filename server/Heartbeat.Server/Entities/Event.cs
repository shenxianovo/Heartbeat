namespace Heartbeat.Server.Entities;

public sealed class Event : FactRecord
{
    public DateTimeOffset Timestamp { get; set; }
}
