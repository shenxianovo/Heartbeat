namespace Heartbeat.Recording;

public sealed class Track
{
    private Track()
    {
    }

    public Guid Id { get; private set; }

    public Guid CollectorId { get; private set; }

    public string Type { get; private set; } = string.Empty;

    public int Version { get; private set; }

    public TimeMode TimeMode { get; private set; }

    public EndMode? EndMode { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static Track Create(
        Guid collectorId,
        string type,
        int version,
        TimeMode timeMode,
        EndMode? endMode,
        DateTimeOffset createdAt)
    {
        if (collectorId == Guid.Empty)
        {
            throw new ArgumentException("A collector is required.", nameof(collectorId));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        ValidateTimeMode(timeMode, endMode);

        var normalizedCreatedAt = createdAt.ToUniversalTime();

        return new Track
        {
            Id = Guid.CreateVersion7(normalizedCreatedAt),
            CollectorId = collectorId,
            Type = TextValue.NormalizeRequired(type, nameof(type)),
            Version = version,
            TimeMode = timeMode,
            EndMode = endMode,
            CreatedAt = normalizedCreatedAt,
        };
    }

    internal void EnsureAccepts(DateTimeOffset? endedAt)
    {
        var hasExplicitEnd = endedAt is not null;
        var acceptsEnd = TimeMode is TimeMode.Range
            && EndMode is global::Heartbeat.Recording.EndMode.Explicit;

        if (hasExplicitEnd != acceptsEnd)
        {
            throw new ArgumentException(
                "An end time is required only for range tracks with an explicit end mode.",
                nameof(endedAt));
        }
    }

    private static void ValidateTimeMode(TimeMode timeMode, EndMode? endMode)
    {
        if (!Enum.IsDefined(timeMode))
        {
            throw new ArgumentOutOfRangeException(nameof(timeMode));
        }

        if (endMode is not null && !Enum.IsDefined(endMode.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(endMode));
        }

        if ((timeMode is TimeMode.Point && endMode is not null)
            || (timeMode is TimeMode.Range && endMode is null))
        {
            throw new ArgumentException(
                "Point tracks cannot have an end mode, and range tracks require one.",
                nameof(endMode));
        }
    }
}
