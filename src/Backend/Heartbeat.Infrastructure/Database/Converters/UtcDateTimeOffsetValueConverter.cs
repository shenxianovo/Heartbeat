using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Heartbeat.Infrastructure.Database.Converters;

public sealed class UtcDateTimeOffsetValueConverter : ValueConverter<DateTimeOffset, DateTimeOffset>
{
    public UtcDateTimeOffsetValueConverter()
        : base(value => value.ToUniversalTime(), value => value.ToUniversalTime())
    {
    }
}
