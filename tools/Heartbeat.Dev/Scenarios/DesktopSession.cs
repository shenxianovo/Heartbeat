namespace Heartbeat.Dev;

internal sealed class DesktopSession(RepositoryContext repository, ScenarioConfiguration configuration, PackagedDesktop package)
    : DesktopScenarioSession(new(package.Identifier, package.DisplayName))
{
    private ScenarioProcess? _process;
    private MacDesktopDriver _ui = null!;
    public override bool HasExited => _process?.Completion.IsCompleted ?? true;

    private void Launch()
    {
        _process = new ScenarioProcess(repository.Root, package.Executable, ["--data-directory", ProfileDirectory]);
        _ui = new MacDesktopDriver(repository, configuration, _process.Id);
    }

    public override async Task ConfigureAsync(Uri web, CancellationToken token)
    {
        Launch();
        await _ui.ConfigureAsync(web, token);
    }

    public override async Task RestartAfterCrashAsync(CancellationToken token)
    {
        await _process!.KillAsync();
        await _process.DisposeAsync();
        Launch();
        await _ui.WaitForCollectionAsync(token);
    }

    public override Task StartCollectionAsync(CancellationToken token) => _ui.StartCollectionAsync(token);
    public override Task PauseAsync(CancellationToken token) => _ui.PauseCollectionAsync(token);
    public override async Task<bool?> IsForegroundAsync(CancellationToken token) => await _ui.IsForegroundAsync(token);

    public override async Task QuitAsync(CancellationToken token)
    {
        await _ui.QuitAsync(token);
        var result = await _process!.WaitAsync(TimeSpan.FromSeconds(30), token);
        if (result.ExitCode != 0) throw new InvalidOperationException("Desktop did not exit cleanly.");
    }

    protected override async ValueTask DisposeProcessAsync()
    {
        if (_process is not null) await _process.DisposeAsync();
    }
}
