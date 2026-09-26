namespace Heartbeat.Dev;

internal sealed record DiagnosticProbe<T>(T? Value, string? Error);

internal sealed class DesktopCollectionStep(DesktopSession desktop, StageArtifacts artifacts)
{
    public async Task<DesktopReplayEvidence> WaitAsync(DesktopObservationExpectation expectation,
        Func<CancellationToken, Task<ScenarioRecord[]>> readRecords, CancellationToken token)
    {
        var samples = new List<object>();
        DesktopReplayEvidence? witness = null;
        await ScenarioWait.UntilAsync("the controlled desktop application's Record", async cancellation =>
        {
            var foreground = await ProbeAsync(() => desktop.Ui.IsForegroundAsync(cancellation));
            var queue = await ProbeAsync(() => Task.FromResult(desktop.Queue));
            var records = await ProbeAsync(async () => expectation.Inspect(await readRecords(cancellation), DateTimeOffset.UtcNow));
            samples.Add(new { sampledAt = DateTimeOffset.UtcNow, processExited = desktop.HasExited, foreground, queue, matches = records });
            // Persist before the deadline/cleanup can discard the live profile and database.
            await artifacts.WriteAsync("collection.json", new { since = expectation.Since, samples }, CancellationToken.None);
            cancellation.ThrowIfCancellationRequested();
            witness = records.Value?.Witness;
            return witness is not null;
        }, token);
        return witness!;
    }

    private static async Task<DiagnosticProbe<T>> ProbeAsync<T>(Func<Task<T>> read)
    {
        try { return new(await read(), null); }
        catch (Exception error) { return new(default, error.GetType().Name); }
    }
}
