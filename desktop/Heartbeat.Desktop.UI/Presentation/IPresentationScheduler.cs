namespace Heartbeat.Desktop.UI.Presentation;

public interface IPresentationScheduler
{
    DateTimeOffset UtcNow { get; }
    void Post(Action action);
    IDisposable SchedulePeriodic(TimeSpan interval, Action action);
}
