using System.Text.Json;
using Heartbeat.Desktop;
using Heartbeat.Hub;

namespace Heartbeat.Dev;

internal sealed record ReplayApplication(string Identifier, string DisplayName,
    string CollectorKey = "heartbeat.collector.desktop.macos");

// The two hosts share custody and business assertions, not their UI or OS evidence.
internal abstract class DesktopScenarioSession(ReplayApplication application) : IAsyncDisposable
{
    protected string ProfileDirectory { get; } = Directory.CreateTempSubdirectory("heartbeat-desktop-scenario-").FullName;
    public ReplayApplication Application { get; } = application;
    public DesktopSettings Settings => JsonSerializer.Deserialize<DesktopSettings>(
        File.ReadAllText(Path.Combine(ProfileDirectory, "settings.json")), JsonSerializerOptions.Web)!;
    public HubCustodySnapshot Custody => HubCustodySnapshot.Read(ProfileDirectory, Settings.Destination);
    public QueueStatus Queue => HubCustodySnapshot.Status(ProfileDirectory, Settings.Destination);
    public abstract bool HasExited { get; }
    public abstract Task ConfigureAsync(Uri web, CancellationToken token);
    public abstract Task StartCollectionAsync(CancellationToken token);
    public abstract Task PauseAsync(CancellationToken token);
    public abstract Task RestartAfterCrashAsync(CancellationToken token);
    public abstract Task QuitAsync(CancellationToken token);
    public virtual Task<bool?> IsForegroundAsync(CancellationToken token) => Task.FromResult<bool?>(null);
    protected abstract ValueTask DisposeProcessAsync();

    public Task WaitForDrainAsync(CancellationToken token) => ScenarioWait.UntilAsync("Hub delivery drain", _ =>
    {
        if (HasExited) throw new InvalidOperationException("Desktop process exited before delivery drained.");
        var status = Queue;
        if (status.Failed > 0) throw new InvalidOperationException("Hub has rejected Records.");
        return Task.FromResult(status.Pending == 0);
    }, token);

    public async ValueTask DisposeAsync()
    {
        try { await DisposeProcessAsync(); }
        finally { Directory.Delete(ProfileDirectory, recursive: true); }
    }
}
