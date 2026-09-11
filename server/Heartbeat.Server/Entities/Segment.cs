namespace Heartbeat.Server.Entities;

public sealed class Segment : FactRecord
{
    public DateTimeOffset StartTime { get; set; }
    public DateTimeOffset EndTime { get; set; }
}
