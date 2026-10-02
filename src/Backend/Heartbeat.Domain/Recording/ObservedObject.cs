namespace Heartbeat.Recording;

public sealed class ObservedObject
{
    public Guid Id { get; private set; }
    public Guid TimelineId { get; private set; }
    public string Namespace { get; private set; } = string.Empty;
    public string Key { get; private set; } = string.Empty;
    public string? Name { get; private set; }
    public DateTimeOffset? NameObservedAt { get; private set; }
    public Guid? NameRecordId { get; private set; }
}

public sealed class RecordObject
{
    public Guid RecordId { get; private set; }
    public Guid ObjectId { get; private set; }
    public string Role { get; private set; } = string.Empty;
    public ObservedObject Subject { get; private set; } = null!;
}
