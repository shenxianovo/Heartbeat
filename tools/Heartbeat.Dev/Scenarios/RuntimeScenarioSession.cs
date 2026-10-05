using System.Diagnostics;
using System.Text.Json;

namespace Heartbeat.Dev;

internal sealed class RuntimeScenarioSession(RepositoryContext repository, ScenarioConfiguration configuration)
    : DesktopScenarioSession(RuntimeScenarioPlatform.Application)
{
    private Process? _process;
    private Task<string>? _errors;
    private Uri _web = null!;
    public override bool HasExited => _process?.HasExited ?? true;

    public override async Task ConfigureAsync(Uri web, CancellationToken token)
    {
        _web = web;
        await LaunchAsync(configuration.ApiKey, token);
    }

    private async Task LaunchAsync(string? apiKey, CancellationToken token)
    {
        var input = new RuntimeHostInput(ProfileDirectory, _web, new Uri(configuration.Authority), apiKey);
        _process = ProcessRunner.Start(repository.Root, "dotnet", [typeof(Program).Assembly.Location, RuntimeScenarioHost.CommandName],
            new Dictionary<string, string?> { [RuntimeScenarioHost.InputVariable] = JsonSerializer.Serialize(input) },
            redirectOutput: true, redirectInput: true);
        _errors = _process.StandardError.ReadToEndAsync(CancellationToken.None);
        await ExpectAsync("ready", token);
    }

    public override Task StartCollectionAsync(CancellationToken token) => SendAsync("start", token);
    public override Task PauseAsync(CancellationToken token) => SendAsync("pause", token);

    private async Task SendAsync(string command, CancellationToken token)
    {
        await _process!.StandardInput.WriteLineAsync(command.AsMemory(), token);
        await _process.StandardInput.FlushAsync(token);
        await ExpectAsync("ok", token);
    }

    private async Task ExpectAsync(string expected, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            var actual = await _process!.StandardOutput.ReadLineAsync(deadline.Token);
            if (actual != expected) throw new InvalidOperationException("Background runtime action failed or the child exited.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("Background runtime action timed out."); }
    }

    public override async Task RestartAfterCrashAsync(CancellationToken token)
    {
        await DisposeProcessAsync(); // Kill, not DisposeAsync on DesktopRuntime: exercise actual crash custody.
        await LaunchAsync(null, token); // No credential injection after restart.
    }

    public override async Task QuitAsync(CancellationToken token)
    {
        await _process!.StandardInput.WriteLineAsync("quit".AsMemory(), token);
        await _process.StandardInput.FlushAsync(token);
        await _process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(30), token);
        if (_process.ExitCode != 0) throw new InvalidOperationException("Background runtime did not exit cleanly.");
    }

    protected override async ValueTask DisposeProcessAsync()
    {
        if (_process is null) return;
        if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync();
        if (_errors is not null) await _errors;
        _process.Dispose();
        _process = null;
    }
}
