using System.Text.Json;
using Heartbeat.Desktop;
using Heartbeat.Hub;

namespace Heartbeat.Dev;

internal sealed class DesktopSession(RepositoryContext repository, ScenarioConfiguration configuration, string executable) : IAsyncDisposable
{
    private readonly string _profile = Directory.CreateTempSubdirectory("heartbeat-desktop-scenario-").FullName;
    private ScenarioProcess? _process;
    public MacDesktopDriver Ui { get; private set; } = null!;
    public DesktopSettings Settings => JsonSerializer.Deserialize<DesktopSettings>(
        File.ReadAllText(Path.Combine(_profile, "settings.json")), JsonSerializerOptions.Web)!;
    public HubCustodySnapshot Custody => HubCustodySnapshot.Read(_profile, Settings.Destination);
    public QueueStatus Queue => HubCustodySnapshot.Status(_profile, Settings.Destination);
    public bool HasExited => _process?.Completion.IsCompleted ?? true;

    public void Launch()
    {
        _process = new ScenarioProcess(repository.Root, executable, ["--data-directory", _profile]);
        Ui = new MacDesktopDriver(repository, configuration, _process.Id);
    }

    public async Task RestartAfterCrashAsync(CancellationToken token)
    {
        await _process!.KillAsync();
        await _process.DisposeAsync();
        Launch();
        // Startup must reload the saved connection and credential without UI input.
        await Ui.WaitForCollectionAsync(token);
    }

    public Task PauseAsync(CancellationToken token) => Ui.PauseCollectionAsync(token);

    public Task WaitForDrainAsync(CancellationToken token) => ScenarioWait.UntilAsync("Hub delivery drain", _ =>
    {
        var status = Queue;
        if (status.Failed > 0) throw new InvalidOperationException("Hub has rejected Records.");
        return Task.FromResult(status.Pending == 0);
    }, token);

    public async Task QuitAsync(CancellationToken token)
    {
        await Ui.QuitAsync(token);
        var result = await _process!.WaitAsync(TimeSpan.FromSeconds(30), token);
        if (result.ExitCode != 0) throw new InvalidOperationException("Desktop did not exit cleanly.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is not null) await _process.DisposeAsync();
        Directory.Delete(_profile, recursive: true);
    }
}
