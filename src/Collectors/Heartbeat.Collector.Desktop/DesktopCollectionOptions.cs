namespace Heartbeat.Collector.Desktop;

public sealed record DesktopCollectionOptions(
    string Target,
    string DisplayName,
    TimeSpan Interval,
    TimeSpan MaximumConfirmationGap,
    TimeSpan WindowTitleDwell,
    bool Once = false);
