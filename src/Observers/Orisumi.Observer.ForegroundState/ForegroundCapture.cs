using Orisumi.Core;

namespace Orisumi.Observers.ForegroundState;

internal sealed record ForegroundCapture(
    LocalApplication Application,
    ForegroundApplicationContent Content,
    EntityId ObservationId,
    DateTimeOffset StartAt,
    DateTimeOffset EndAt,
    string? TimeZone)
{
    internal static ForegroundCapture Create(
        LocalApplication application, ForegroundApplicationReading reading)
    {
        var content = new ForegroundApplicationContent
        {
            Id = EntityId.New(),
            Name = reading.Name,
        };
        var time = VersionTime.Normalize(reading.ObservedAt);
        return new(application, content, EntityId.New(), time, time, reading.TimeZone);
    }

    internal ForegroundCapture Through(DateTimeOffset time) => this with { EndAt = VersionTime.Normalize(time) };
}
