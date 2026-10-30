using Orisumi.Core;
using Orisumi.Observer.Sdk;

namespace Orisumi.Observers.ApplicationIdentity;

internal static class Program
{
    public static async Task<int> Main()
    {
        await using var session = new PipeObserverSession(Console.In, Console.Out);
        try
        {
            var init = await session.Initialization;
            var settings = init.GetProperty("settings");
            var modelName = settings.TryGetProperty("model", out var model) ? model.GetString()! : "gpt-6.1-terra";
            var seconds = settings.TryGetProperty("intervalSeconds", out var interval) ? interval.GetDouble() : 60;
            if (!double.IsFinite(seconds) || seconds <= 0) throw new ArgumentException("intervalSeconds must be positive and finite.");
            var work = new IdentityWork(session, new(init.GetProperty("instanceId").GetGuid()), init.GetProperty("dataDirectory").GetString()!);
            using var http = new HttpClient();
            var recognizer = new IdentityRecognizer(new(session, work), work,
                new OpenAiApplicationModel(http, Environment.GetEnvironmentVariable("OPENAI_API_KEY"), modelName), modelName,
                init.GetProperty("packageVersion").GetString()!, Console.Error.WriteLine);
            // Restore the durable local handoff before ready; final delivery may wait while offline.
            await work.RestoreHandoffAsync(session.Stopping);
            await session.RequestAsync("ready", JsonLines.Json(new { }), cancellationToken: session.Stopping);
            while (!session.Stopping.IsCancellationRequested)
            {
                try { await work.ResumeAsync(session.Stopping); await recognizer.RunOnceAsync(session.Stopping); }
                catch (Exception exception) when (exception is HttpRequestException or ModelViolationException or IOException)
                { await Console.Error.WriteLineAsync(exception.Message); }
                await Task.Delay(TimeSpan.FromSeconds(seconds), session.Stopping);
            }
            return 0;
        }
        catch (OperationCanceledException) when (session.Stopping.IsCancellationRequested) { return 0; }
        catch (Exception exception) { await Console.Error.WriteLineAsync(exception.Message); return 1; }
    }
}
