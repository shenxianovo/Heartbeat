using System.Diagnostics;

namespace Heartbeat.Dev;

internal sealed record ScenarioProcessResult(DateTimeOffset Started, DateTimeOffset Completed, int ExitCode);

// Real child process lifetime, usable independently of Docker, Collector and any scenario.
internal sealed class ScenarioProcess : IAsyncDisposable
{
    private readonly Process _process;
    public int Id => _process.Id;
    public Task<ScenarioProcessResult> Completion { get; }

    public ScenarioProcess(string directory, string fileName, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment = null, string? logPath = null)
    {
        var started = DateTimeOffset.UtcNow;
        _process = ProcessRunner.Start(directory, fileName, arguments, environment, redirectOutput: true);
        Completion = CompleteAsync(started, logPath);
    }

    public async Task<ScenarioProcessResult> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            return await Completion.WaitAsync(timeout, cancellationToken);
        }
        catch (Exception) when (!Completion.IsCompleted)
        {
            await KillAsync();
            throw;
        }
    }

    public async Task<ScenarioProcessResult> StopAsync(CancellationToken cancellationToken)
    {
        if (!_process.HasExited)
            await ProcessRunner.InterruptAsync(_process, TimeSpan.FromSeconds(15), cancellationToken);
        return await Completion;
    }

    private async Task<ScenarioProcessResult> CompleteAsync(DateTimeOffset started, string? logPath)
    {
        // Drain both pipes while the child runs, including guided sessions with no log retention.
        var stdout = _process.StandardOutput.ReadToEndAsync();
        var stderr = _process.StandardError.ReadToEndAsync();
        await _process.WaitForExitAsync();
        var result = new ScenarioProcessResult(started, DateTimeOffset.UtcNow, _process.ExitCode);
        var content = await stdout + await stderr;
        if (logPath is not null) await File.WriteAllTextAsync(logPath, content);
        return result;
    }

    public async Task KillAsync()
    {
        if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        await Completion;
    }

    public async ValueTask DisposeAsync()
    {
        try { await KillAsync(); }
        finally { _process.Dispose(); }
    }
}

internal static class ScenarioWait
{
    public static async Task UntilAsync(string expectation, Func<CancellationToken, Task<bool>> condition,
        CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(60));
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (!await condition(deadline.Token)) await timer.WaitForNextTickAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out waiting for {expectation}.");
        }
    }
}
