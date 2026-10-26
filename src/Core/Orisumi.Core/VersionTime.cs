using System.Globalization;

namespace Orisumi.Core;

public static class VersionTime
{
    public static DateTimeOffset Normalize(DateTimeOffset value) =>
        new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);

    public static string Format(DateTimeOffset value) =>
        Normalize(value).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    public static VersionSubmission Normalize(VersionSubmission version) => version.Content is Observation observation
        ? version with { Content = observation with
        {
            StartAt = observation.StartAt is { } start ? Normalize(start) : null,
            EndAt = observation.EndAt is { } end ? Normalize(end) : null,
        } }
        : version;
}
