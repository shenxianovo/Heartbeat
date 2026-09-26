using System.Text.Json;

namespace Heartbeat.Dev;

internal enum DesktopStage
{
    Authentication, PackageAndServices, FirstLaunchAndConfigure, FirstCollection, NormalWebReplay,
    OfflineCustody, ForcedExitAndRestart, DeliveryRecovery, RecoveredWebReplay, CleanQuit,
}

internal sealed record StageArtifacts(string Directory)
{
    public string File(string name) => Path.Combine(Directory, name);
    public Task WriteAsync(string name, object value, CancellationToken token) =>
        System.IO.File.WriteAllTextAsync(File(name), JsonSerializer.Serialize(value, JsonOptions.Indented), token);
}

// Records the explicit sequence chosen by the scenario; it does not schedule or retry steps.
internal sealed class DesktopJourneyReport(ScenarioEnvironment environment, TextWriter output)
{
    private readonly List<object> _completed = [];

    public Task RunAsync(DesktopStage stage, Func<StageArtifacts, Task> action) =>
        RunAsync(stage, async artifacts => { await action(artifacts); return true; });

    public async Task<T> RunAsync<T>(DesktopStage stage, Func<StageArtifacts, Task<T>> action)
    {
        var name = JsonNamingPolicy.KebabCaseLower.ConvertName(stage.ToString());
        var artifacts = new StageArtifacts(Path.Combine(environment.Evidence.Run.Directory, name));
        Directory.CreateDirectory(artifacts.Directory);
        var started = DateTimeOffset.UtcNow;
        await output.WriteLineAsync($"Desktop journey: {name}");
        await SaveAsync(name, "running", started, null);
        try
        {
            var result = await action(artifacts);
            _completed.Add(new { stage = name, started, completed = DateTimeOffset.UtcNow, artifacts = name });
            await SaveAsync(name, "succeeded", started, null);
            return result;
        }
        catch (Exception error)
        {
            // Exception messages can contain native payloads or credentials.
            await SaveAsync(name, error is OperationCanceledException ? "cancelled" : "failed", started, error.GetType().Name);
            throw;
        }
    }

    private Task SaveAsync(string stage, string status, DateTimeOffset started, string? error) =>
        environment.WriteAsync("journey.json", new { stage, status, started, error,
            passed = stage == JsonNamingPolicy.KebabCaseLower.ConvertName(DesktopStage.CleanQuit.ToString()) && status == "succeeded",
            completed = _completed }, CancellationToken.None);
}
