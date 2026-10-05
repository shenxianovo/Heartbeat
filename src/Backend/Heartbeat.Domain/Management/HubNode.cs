using Heartbeat.Recording;

namespace Heartbeat.Management;

public sealed class HubNode
{
    public Guid Id { get; private set; }
    public RecordingObject Identity { get; private set; } = null!;
    public Guid SessionId { get; private set; }
    public DateTimeOffset LastSeenAt { get; private set; }
    public DateTimeOffset? RetiredAt { get; private set; }
    public string StatusJson { get; private set; } = "{}";
}
