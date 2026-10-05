using Heartbeat.Recording;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Heartbeat.Persistence.Configurations;

internal sealed class TimeModeConverter : ValueConverter<TimeMode, string>
{
    public TimeModeConverter()
        : base(
            value => ToDatabase(value),
            value => FromDatabase(value))
    {
    }

    private static string ToDatabase(TimeMode value) => value switch
    {
        TimeMode.Point => "point",
        TimeMode.Range => "range",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown time mode."),
    };

    private static TimeMode FromDatabase(string value) => value switch
    {
        "point" => TimeMode.Point,
        "range" => TimeMode.Range,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown stored time mode."),
    };
}
