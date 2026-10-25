using System.CommandLine;
using Heartbeat.Observer.Sdk;

namespace Heartbeat.Observers.ForegroundState;

internal static class Program
{
    public static int Main(string[] args)
    {
        var run = RunAsync(args);
        // AppKit refreshes time-varying application state on the main run loop.
        if (OperatingSystem.IsMacOS())
            while (!run.IsCompleted) MacOSForegroundApplicationReader.PumpEvents();
        return run.GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var identityFile = new Option<string>("--identity-file")
        {
            Description = "Foreground state observer identity file; another file selects another instance.",
            DefaultValueFactory = _ => ForegroundStateIdentity.DefaultPath,
        };
        var applicationsFile = new Option<string>("--applications-file")
        {
            Description = "Device-local application identity registry, shared by observer instances on this device.",
            DefaultValueFactory = _ => LocalApplicationRegistry.DefaultPath,
        };
        var stateFile = new Option<string?>("--state-file")
        {
            Description = "SDK SQLite state; defaults to the identity path plus .sqlite.",
        };
        var backend = new Option<string>("--backend")
        {
            Description = "Heartbeat API base address, including /api/.",
            DefaultValueFactory = _ => "http://localhost:8080/api/",
        };
        var interval = new Option<int>("--interval-ms")
        {
            Description = "Fallback confirmation interval in milliseconds (100 through 60000).",
            DefaultValueFactory = _ => 1000,
        };
        var samples = new Option<int>("--samples")
        {
            Description = "Stop after this many sampling attempts; 0 runs until stopped.",
            DefaultValueFactory = _ => 0,
        };
        var command = new RootCommand("Collect and upload macOS foreground periods")
        {
            identityFile, applicationsFile, stateFile, backend, interval, samples,
        };
        command.SetAction(async (parsed, cancellationToken) =>
        {
            if (parsed.GetValue(interval) is < 100 or > 60000 || parsed.GetValue(samples) < 0)
            {
                Console.Error.WriteLine("--interval-ms must be 100 through 60000; --samples must be nonnegative.");
                return 2;
            }
            if (!OperatingSystem.IsMacOS())
                throw new PlatformNotSupportedException("Foreground application collection requires macOS.");
            var identityPath = Path.GetFullPath(parsed.GetValue(identityFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(identityPath)!);
            using var instanceLock = new FileStream(identityPath + ".lock", FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            var observerId = ForegroundStateIdentity.LoadOrCreate(identityPath);
            var statePath = Path.GetFullPath(parsed.GetValue(stateFile) ?? identityPath + ".sqlite");
            Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
            using var stateLock = new FileStream(statePath + ".lock", FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            var backendUri = new Uri(parsed.GetValue(backend)!.TrimEnd('/') + "/", UriKind.Absolute);
            if (backendUri.Scheme is not ("http" or "https")) throw new ArgumentException("--backend must be an HTTP address.");
            using var http = new HttpClient { BaseAddress = backendUri, Timeout = TimeSpan.FromSeconds(10) };
            using var sdk = new ObserverClient(statePath, observerId, http);
            var registration = await ForegroundRegistration.RegisterAsync(sdk, observerId, cancellationToken);
            var store = new ForegroundStore(sdk, observerId, registration);
            var applications = new LocalApplicationRegistry(parsed.GetValue(applicationsFile)!);
            var delivery = new ForegroundDelivery(sdk, applications, registration, Console.Error);
            store.FinishActive();
            var collector = new ForegroundCollector(observerId,
                applications, store,
                TimeSpan.FromMilliseconds(parsed.GetValue(interval)), MacOSForegroundApplicationReader.Read,
                Console.Out, Console.Error);
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var eventFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var events = new MacOSForegroundEvents(collector, exception => eventFailure.TrySetException(exception));
            var collection = Task.Run(() => collector.RunAsync(parsed.GetValue(samples), stopping.Token), CancellationToken.None);
            var sending = delivery.RunAsync(stopping.Token);
            try
            {
                var completed = await Task.WhenAny(collection, eventFailure.Task, sending);
                await completed;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Console.Error.WriteLine("Collection stopped. Unconfirmed versions remain in the SDK SQLite file.");
                return 130;
            }
            finally
            {
                stopping.Cancel();
                collector.Stop();
                try { await collection; }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
                try { await sending; }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
            }
            await delivery.SendAsync(cancellationToken);
            return 0;
        });
        try
        {
            var parsed = command.Parse(args);
            var result = await parsed.InvokeAsync(new InvocationConfiguration
            {
                EnableDefaultExceptionHandler = false,
            });
            return parsed.Errors.Count == 0 ? result : 2;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Collection cancelled.");
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }
}
