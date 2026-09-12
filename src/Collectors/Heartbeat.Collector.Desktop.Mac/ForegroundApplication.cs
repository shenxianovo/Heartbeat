namespace Heartbeat.Collector.Desktop.Mac;

public sealed record ForegroundApplication(string Platform, string IdKind, string Id);

public interface IForegroundApplicationReader
{
    ForegroundApplication? Read();
}
