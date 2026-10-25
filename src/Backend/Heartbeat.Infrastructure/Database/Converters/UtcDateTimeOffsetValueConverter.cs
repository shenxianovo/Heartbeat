using Heartbeat.Core;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Heartbeat.Infrastructure.Database.Converters;

public sealed class UtcDateTimeOffsetValueConverter() : ValueConverter<DateTimeOffset, DateTimeOffset>(
    value => Normalize(value), value => Normalize(value))
{
    public static DateTimeOffset Normalize(DateTimeOffset value) => VersionTime.Normalize(value);
}
