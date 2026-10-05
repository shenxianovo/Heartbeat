namespace Heartbeat.Recording;

public sealed class Timeline
{
    private Timeline()
    {
    }

    public Guid Id { get; private set; }

    public RecordingObject Identity { get; private set; } = null!;

    public Guid OwnerId { get; private set; }

    public string DisplayName { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public static Timeline Create(
        Guid ownerId,
        string displayName,
        DateTimeOffset createdAt)
    {
        if (ownerId == Guid.Empty)
        {
            throw new ArgumentException("An owner is required.", nameof(ownerId));
        }

        var normalizedCreatedAt = createdAt.ToUniversalTime();

        var identity = RecordingObject.Create(ownerId);
        return new Timeline
        {
            Id = identity.Id,
            Identity = identity,
            OwnerId = ownerId,
            DisplayName = TextValue.NormalizeRequired(displayName, nameof(displayName)),
            CreatedAt = normalizedCreatedAt,
        };
    }

    public void UpdateDisplayName(string displayName)
    {
        DisplayName = TextValue.NormalizeRequired(displayName, nameof(displayName));
    }
}
