namespace Heartbeat.Collector.Desktop.Mac;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        CollectorOptions options;
        try
        {
            options = CollectorOptions.Parse(args, ReadEnvironment());
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or UriFormatException)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            using var httpClient = HeartbeatRecordingClient.CreateHttpClient(
                options.ApiBaseUrl,
                options.AuthToken);
            var client = new HeartbeatRecordingClient(httpClient);
            return await RunAsync(options, client, cancellation.Token);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    internal static async Task<int> RunAsync(
        CollectorOptions options,
        HeartbeatRecordingClient client,
        CancellationToken cancellationToken)
    {
        try
        {
            var collectorId = await client.RegisterCollectorAsync(options.Target, options.DisplayName, cancellationToken);
            var trackId = await client.ResolveForegroundTrackAsync(collectorId, cancellationToken);
            Console.WriteLine($"Collector {collectorId} is writing Track {trackId}.");

            var reader = new MacForegroundApplicationReader();
            var session = new DesktopCollectorSession(reader, client, TimeProvider.System);
            await session.RunAsync(trackId, options, cancellationToken);

            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static Dictionary<string, string?> ReadEnvironment() =>
        Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(
                item => (string)item.Key,
                item => item.Value as string,
                StringComparer.OrdinalIgnoreCase);
}
