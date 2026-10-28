using Orisumi.Core;
using Orisumi.Observer.Sdk;

namespace Orisumi.Observers.ForegroundState;

internal static class Program
{
    public static int Main()
    {
        var run = RunAsync();
        // AppKit delivers application and sleep events on the main run loop.
        if (OperatingSystem.IsMacOS())
            while (!run.IsCompleted) MacOSForegroundApplicationReader.PumpEvents();
        return run.GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync()
    {
        try
        {
            if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Foreground collection requires macOS.");
            await using var session = new PipeObserverSession(Console.In, Console.Out);
            var init = await session.Initialization;
            if (init.GetProperty("protocol").GetInt32() != 1) throw new InvalidDataException("Unsupported Runtime protocol.");
            var observer = new EntityId(init.GetProperty("instanceId").GetGuid());
            var shared = init.GetProperty("sharedDataDirectory").GetString()!;
            var settings = init.GetProperty("settings");
            var interval = settings.TryGetProperty("intervalMs", out var setting) ? setting.GetInt32() : 1000;
            if (interval is < 100 or > 60000) throw new ArgumentException("intervalMs must be 100 through 60000.");
            var registration = await ForegroundRegistration.RegisterAsync(session, observer, init.GetProperty("packageVersion").GetString(), session.Stopping);
            var store = new ForegroundStore(session, observer, registration);
            var applications = new LocalApplicationRegistry(Path.Combine(shared, "local-applications.json"));
            var delivery = new ForegroundDelivery(session, applications, registration, TextWriter.Null);
            store.FinishActive();
            var collector = new ForegroundCollector(observer, applications, store,
                TimeSpan.FromMilliseconds(interval), MacOSForegroundApplicationReader.Read, TextWriter.Null, Console.Error);
            var eventFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var events = new MacOSForegroundEvents(collector, exception => eventFailure.TrySetException(exception));
            await session.RequestAsync("ready", JsonLines.Json(new { }), cancellationToken: session.Stopping);
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(session.Stopping);
            var collection = Task.Run(() => collector.RunAsync(0, stopping.Token), CancellationToken.None);
            var repairing = delivery.RunAsync(stopping.Token);
            try { await await Task.WhenAny(collection, eventFailure.Task, repairing); }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
            finally
            {
                await stopping.CancelAsync();
                // During normal stop the host still acknowledges final local writes.
                collector.Stop();
                try { await Task.WhenAll(collection, repairing); }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
            }
            return 0;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync(exception.Message);
            return 1;
        }
    }
}
