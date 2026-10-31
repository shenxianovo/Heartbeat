using Orisumi.Observer.Sdk;

namespace Orisumi.Observers.ApplicationIdentity;

internal static class Program
{
    public static async Task<int> Main()
    {
        await using var context = DataClient.Connect();
        await using var data = context.Direct;
        try
        {
            var settings = context.Settings;
            var modelName = settings.TryGetProperty("model", out var model) ? model.GetString()! : "gpt-6.1-terra";
            var seconds = settings.TryGetProperty("intervalSeconds", out var interval) ? interval.GetDouble() : 60;
            if (!double.IsFinite(seconds) || seconds <= 0) throw new ArgumentException("intervalSeconds must be positive and finite.");
            using var http = new HttpClient();
            var recognizer = new IdentityRecognizer(data, new(data),
                new OpenAiApplicationModel(http, Environment.GetEnvironmentVariable("OPENAI_API_KEY"), modelName), modelName,
                context.PackageVersion!, Console.Error.WriteLine);
            while (!context.Stopping.IsCancellationRequested)
            {
                try { await recognizer.RunOnceAsync(context.Stopping); }
                catch (Exception exception) when (exception is HttpRequestException or WriteRejectedException)
                { await Console.Error.WriteLineAsync($"这一轮结束，下轮重试：{exception.Message}"); }
                await Task.Delay(TimeSpan.FromSeconds(seconds), context.Stopping);
            }
            return 0;
        }
        catch (OperationCanceledException) when (context.Stopping.IsCancellationRequested) { return 0; }
        catch (Exception exception) { await Console.Error.WriteLineAsync(exception.Message); return 1; }
    }
}
