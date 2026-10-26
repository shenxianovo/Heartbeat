using Orisumi.Core;

namespace Orisumi.Observers.ForegroundState;

internal sealed record ForegroundCapture(
    EntityId ObserverId,
    LocalApplication Application,
    ForegroundApplicationContent Content,
    EntityId ObservationId,
    DateTimeOffset StartAt,
    DateTimeOffset EndAt,
    string? TimeZone)
{
    internal static ForegroundCapture Create(
        EntityId observerId, LocalApplication application, ForegroundApplicationReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        var content = new ForegroundApplicationContent
        {
            Id = EntityId.New(),
            LocalApplicationId = application.Id,
            Name = reading.Name,
        };
        var time = Microseconds(reading.ObservedAt);
        return new(observerId, application, content, EntityId.New(), time, time, reading.TimeZone);
    }

    internal ForegroundCapture Through(DateTimeOffset time) => this with { EndAt = Microseconds(time) };

    private static DateTimeOffset Microseconds(DateTimeOffset time) =>
        new(time.UtcTicks - time.UtcTicks % 10, TimeSpan.Zero);
}
