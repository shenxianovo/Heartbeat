using System.CommandLine;

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
        var api = new Option<string>("--api")
        {
            Description = "API base URL, including its prefix (for example http://localhost:8080/api/).",
            DefaultValueFactory = _ => "http://localhost:8080/api/",
        };
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
            Description = "SQLite active-period checkpoint and Outbox; defaults to the identity path plus .sqlite.",
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
        var command = new RootCommand("Collect macOS foreground periods locally and synchronize completed periods")
        {
            api, identityFile, applicationsFile, stateFile, interval, samples,
        };
        command.SetAction(async (parsed, cancellationToken) =>
        {
            if (!Uri.TryCreate(parsed.GetValue(api), UriKind.Absolute, out var url)
                || url.Scheme is not ("http" or "https")
                || url.Query.Length != 0 || url.Fragment.Length != 0)
            {
                Console.Error.WriteLine("--api must be an absolute HTTP(S) base URL without a query or fragment.");
                return 2;
            }
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
            using var client = new HttpClient
            {
                BaseAddress = new Uri(url.AbsoluteUri.TrimEnd('/') + "/"),
                Timeout = TimeSpan.FromSeconds(30),
            };
            var statePath = Path.GetFullPath(parsed.GetValue(stateFile) ?? identityPath + ".sqlite");
            Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
            using var stateLock = new FileStream(statePath + ".lock", FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            var store = new ForegroundStore(statePath, observerId);
            store.FinishActive();
            var collector = new ForegroundCollector(observerId,
                new LocalApplicationRegistry(parsed.GetValue(applicationsFile)!), store,
                TimeSpan.FromMilliseconds(parsed.GetValue(interval)), MacOSForegroundApplicationReader.Read,
                Console.Out, Console.Error);
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var eventFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var events = new MacOSForegroundEvents(collector, exception => eventFailure.TrySetException(exception));
            var sender = Task.Run(() => new OutboxSender(store, client, Console.Error).RunAsync(stopping.Token), CancellationToken.None);
            var collection = Task.Run(() => collector.RunAsync(parsed.GetValue(samples), stopping.Token), CancellationToken.None);
            try
            {
                var completed = await Task.WhenAny(collection, sender, eventFailure.Task);
                await completed;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Console.Error.WriteLine("Collection stopped. Completed periods remain locally until verified on the server.");
                return 130;
            }
            finally
            {
                stopping.Cancel();
                collector.Stop();
                try { await collection; }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
                try { await sender; }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
            }
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
            Console.Error.WriteLine("Collection or submission cancelled or timed out.");
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }
}
