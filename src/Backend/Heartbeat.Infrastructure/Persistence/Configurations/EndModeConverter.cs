using Heartbeat.Recording;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Heartbeat.Persistence.Configurations;

internal sealed class EndModeConverter : ValueConverter<EndMode, string>
{
    public EndModeConverter()
        : base(
            value => ToDatabase(value),
            value => FromDatabase(value))
    {
    }

    private static string ToDatabase(EndMode value) => value switch
    {
        EndMode.Explicit => "explicit",
        EndMode.NextRecord => "next_record",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown end mode."),
    };

    private static EndMode FromDatabase(string value) => value switch
    {
        "explicit" => EndMode.Explicit,
        "next_record" => EndMode.NextRecord,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown stored end mode."),
    };
}
