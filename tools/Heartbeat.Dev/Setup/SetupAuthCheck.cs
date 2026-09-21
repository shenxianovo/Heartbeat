using System.Diagnostics;

namespace Heartbeat.Dev;

/// <summary>Runs the one-shot checker with bounded waits and secret-free progress.</summary>
internal sealed class SetupAuthCheck(
    RepositoryContext repository, IProcessRunner runner, TextWriter output, TimeSpan? stepTimeout = null)
{
    private static readonly string[] ConfigurationKeys =
        ["AUTH_AUTHORITY", "HEARTBEAT_API_KEY", "HEARTBEAT_OWNER_ID", "HEARTBEAT_HUB_TOKEN",
         "HEARTBEAT_COLLECTOR_TARGET", "HEARTBEAT_COLLECTOR_DISPLAY_NAME"];

    public async Task<string> RunAsync(string envFile, CancellationToken token)
    {
        var compose = ComposeInvocation.Create(repository, envFile, release: true);
        var environment = ConfigurationKeys.ToDictionary(key => key, _ => (string?)null);
        await RunStepAsync("Building Hub authentication checker", [.. compose, "build", "hub"],
            environment, TimeSpan.FromMinutes(10), token);

        var container = "heartbeat-auth-check-" + Guid.NewGuid().ToString("N");
        try
        {
            var result = await RunStepAsync("Validating API key with Auth",
                [.. compose, "run", "--rm", "--no-deps", "--name", container, "hub", "--check-auth"],
                environment, TimeSpan.FromMinutes(1), token);
            return result.StdOut;
        }
        catch
        {
            await RemoveCheckerAsync(container);
            throw;
        }
    }

    private async Task<ProcessResult> RunStepAsync(string label, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment, TimeSpan timeout, CancellationToken token)
    {
        timeout = stepTimeout ?? timeout;
        await output.WriteLineAsync($"  {label} (timeout {timeout.TotalSeconds:0}s)...");
        var elapsed = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        try
        {
            var running = runner.CaptureAsync("docker", arguments, environment, deadline.Token);
            while (true)
            {
                try
                {
                    // Let CaptureAsync finish cancelling the Docker process before container cleanup.
                    var result = await running.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
                    if (result.ExitCode != 0)
                        throw new InvalidOperationException($"{label} failed (exit {result.ExitCode}); .env.local was preserved.");
                    await output.WriteLineAsync($"  {label}: complete ({elapsed.Elapsed.TotalSeconds:0}s).");
                    return result;
                }
                catch (TimeoutException)
                {
                    await output.WriteLineAsync($"  {label}: still running ({elapsed.Elapsed.TotalSeconds:0}s). Ctrl-C cancels.");
                }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new InvalidOperationException($"{label} timed out after {timeout.TotalSeconds:0}s; .env.local was preserved. Check Docker and Auth connectivity, then retry env setup.");
        }
    }

    private async Task RemoveCheckerAsync(string container)
    {
        // Killing the Docker client alone does not stop its container.
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var result = await runner.CaptureAsync("docker", ["rm", "--force", container], null, cleanup.Token);
            if (result.ExitCode == 0 || result.StdErr.Contains("No such container", StringComparison.OrdinalIgnoreCase)) return;
        }
        catch (Exception exception) when (exception is OperationCanceledException or System.ComponentModel.Win32Exception or IOException)
        {
            // Preserve the original failure and provide a scoped recovery command.
        }
        await output.WriteLineAsync($"Could not confirm checker cleanup. Run: docker rm --force {container}");
    }
}
