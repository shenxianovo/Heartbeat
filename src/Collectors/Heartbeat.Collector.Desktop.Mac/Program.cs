using System.Net.Http.Headers;
using Heartbeat.Collector.Desktop.Mac.Diagnostics;
using Heartbeat.Hub;

namespace Heartbeat.Collector.Desktop.Mac;

public static class Program
{
    public static int Main(string[] args)
    {
        // Stay on the native main thread: an async Main would leave AppKit events unprocessed.
        var operation = MainAsync(args);
        return OperatingSystem.IsMacOS()
            ? MacRunLoop.Run(operation)
            : operation.GetAwaiter().GetResult();
    }

    private static async Task<int> MainAsync(string[] args)
    {
        // The probe only observes the system; it must not require a Hub endpoint or credentials.
        if (WindowTitleProbeOptions.IsRequested(args))
        {
            return await RunProbeAsync(args);
        }

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

        return await WithCancellationAsync(async cancellationToken =>
        {
            using var httpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                BaseAddress = options.HubBaseUrl,
            };
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.HubToken);
            var client = new HubSubmissionClient(httpClient);
            return await RunAsync(options, client, cancellationToken);
        });
    }

    private static async Task<int> RunProbeAsync(string[] args)
    {
        WindowTitleProbeOptions options;
        try
        {
            options = WindowTitleProbeOptions.Parse(args);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }

        if (!OperatingSystem.IsMacOS())
        {
            Console.Error.WriteLine("The window title probe reads macOS foreground state and requires macOS.");
            return 2;
        }

        return await WithCancellationAsync(async cancellationToken =>
        {
            using var source = new MacSystemObservationSource();
            var probe = new WindowTitleProbe(source, new MacContinuousTimeProvider());
            return await probe.RunAsync(options, Console.Out, cancellationToken);
        });
    }

    private static async Task<int> WithCancellationAsync(Func<CancellationToken, Task<int>> run)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            return await run(cancellation.Token);
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
        IDesktopObservationSource? source = null)
    {
        try
        {
            using var ownedSource = source is null ? new MacSystemObservationSource() : null;
            TimeProvider timeProvider = OperatingSystem.IsMacOS()
                ? new MacContinuousTimeProvider()
                : TimeProvider.System;
            var session = new DesktopCollectorSession("heartbeat.collector.desktop.macos", source ?? ownedSource!, client, timeProvider);
            await session.RunAsync(options.Collection, cancellationToken);

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
