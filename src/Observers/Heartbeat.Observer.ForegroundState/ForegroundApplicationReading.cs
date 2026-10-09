namespace Heartbeat.Observers.ForegroundState;

internal sealed record ForegroundApplicationReading(
    string? BundleIdentifier,
    string? Name,
    string? ExecutablePath,
    DateTimeOffset ObservedAt,
    string? TimeZone);
