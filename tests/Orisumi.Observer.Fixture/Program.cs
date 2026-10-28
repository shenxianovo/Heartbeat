using Orisumi.Observer.Sdk;

namespace Orisumi.Observer.Fixture;

public static class Program
{
    public static async Task Main()
    {
        await using var session = new PipeObserverSession(Console.In, Console.Out);
        await session.Initialization;
        await session.RequestAsync("ready", JsonLines.Json(new { }));
        try { await Task.Delay(Timeout.InfiniteTimeSpan, session.Stopping); }
        catch (OperationCanceledException) when (session.Stopping.IsCancellationRequested) { }
    }
}
