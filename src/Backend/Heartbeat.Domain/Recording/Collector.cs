namespace Heartbeat.Recording;

public sealed class Collector
{
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
    {
        if (timelineId == Guid.Empty)
        {
            throw new ArgumentException("A timeline is required.", nameof(timelineId));
        }

        var normalizedKey = NormalizeKey(key);
        var normalizedCreatedAt = createdAt.ToUniversalTime();

        return new Collector
        {
            Id = Guid.CreateVersion7(normalizedCreatedAt),
            TimelineId = timelineId,
            Key = normalizedKey,
            Target = TextValue.NormalizeRequired(target, nameof(target)),
            DisplayName = TextValue.NormalizeRequired(displayName, nameof(displayName)),
            CreatedAt = normalizedCreatedAt,
        };
    }

    public void UpdateDisplayName(string displayName)
    {
        DisplayName = TextValue.NormalizeRequired(displayName, nameof(displayName));
    }

    private static string NormalizeKey(string key)
    {
        var normalized = TextValue.NormalizeRequired(key, nameof(key));
        var segments = normalized.Split('.');

        if (segments.Length < 2
            || segments.Any(segment => segment.Length == 0)
            || normalized.Any(character =>
                character is not (>= 'a' and <= 'z')
                and not (>= '0' and <= '9')
                and not '.'
                and not '-'
                and not '_'))
        {
            throw new ArgumentException(
                "A collector key must be a lowercase, dot-separated identifier.",
                nameof(key));
        }

        return normalized;
    }
}
