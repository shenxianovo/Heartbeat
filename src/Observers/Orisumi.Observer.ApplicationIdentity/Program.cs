using System.Globalization;

namespace Orisumi.Observers.ApplicationIdentity;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 == args.Length)
                throw new ArgumentException("Usage: --backend URL --data-dir PATH [--model MODEL] [--interval-seconds NUMBER]");
            options.Add(args[i][2..], args[i + 1]);
        }
        var backend = options.GetValueOrDefault("backend") ?? throw new ArgumentException("--backend is required.");
        var directory = options.GetValueOrDefault("data-dir") ?? throw new ArgumentException("--data-dir is required.");
        var modelName = options.GetValueOrDefault("model", "gpt-6.1-terra");
        var interval = TimeSpan.FromSeconds(double.Parse(options.GetValueOrDefault("interval-seconds", "60"), CultureInfo.InvariantCulture));
        if (interval <= TimeSpan.Zero) throw new ArgumentException("Polling interval must be positive.");
        Directory.CreateDirectory(directory);
        using var instanceLock = new FileStream(Path.Combine(directory, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        using var http = new HttpClient { BaseAddress = new Uri(backend.TrimEnd('/') + "/") };
        using var openai = new HttpClient();
        var state = new IdentityState(directory);
        var recognizer = new IdentityRecognizer(new(http, state), state,
            new OpenAiApplicationModel(openai, Environment.GetEnvironmentVariable("OPENAI_API_KEY"), modelName),
            modelName, typeof(IdentityRecognizer).Assembly.GetName().Version!.ToString(), Console.Error.WriteLine);
        try
        {
            do
            {
                await recognizer.RunOnceAsync(cancellation.Token);
                await Task.Delay(interval, cancellation.Token);
            } while (!cancellation.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }
}
