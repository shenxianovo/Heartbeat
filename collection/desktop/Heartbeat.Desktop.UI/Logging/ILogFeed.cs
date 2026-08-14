using Serilog.Events;

namespace Heartbeat.Desktop.UI.Logging;

public readonly record struct LogEntry(string Message, LogEventLevel Level);

public interface ILogFeed
{
    int Capacity { get; }
    event Action<IReadOnlyList<LogEntry>>? Changed;
    IReadOnlyList<LogEntry> GetAll();
}
