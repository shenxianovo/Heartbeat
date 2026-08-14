using Heartbeat.Core;
using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Collector.System.Configuration;
using Heartbeat.Collector.System.Input;
using Heartbeat.Collector.System.Observations;
using Heartbeat.Collection.Hub.Presence;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Collection.Hub.Time;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Heartbeat.Collector.System.Collection;

/// <summary>
/// 内置 system Collector 的平台无关状态机。它只消费语义桌面观察，折叠为
/// ActivitySegment 快照与 Current Activity 转场；原生 API、窗口句柄和平台生命周期
/// 均留在 adapter 与 platform head（ADR-020/021/033）。
/// </summary>
public sealed class AppMonitorService(
    IClock clock,
    IDesktopObservationSource observations,
    IInputActivitySignal inputActivity,
    ISegmentSink sink,
    ICurrentActivitySink activitySink,
    IDesktopSettings settings) : IHostedService, IDisposable
{
    private static readonly TimeSpan TitleGateWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromSeconds(30);

    private readonly object _lock = new();
    private string? _currentApp;
    private string? _currentAppDisplayName;
    private string? _currentTitle;
    private string? _segmentTitle;
    private Guid _currentId;
    private DateTimeOffset _currentStart;

    private bool _isAway;
    private Guid _awayId;
    private DateTimeOffset _awayStart;
    private volatile string[] _awayProcessNames = [];

    private CancellationTokenSource? _snapshotCts;
    private Task? _snapshotLoop;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Log.Information("应用监测服务启动");

        _awayProcessNames = [.. settings.AwayProcessNames];
        settings.AwayProcessNamesChanged += OnAwayProcessNamesChanged;
        observations.Observation += OnObservation;

        var initial = observations.CurrentActivity;
        var initialApp = Normalize(initial.AppIdentityKey);
        if (initialApp != null)
        {
            lock (_lock)
            {
                StartSegment(initialApp, initial.AppDisplayName, initial.Title, clock.UtcNow);
                Log.Information("初始前台应用: {App}", initialApp);
            }
        }
        activitySink.Report(ToCurrentActivity(initialApp, initial.AppDisplayName));

        observations.Start();

        _snapshotCts = new CancellationTokenSource();
        _snapshotLoop = Task.Run(() => SnapshotLoopAsync(_snapshotCts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Log.Information("应用监测服务停止");
        _snapshotCts?.Cancel();

        // 终态快照先进入 hub；Windows composition root 保持 monitor 最先停、UploadWorker 后停。
        PushCurrentSnapshot();

        settings.AwayProcessNamesChanged -= OnAwayProcessNamesChanged;
        observations.Observation -= OnObservation;
        observations.Stop();
        return Task.CompletedTask;
    }

    private async Task SnapshotLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(SnapshotInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                PushCurrentSnapshot();
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
    }

    public void PushCurrentSnapshot()
    {
        var now = clock.UtcNow;
        ActivitySegmentItem? snapshot;
        lock (_lock)
        {
            snapshot = _isAway
                ? BuildSegment(_awayId, AppIdentityKeys.Away, "离开", null, _awayStart, now)
                : BuildSegment(_currentId, _currentApp, _currentAppDisplayName, _segmentTitle, _currentStart, now);
        }
        if (snapshot != null)
            sink.Push([snapshot]);
    }

    private void OnObservation(DesktopObservation observation)
    {
        switch (observation.Kind)
        {
            case DesktopObservationKind.EnteredAway:
                EnterAway();
                break;
            case DesktopObservationKind.ExitedAway:
                ExitAway(observation.Activity);
                break;
            case DesktopObservationKind.AppActivated:
            case DesktopObservationKind.FocusedWindowChanged:
            case DesktopObservationKind.TitleChanged:
                ObserveActivity(observation);
                break;
        }
    }

    private void ObserveActivity(DesktopObservation observation)
    {
        var newApp = Normalize(observation.Activity.AppIdentityKey);
        var newAppDisplayName = observation.Activity.AppDisplayName;
        var newTitle = observation.Activity.Title;
        var now = clock.UtcNow;
        ActivitySegmentItem? closed = null;
        var reportActivity = false;

        lock (_lock)
        {
            if (_isAway)
                return;

            var appSame = string.Equals(_currentApp, newApp, StringComparison.OrdinalIgnoreCase);
            var titleSame = string.Equals(_currentTitle, newTitle, StringComparison.Ordinal);

            var usesLegacyWindowsFocusPolicy =
                observation.Kind == DesktopObservationKind.FocusedWindowChanged
                && !settings.SplitFocusedWindowChangesUnconditionally
                && appSame;

            if (observation.Kind == DesktopObservationKind.TitleChanged || usesLegacyWindowsFocusPolicy)
            {
                if (appSame && titleSame)
                    return;

                // 只有同一 focused window 的标题变化需要 Interaction Signal 门控。
                if (appSame && !inputActivity.ClickedWithin(TitleGateWindow))
                {
                    _currentTitle = newTitle;
                    return;
                }
            }

            // 最终跨平台语义下 App 激活与 focused-window 切换必切段；Windows 本票通过
            // settings 的兼容策略保持旧输出，后续切换协议/语义时可独立翻转。
            closed = CloseCurrentSegment(now);
            StartSegment(newApp, newAppDisplayName, newTitle, now);
            reportActivity = true;

            if (newApp != null)
                Log.Debug("桌面转场 {Kind}: {App} / {Title}", observation.Kind, newApp, newTitle);
        }

        if (closed != null)
            sink.Push([closed]);
        if (reportActivity)
            activitySink.Report(ToCurrentActivity(newApp, newAppDisplayName));
    }

    private void EnterAway()
    {
        var now = clock.UtcNow;
        ActivitySegmentItem? closed;
        lock (_lock)
        {
            if (_isAway) return;

            closed = CloseCurrentSegment(now);
            _isAway = true;
            _awayId = Guid.CreateVersion7();
            _awayStart = now;
            _currentApp = null;
            _currentAppDisplayName = null;
            _currentTitle = null;
            _segmentTitle = null;
            _currentStart = default;
            Log.Information("进入 away，封口当前应用段");
        }

        if (closed != null)
            sink.Push([closed]);
        activitySink.Report(new CurrentActivity(AppIdentityKeys.Away, "离开"));
    }

    private void ExitAway(DesktopActivity resumed)
    {
        var now = clock.UtcNow;
        var resumedApp = Normalize(resumed.AppIdentityKey);
        ActivitySegmentItem? awayFinal;
        lock (_lock)
        {
            if (!_isAway) return;

            awayFinal = BuildSegment(_awayId, AppIdentityKeys.Away, "离开", null, _awayStart, now);
            _isAway = false;
            StartSegment(resumedApp, resumed.AppDisplayName, resumed.Title, now);
            Log.Information("退出 away，恢复前台: {App}", resumedApp ?? "(无)");
        }

        if (awayFinal != null)
            sink.Push([awayFinal]);
        activitySink.Report(ToCurrentActivity(resumedApp, resumed.AppDisplayName));
    }

    private void StartSegment(string? app, string? appDisplayName, string? title, DateTimeOffset now)
    {
        _currentId = Guid.CreateVersion7();
        _currentApp = app;
        _currentAppDisplayName = appDisplayName;
        _currentTitle = title;
        _segmentTitle = title;
        _currentStart = now;
    }

    private ActivitySegmentItem? CloseCurrentSegment(DateTimeOffset now)
        => BuildSegment(_currentId, _currentApp, _currentAppDisplayName, _segmentTitle, _currentStart, now);

    private static ActivitySegmentItem? BuildSegment(
        Guid id,
        string? appIdentityKey,
        string? appDisplayName,
        string? title,
        DateTimeOffset start,
        DateTimeOffset end)
    {
        if (appIdentityKey == null || start == default) return null;
        var duration = end - start;
        if (duration.TotalSeconds < 1) return null;

        return new ActivitySegmentItem
        {
            Id = id,
            Source = ActivitySources.System,
            IdentityKey = SystemIdentity.Key(appIdentityKey, title),
            AppIdentityKey = appIdentityKey,
            AppDisplayName = appDisplayName,
            Title = title,
            StartTime = start,
            EndTime = end
        };
    }

    private void OnAwayProcessNamesChanged(IReadOnlyList<string> names)
        => _awayProcessNames = [.. names];

    private string? Normalize(string? appIdentityKey)
    {
        if (string.IsNullOrEmpty(appIdentityKey)) return appIdentityKey;

        foreach (var name in _awayProcessNames)
        {
            if (string.Equals(
                    appIdentityKey,
                    AppIdentityKeys.FromLegacyWindowsAppName(name),
                    StringComparison.OrdinalIgnoreCase))
                return AppIdentityKeys.Away;
        }
        return AppIdentityKeys.Normalize(appIdentityKey);
    }

    private static CurrentActivity? ToCurrentActivity(string? appIdentityKey, string? appDisplayName)
        => appIdentityKey == null ? null : new CurrentActivity(appIdentityKey, appDisplayName);

    public void Dispose()
    {
        _snapshotCts?.Cancel();
        _snapshotCts?.Dispose();
        settings.AwayProcessNamesChanged -= OnAwayProcessNamesChanged;
        observations.Observation -= OnObservation;
        observations.Stop();
        GC.SuppressFinalize(this);
    }
}
