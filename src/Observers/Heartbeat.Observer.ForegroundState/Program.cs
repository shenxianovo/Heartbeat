using System.CommandLine;
using System.Text.Json;

namespace Heartbeat.Observers.ForegroundState;

internal static class Program
{
    public static async Task<int> Main(string[] args)
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
        var command = new RootCommand("Read the macOS foreground application once, submit it, and verify read-back")
        {
            api, identityFile,
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
            var reading = MacOSForegroundApplicationReader.Read();
            var observerId = ForegroundStateIdentity.LoadOrCreate(parsed.GetValue(identityFile)!);
            var capture = ForegroundCapture.Create(observerId, reading);
            Console.Error.WriteLine($"ObserverId={observerId.Value} SchemaId={ForegroundCapture.Schema.Id.Value} "
                + $"DataId={capture.Data.Id.Value} ObservationId={capture.Observation.Id.Value}");
            using var client = new HttpClient
            {
                BaseAddress = new Uri(url.AbsoluteUri.TrimEnd('/') + "/"),
                Timeout = TimeSpan.FromSeconds(30),
            };
            await capture.SubmitAndVerifyAsync(client, Console.Error, cancellationToken);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                observerId = observerId.Value,
                schemaId = ForegroundCapture.Schema.Id.Value,
                dataId = capture.Data.Id.Value,
                observationId = capture.Observation.Id.Value,
                observedAt = capture.Observation.StartAt?.UtcDateTime,
                capture.Observation.TimeZone,
                application = new { reading.BundleIdentifier, reading.Name, reading.ExecutablePath },
                verified = true,
            }, JsonSerializerOptions.Web));
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
