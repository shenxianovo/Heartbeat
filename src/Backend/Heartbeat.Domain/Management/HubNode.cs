namespace Heartbeat.Management;

public sealed class HubNode
{
    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public Guid SessionId { get; private set; }
    public DateTimeOffset LastSeenAt { get; private set; }
    public DateTimeOffset? RetiredAt { get; private set; }
    public string StatusJson { get; private set; } = "{}";
}
