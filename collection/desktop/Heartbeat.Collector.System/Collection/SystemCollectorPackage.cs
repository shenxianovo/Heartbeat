namespace Heartbeat.Collector.System.Collection;

public static class SystemCollectorPackage
{
    public static string Path => global::System.IO.Path.Combine(
        AppContext.BaseDirectory,
        "CollectorPackages",
        "System");
}
