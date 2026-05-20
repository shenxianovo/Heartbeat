using System.Runtime.InteropServices;
using Serilog;
using Velopack;
using Velopack.Sources;

namespace Heartbeat.WPF.Services;

public sealed class UpdateService : IDisposable
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(4);
    private const string RepoUrl = "https://github.com/shenxianovo/Heartbeat";

    private readonly UpdateManager _updateManager;
    private Timer? _timer;
    private UpdateInfo? _pendingUpdate;
    private bool _isDownloading;

    public event Action<string>? UpdateAvailable;
    public event Action<int>? DownloadProgress;
    public event Action? UpdateReady;

    public bool HasPendingUpdate => _pendingUpdate != null;
    public string? PendingVersion => _pendingUpdate?.TargetFullRelease?.Version?.ToString();

    public UpdateService()
    {
        var channel = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "win-arm64"
            : "win-x64";
        _updateManager = new UpdateManager(
            new GithubSource(RepoUrl, null, false),
            new UpdateOptions { ExplicitChannel = channel });
    }

    public void Start()
    {
        _ = CheckForUpdateAsync();
        _timer = new Timer(_ => _ = CheckForUpdateAsync(), null, CheckInterval, CheckInterval);
    }

    public async Task CheckForUpdateAsync()
    {
        if (!_updateManager.IsInstalled) return;

        try
        {
            var updateInfo = await _updateManager.CheckForUpdatesAsync();
            if (updateInfo != null)
            {
                _pendingUpdate = updateInfo;
                UpdateAvailable?.Invoke(updateInfo.TargetFullRelease.Version.ToString());
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "检查更新失败");
        }
    }

    public async Task DownloadUpdateAsync()
    {
        if (_pendingUpdate == null || _isDownloading) return;
        _isDownloading = true;

        try
        {
            await _updateManager.DownloadUpdatesAsync(
                _pendingUpdate,
                progress => DownloadProgress?.Invoke(progress));

            UpdateReady?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "下载更新失败");
        }
        finally
        {
            _isDownloading = false;
        }
    }

    public void ApplyUpdateAndRestart()
    {
        _updateManager.ApplyUpdatesAndRestart(_pendingUpdate);
    }

    public void ApplyUpdateOnExit()
    {
        if (_pendingUpdate != null)
        {
            _updateManager.ApplyUpdatesAndExit(_pendingUpdate);
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
