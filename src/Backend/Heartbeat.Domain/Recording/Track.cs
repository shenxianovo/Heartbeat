namespace Heartbeat.Recording;

public sealed class Track
{
    private Track()
    {
    }

    public Guid Id { get; private set; }

    public RecordingObject Identity { get; private set; } = null!;

    public Guid CollectorId { get; private set; }

    public string Type { get; private set; } = string.Empty;

    public int Version { get; private set; }

    public TimeMode TimeMode { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static Track Create(
        Guid collectorId,
        RecordingObject identity,
        string type,
        int version,
        TimeMode timeMode,
        DateTimeOffset createdAt)
    {
        if (collectorId == Guid.Empty)
        {
            throw new ArgumentException("A collector is required.", nameof(collectorId));
        }

        ArgumentNullException.ThrowIfNull(identity);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        if (!Enum.IsDefined(timeMode))
        {
            throw new ArgumentOutOfRangeException(nameof(timeMode));
        }

        var normalizedCreatedAt = createdAt.ToUniversalTime();

        return new Track
        {
            Id = identity.Id,
            Identity = identity,
            CollectorId = collectorId,
            Type = TextValue.NormalizeRequired(type, nameof(type)),
            Version = version,
            TimeMode = timeMode,
            CreatedAt = normalizedCreatedAt,
        };
    }

    internal void EnsureAccepts(DateTimeOffset? endedAt)
    {
        var hasExplicitEnd = endedAt is not null;
        var acceptsEnd = TimeMode is TimeMode.Range;

        if (hasExplicitEnd != acceptsEnd)
        {
            throw new ArgumentException(
                "Range tracks require an end time; point tracks must not have one.",
                nameof(endedAt));
        }
    }
}
