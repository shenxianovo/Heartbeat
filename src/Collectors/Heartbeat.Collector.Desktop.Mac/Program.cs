using System.Net.Http.Headers;
using Heartbeat.Hub;

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
            using var httpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                BaseAddress = options.HubBaseUrl,
            };
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.HubToken);
            var client = new HubSubmissionClient(httpClient);
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
        HubSubmissionClient client,
        CancellationToken cancellationToken,
        IForegroundApplicationReader? reader = null)
    {
        try
        {
            var session = new DesktopCollectorSession(reader ?? new MacForegroundApplicationReader(), client, TimeProvider.System);
            await session.RunAsync(options, cancellationToken);

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
