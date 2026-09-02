namespace Heartbeat.Desktop.UI.Presentation;

public interface IPresentationScheduler
{
    void Post(Action action);
}
