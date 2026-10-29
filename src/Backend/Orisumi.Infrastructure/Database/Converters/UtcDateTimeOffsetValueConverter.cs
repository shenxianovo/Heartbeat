using Orisumi.Core;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Orisumi.Infrastructure.Database.Converters;

public sealed class UtcDateTimeOffsetValueConverter() : ValueConverter<DateTimeOffset, DateTimeOffset>(
    value => VersionTime.Normalize(value), value => VersionTime.Normalize(value));
