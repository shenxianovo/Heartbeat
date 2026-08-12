using Avalonia.Threading;

namespace Heartbeat.Desktop.UI.Presentation;

public sealed class AvaloniaPresentationScheduler : IPresentationScheduler
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    public IDisposable SchedulePeriodic(TimeSpan interval, Action action)
    {
        var timer = new DispatcherTimer(interval, DispatcherPriority.Background, (_, _) => action());
        timer.Start();
        return new TimerRegistration(timer);
    }

    private sealed class TimerRegistration(DispatcherTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }
}
