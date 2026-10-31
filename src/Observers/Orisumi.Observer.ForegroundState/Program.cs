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
            await using var data = DataClient.Connect();
            var settings = data.Settings;
            await ForegroundRegistration.RegisterAsync(data, token: data.Stopping);
            if (data.Device is not { } device)
            {
                await Console.Error.WriteLineAsync("读不到这台设备的标识，前台采集等待中。");
                try { await Task.Delay(Timeout.InfiniteTimeSpan, data.Stopping); }
                catch (OperationCanceledException) when (data.Stopping.IsCancellationRequested) { }
                return 0;
            }
            var interval = settings.TryGetProperty("intervalMs", out var setting) ? setting.GetInt32() : 1000;
            if (interval is < 100 or > 60000) throw new ArgumentException("intervalMs must be 100 through 60000.");
            var store = new ForegroundStore(data, device, Console.Error.WriteLine);
            var collector = new ForegroundCollector(new ApplicationKeys(device.Key.Value), store,
                TimeSpan.FromMilliseconds(interval), MacOSForegroundApplicationReader.Read, Console.Error);
            var eventFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var events = new MacOSForegroundEvents(collector, exception => eventFailure.TrySetException(exception));
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(data.Stopping);
            var collection = Task.Run(() => collector.RunAsync(0, stopping.Token), CancellationToken.None);
            try { await await Task.WhenAny(collection, eventFailure.Task); }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
            finally
            {
                await stopping.CancelAsync();
                // A normal stop submits the ongoing period before the process exits.
                collector.Stop();
                try { await collection; }
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
