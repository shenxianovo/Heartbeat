namespace Heartbeat.Recording;

public sealed class Collector
{
    public const int MaximumTextLength = 255;

    private Collector()
    {
    }

    public Guid Id { get; private set; }

    public RecordingObject Identity { get; private set; } = null!;

    public Guid TimelineId { get; private set; }

    public string Key { get; private set; } = string.Empty;

    public string Target { get; private set; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public static Collector Create(
        Guid timelineId,
        RecordingObject identity,
        string key,
        string target,
        string displayName,
        DateTimeOffset createdAt)
        => Create(
            timelineId,
            identity,
            CollectorRegistration.Create(key, target, displayName),
            createdAt);

    public static Collector Create(
        Guid timelineId,
        RecordingObject identity,
        CollectorRegistration registration,
        DateTimeOffset createdAt)
    {
        if (timelineId == Guid.Empty)
        {
            throw new ArgumentException("A timeline is required.", nameof(timelineId));
        }

        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(registration);
        var normalizedCreatedAt = createdAt.ToUniversalTime();

        return new Collector
        {
            Id = identity.Id,
            Identity = identity,
            TimelineId = timelineId,
            Key = registration.Key,
            Target = registration.Target,
            DisplayName = registration.DisplayName,
            CreatedAt = normalizedCreatedAt,
        };
    }

}
