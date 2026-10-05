namespace Heartbeat.Recording;

public sealed class RecordingObject
{
    private RecordingObject() { }

    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }

    public static RecordingObject Create(Guid ownerId) => Register(Guid.CreateVersion7(), ownerId);

    public static RecordingObject Register(Guid id, Guid ownerId)
    {
        if (ownerId == Guid.Empty)
            throw new ArgumentException("An owner is required.", nameof(ownerId));
        if (id.Version != 7)
            throw new ArgumentException("An object ID must be a UUID v7.", nameof(id));
        return new RecordingObject { Id = id, OwnerId = ownerId };
    }
}
