namespace Heartbeat.Recording;

public sealed class ObjectBinding
{
    public long Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public Guid? ScopeId { get; private set; }
    public Guid ObjectId { get; private set; }
    public string Namespace { get; private set; } = string.Empty;
    public string Key { get; private set; } = string.Empty;
}

public sealed class ObjectDescription
{
    public Guid ObjectId { get; private set; }
    public string? Name { get; private set; }
    public DateTimeOffset ObservedAt { get; private set; }
    public Guid RecordId { get; private set; }
}

public sealed class RecordObject
{
    public Guid RecordId { get; private set; }
    public int ReferenceIndex { get; private set; }
    public Guid ObjectId { get; private set; }
    public string Role { get; private set; } = string.Empty;
}
