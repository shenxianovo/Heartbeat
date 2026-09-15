namespace Heartbeat.Collector.Desktop.Mac;

public sealed record ForegroundApplication(
    string Platform,
    string IdKind,
    string Id,
    string? DisplayName = null,
    int ProcessIdentifier = 0);
