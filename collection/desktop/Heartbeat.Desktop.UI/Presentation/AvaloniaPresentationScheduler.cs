using Avalonia.Threading;

namespace Heartbeat.Desktop.UI.Presentation;

public sealed class AvaloniaPresentationScheduler : IPresentationScheduler
{
    public void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }
}
