using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using Serilog;
using Velopack;
using Velopack.Sources;

namespace Heartbeat.WPF.Services;

/// <summary>
/// 更新生命周期状态。只有 ReadyToApply 的更新才允许被应用。
/// </summary>
public enum UpdateState
{
    Idle,
    UpdateAvailable,
    Downloading,
    ReadyToApply,
}

/// <summary>
/// 一次检查的瞬时结果。检查失败 ≠ 已是最新。
/// </summary>
public enum CheckResult
{
    UpToDate,
    UpdateFound,
    CheckFailed,
    /// <summary>Downloading/ReadyToApply 期间跳过检查，旧 pending 优先。</summary>
    Skipped,
}

public sealed class UpdateService : IDisposable
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(4);
    private static readonly TimeSpan[] DownloadRetryDelays =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(8),
    ];
    private const string RepoUrl = "https://github.com/shenxianovo/Heartbeat";

    private readonly UpdateManager _updateManager;
    private readonly object _gate = new();
    private Timer? _timer;
    private UpdateInfo? _pendingUpdate;
    private Task<CheckResult>? _inflightCheck;
    private bool _isDownloading;

    public event Action<string>? UpdateAvailable;
    public event Action<int>? DownloadProgress;
    public event Action? UpdateReady;
    public event Action<string>? DownloadFailed;

    public UpdateState State { get; private set; } = UpdateState.Idle;
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

    /// <summary>
    /// 检查更新。并发调用合流到同一个 in-flight 任务（对 GitHub 只发一次请求）。
    /// </summary>
    public Task<CheckResult> CheckForUpdateAsync()
    {
        if (!_updateManager.IsInstalled) return Task.FromResult(CheckResult.Skipped);

        lock (_gate)
        {
            if (State is UpdateState.Downloading or UpdateState.ReadyToApply)
            {
                return Task.FromResult(CheckResult.Skipped);
            }
            _inflightCheck ??= CheckCoreAsync();
            return _inflightCheck;
        }
    }

    private async Task<CheckResult> CheckCoreAsync()
    {
        try
        {
            var updateInfo = await _updateManager.CheckForUpdatesAsync();
            if (updateInfo == null) return CheckResult.UpToDate;

            _pendingUpdate = updateInfo;
            State = UpdateState.UpdateAvailable;
            UpdateAvailable?.Invoke(updateInfo.TargetFullRelease.Version.ToString());
            return CheckResult.UpdateFound;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "检查更新失败");
            return CheckResult.CheckFailed;
        }
        finally
        {
            lock (_gate)
            {
                _inflightCheck = null;
            }
        }
    }

    /// <summary>
    /// 下载已发现的更新。对超时/连接类异常做有限重试（指数退避），
    /// 全部失败退回 UpdateAvailable，由下次检查（定时或手动）重新触发。
    /// </summary>
    public async Task DownloadUpdateAsync()
    {
        UpdateInfo pending;
        lock (_gate)
        {
            if (State != UpdateState.UpdateAvailable || _isDownloading || _pendingUpdate == null) return;
            _isDownloading = true;
            State = UpdateState.Downloading;
            pending = _pendingUpdate;
        }

        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await _updateManager.DownloadUpdatesAsync(
                        pending,
                        progress => DownloadProgress?.Invoke(progress));

                    State = UpdateState.ReadyToApply;
                    UpdateReady?.Invoke();
                    return;
                }
                catch (Exception ex) when (attempt < DownloadRetryDelays.Length && IsTransient(ex))
                {
                    var delay = DownloadRetryDelays[attempt];
                    Log.Warning(ex, "下载更新失败（第 {Attempt} 次），{Delay} 后重试", attempt + 1, delay);
                    await Task.Delay(delay);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "下载更新失败，已放弃重试");
                    State = UpdateState.UpdateAvailable;
                    DownloadFailed?.Invoke(ex.Message);
                    return;
                }
            }
        }
        finally
        {
            _isDownloading = false;
        }
    }

    private static bool IsTransient(Exception ex) =>
        ex is HttpRequestException or TimeoutException or TaskCanceledException or IOException
        || ex.InnerException is { } inner && IsTransient(inner);

    public void ApplyUpdateAndRestart()
    {
        if (State != UpdateState.ReadyToApply || _pendingUpdate is not { } pending) return;
        _updateManager.ApplyUpdatesAndRestart(pending);
    }

    public void ApplyUpdateOnExit()
    {
        if (State != UpdateState.ReadyToApply || _pendingUpdate is not { } pending) return;
        _updateManager.ApplyUpdatesAndExit(pending);
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
