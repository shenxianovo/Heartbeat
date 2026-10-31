using Orisumi.Core;

namespace Orisumi.Observers.ForegroundState;

internal sealed record ForegroundCapture(
    LocalApplication Application,
    ForegroundApplicationContent Content,
    DateTimeOffset StartAt,
    DateTimeOffset EndAt,
    string? TimeZone)
{
    internal static ForegroundCapture Create(
        LocalApplication application, ForegroundApplicationReading reading)
    {
        var content = new ForegroundApplicationContent
        {
            Name = reading.Name,
            Icon = ReadIcon(reading),
        };
        var time = VersionTime.Normalize(reading.ObservedAt);
        return new(application, content, time, time, reading.TimeZone);
    }

    private static Orisumi.Observer.Sdk.BinaryContent? ReadIcon(ForegroundApplicationReading reading)
    {
        try { return reading.ReadIcon?.Invoke(); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        { return null; }
    }

    internal ForegroundCapture Through(DateTimeOffset time) => this with { EndAt = VersionTime.Normalize(time) };
}
