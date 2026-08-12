using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using Heartbeat.Desktop.UI.Presentation;
using Serilog;
using Velopack;
using Velopack.Sources;

namespace Heartbeat.Desktop.Windows;

public sealed class WindowsUpdateController : IUpdateController, IDisposable
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(4);
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(8),
    ];
    private const string RepositoryUrl = "https://github.com/shenxianovo/Heartbeat";

    private readonly UpdateManager _manager;
    private readonly Func<Task> _prepareForRestart;
    private readonly object _gate = new();
    private Timer? _timer;
    private UpdateInfo? _pending;
    private Task<UpdateCheckResult>? _inflightCheck;
    private bool _isDownloading;
    private UpdateSnapshot _current = UpdateSnapshot.Idle;

    public WindowsUpdateController(Func<Task> prepareForRestart)
    {
        _prepareForRestart = prepareForRestart;
        var channel = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "win-arm64"
            : "win-x64";
        _manager = new UpdateManager(
            new GithubSource(RepositoryUrl, null, false),
            new UpdateOptions { ExplicitChannel = channel });
    }

    public UpdateSnapshot Current
    {
        get { lock (_gate) return _current; }
    }

    public event Action<UpdateSnapshot>? Changed;

    public void Start()
    {
        _ = CheckAsync();
        _timer = new Timer(_ => _ = CheckAsync(), null, CheckInterval, CheckInterval);
    }

    public Task<UpdateCheckResult> CheckAsync()
    {
        if (!_manager.IsInstalled) return Task.FromResult(UpdateCheckResult.Skipped);

        TaskCompletionSource<UpdateCheckResult>? completion = null;
        lock (_gate)
        {
            if (_current.State is UpdateState.Downloading or UpdateState.ReadyToApply)
                return Task.FromResult(UpdateCheckResult.Skipped);
            if (_inflightCheck != null) return _inflightCheck;

            completion = new TaskCompletionSource<UpdateCheckResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _inflightCheck = completion.Task;
        }

        _ = CompleteCheckAsync(completion);
        return completion.Task;
    }

    private async Task CompleteCheckAsync(TaskCompletionSource<UpdateCheckResult> completion)
    {
        try
        {
            var update = await _manager.CheckForUpdatesAsync();
            if (update == null)
            {
                Publish(UpdateSnapshot.Idle);
                completion.TrySetResult(UpdateCheckResult.UpToDate);
                return;
            }

            _pending = update;
            Publish(new UpdateSnapshot(
                UpdateState.UpdateAvailable,
                update.TargetFullRelease.Version.ToString()));
            _ = DownloadAsync();
            completion.TrySetResult(UpdateCheckResult.UpdateFound);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "检查更新失败");
            Publish(Current with { Error = "检查更新失败，请检查网络后重试。" });
            completion.TrySetResult(UpdateCheckResult.CheckFailed);
        }
        finally
        {
            lock (_gate) _inflightCheck = null;
        }
    }

    private async Task DownloadAsync()
    {
        UpdateInfo pending;
        lock (_gate)
        {
            if (_current.State != UpdateState.UpdateAvailable || _isDownloading || _pending == null) return;
            _isDownloading = true;
            pending = _pending;
        }

        Publish(Current with { State = UpdateState.Downloading, DownloadProgress = 0, Error = null });
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await _manager.DownloadUpdatesAsync(
                        pending,
                        progress => Publish(Current with { DownloadProgress = progress }));
                    Publish(Current with { State = UpdateState.ReadyToApply, DownloadProgress = 100 });
                    return;
                }
                catch (Exception exception) when (attempt < RetryDelays.Length && IsTransient(exception))
                {
                    var delay = RetryDelays[attempt];
                    Log.Warning(exception, "下载更新失败（第 {Attempt} 次），{Delay} 后重试", attempt + 1, delay);
                    await Task.Delay(delay);
                }
                catch (Exception exception)
                {
                    Log.Error(exception, "下载更新失败，已放弃重试");
                    Publish(Current with
                    {
                        State = UpdateState.UpdateAvailable,
                        DownloadProgress = null,
                        Error = "更新下载失败，将在下次检查时重试。"
                    });
                    return;
                }
            }
        }
        finally
        {
            lock (_gate) _isDownloading = false;
        }
    }

    public async Task<bool> ApplyAsync()
    {
        UpdateInfo? pending;
        lock (_gate)
        {
            if (_current.State != UpdateState.ReadyToApply || _pending == null) return false;
            pending = _pending;
        }

        await _prepareForRestart();
        _manager.ApplyUpdatesAndRestart(pending);
        return true;
    }

    public bool ApplyOnExitIfReady()
    {
        UpdateInfo? pending;
        lock (_gate)
        {
            if (_current.State != UpdateState.ReadyToApply || _pending == null) return false;
            pending = _pending;
        }
        _manager.ApplyUpdatesAndExit(pending);
        return true;
    }

    private void Publish(UpdateSnapshot snapshot)
    {
        lock (_gate) _current = snapshot;
        Changed?.Invoke(snapshot);
    }

    private static bool IsTransient(Exception exception) =>
        exception is HttpRequestException or TimeoutException or TaskCanceledException or IOException
        || exception.InnerException is { } inner && IsTransient(inner);

    public void Dispose() => _timer?.Dispose();
}
