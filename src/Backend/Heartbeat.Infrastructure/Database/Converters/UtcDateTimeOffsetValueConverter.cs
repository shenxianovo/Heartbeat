using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Heartbeat.Infrastructure.Database.Converters;

public sealed class UtcDateTimeOffsetValueConverter : ValueConverter<DateTimeOffset, DateTimeOffset>
{
    public UtcDateTimeOffsetValueConverter()
        : base(value => ToMicroseconds(value), value => value.ToUniversalTime())
    {
    }

    internal static DateTimeOffset ToMicroseconds(DateTimeOffset value) =>
        new(value.UtcTicks - value.UtcTicks % TimeSpan.TicksPerMicrosecond, TimeSpan.Zero);
}
