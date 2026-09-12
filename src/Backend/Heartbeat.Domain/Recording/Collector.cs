namespace Heartbeat.Recording;

public sealed class Collector
{
    public const int MaximumTextLength = 255;

    private Collector()
    {
    }

    public Guid Id { get; private set; }

    public Guid TimelineId { get; private set; }

    public string Key { get; private set; } = string.Empty;

    public string Target { get; private set; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public static Collector Create(
        Guid timelineId,
        string key,
        string target,
        string displayName,
        DateTimeOffset createdAt)
        => Create(
            timelineId,
            CollectorRegistration.Create(key, target, displayName),
            createdAt);

    public static Collector Create(
        Guid timelineId,
        CollectorRegistration registration,
        DateTimeOffset createdAt)
    {
        if (timelineId == Guid.Empty)
        {
            throw new ArgumentException("A timeline is required.", nameof(timelineId));
        }

        ArgumentNullException.ThrowIfNull(registration);
        var normalizedCreatedAt = createdAt.ToUniversalTime();

        return new Collector
        {
            Id = Guid.CreateVersion7(normalizedCreatedAt),
            TimelineId = timelineId,
            Key = registration.Key,
            Target = registration.Target,
            DisplayName = registration.DisplayName,
            CreatedAt = normalizedCreatedAt,
        };
    }

    public void UpdateDisplayName(string displayName)
    {
        DisplayName = TextValue.NormalizeRequired(displayName, nameof(displayName), MaximumTextLength);
    }
}
