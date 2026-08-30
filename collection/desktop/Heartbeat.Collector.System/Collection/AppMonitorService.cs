using Heartbeat.Core;
using Heartbeat.Collector.System.Configuration;
using Heartbeat.Collector.System.Input;
using Heartbeat.Collector.System.Observations;
using Heartbeat.Collection.Hub.Presence;
using Heartbeat.Collection.Hub.Time;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Heartbeat.Collector.System.Collection;

/// <summary>
/// 内置 system Collector 的平台无关状态机。它只消费语义桌面观察，折叠为
/// foreground Segment Fact 完整快照与 Current Activity 转场；原生 API、窗口句柄和平台生命周期
/// 均留在 adapter 与 platform head（ADR-020/021/033）。
/// </summary>
public sealed class AppMonitorService(
    IClock clock,
    IDesktopObservationSource observations,
    IInputActivitySignal inputActivity,
    ISystemSegmentPublisher publisher,
    ICurrentActivitySink activitySink,
    IDesktopSettings settings,
    TimeProvider? snapshotTimeProvider = null) : IHostedService, IDisposable
{
    private static readonly TimeSpan TitleGateWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromSeconds(30);

    private readonly object _lock = new();
    private readonly TimeProvider _snapshotTimeProvider = snapshotTimeProvider ?? TimeProvider.System;
    private bool _isStopping;
    private string? _currentApp;
    private string? _currentAppDisplayName;
    private string? _currentTitle;
    private string? _segmentTitle;
    private Guid _currentId;
    private long _currentRevision;
    private DateTimeOffset _currentStart;
    private bool _currentIsRotationContinuation;

    private bool _isAway;
    private Guid _awayId;
    private long _awayRevision;
    private DateTimeOffset _awayStart;
    private bool _awayIsRotationContinuation;
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
        lock (_lock)
            _isStopping = false;
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

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Log.Information("应用监测服务停止");
        lock (_lock)
            _isStopping = true;
        settings.AwayProcessNamesChanged -= OnAwayProcessNamesChanged;
        observations.Observation -= OnObservation;
        observations.Stop();

        if (_snapshotCts is not null)
            await _snapshotCts.CancelAsync();
        if (_snapshotLoop is not null)
        {
            try
            {
                await _snapshotLoop.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (
                _snapshotCts?.IsCancellationRequested == true
                && !cancellationToken.IsCancellationRequested)
            {
                // Snapshot loop observes the service-owned cancellation during normal stop.
            }
        }

        // 终态快照先进入 hub；desktop composition 保持 system Binding 先于 UploadWorker 停止。
        PushCurrentSnapshot(isFinal: true);
    }

    private async Task SnapshotLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(SnapshotInterval, _snapshotTimeProvider);
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

    public void PushCurrentSnapshot() => PushCurrentSnapshot(isFinal: false);

    private void PushCurrentSnapshot(bool isFinal)
    {
        IReadOnlyList<ForegroundSegmentSnapshot> snapshots;
        lock (_lock)
        {
            if (_isStopping && !isFinal)
                return;
            var now = clock.UtcNow;
            snapshots = _isAway
                ? BuildSegmentsThrough(
                    ref _awayId,
                    ref _awayRevision,
                    AppIdentityKeys.Away,
                    "离开",
                    null,
                    ref _awayStart,
                    ref _awayIsRotationContinuation,
                    now,
                    isFinal)
                : BuildSegmentsThrough(
                    ref _currentId,
                    ref _currentRevision,
                    _currentApp,
                    _currentAppDisplayName,
                    _segmentTitle,
                    ref _currentStart,
                    ref _currentIsRotationContinuation,
                    now,
                    isFinal);
        }
        foreach (var snapshot in snapshots)
            publisher.Publish(snapshot);
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
        IReadOnlyList<ForegroundSegmentSnapshot> closed = [];
        var reportActivity = false;

        lock (_lock)
        {
            if (_isStopping || _isAway)
                return;
            var now = clock.UtcNow;

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

        foreach (var snapshot in closed)
            publisher.Publish(snapshot);
        if (reportActivity)
            activitySink.Report(ToCurrentActivity(newApp, newAppDisplayName));
    }

    private void EnterAway()
    {
        IReadOnlyList<ForegroundSegmentSnapshot> closed;
        lock (_lock)
        {
            if (_isStopping || _isAway) return;
            var now = clock.UtcNow;

            closed = CloseCurrentSegment(now);
            _isAway = true;
            _awayId = Guid.CreateVersion7();
            _awayRevision = 0;
            _awayStart = now;
            _awayIsRotationContinuation = false;
            _currentApp = null;
            _currentAppDisplayName = null;
            _currentTitle = null;
            _segmentTitle = null;
            _currentStart = default;
            Log.Information("进入 away，封口当前应用段");
        }

        foreach (var snapshot in closed)
            publisher.Publish(snapshot);
        activitySink.Report(new CurrentActivity(AppIdentityKeys.Away, "离开"));
    }

    private void ExitAway(DesktopActivity resumed)
    {
        var resumedApp = Normalize(resumed.AppIdentityKey);
        IReadOnlyList<ForegroundSegmentSnapshot> awayFinal;
        lock (_lock)
        {
            if (_isStopping || !_isAway) return;
            var now = clock.UtcNow;

            awayFinal = BuildSegmentsThrough(
                ref _awayId,
                ref _awayRevision,
                AppIdentityKeys.Away,
                "离开",
                null,
                ref _awayStart,
                ref _awayIsRotationContinuation,
                now,
                isFinal: true);
            _isAway = false;
            StartSegment(resumedApp, resumed.AppDisplayName, resumed.Title, now);
            Log.Information("退出 away，恢复前台: {App}", resumedApp ?? "(无)");
        }

        foreach (var snapshot in awayFinal)
            publisher.Publish(snapshot);
        activitySink.Report(ToCurrentActivity(resumedApp, resumed.AppDisplayName));
    }

    private void StartSegment(string? app, string? appDisplayName, string? title, DateTimeOffset now)
    {
        _currentId = Guid.CreateVersion7();
        _currentRevision = 0;
        _currentApp = app;
        _currentAppDisplayName = appDisplayName;
        _currentTitle = title;
        _segmentTitle = title;
        _currentStart = now;
        _currentIsRotationContinuation = false;
    }

    private IReadOnlyList<ForegroundSegmentSnapshot> CloseCurrentSegment(DateTimeOffset now)
        => BuildSegmentsThrough(
            ref _currentId,
            ref _currentRevision,
            _currentApp,
            _currentAppDisplayName,
            _segmentTitle,
            ref _currentStart,
            ref _currentIsRotationContinuation,
            now,
            isFinal: true);

    private static IReadOnlyList<ForegroundSegmentSnapshot> BuildSegmentsThrough(
        ref Guid id,
        ref long revision,
        string? appIdentityKey,
        string? appDisplayName,
        string? title,
        ref DateTimeOffset start,
        ref bool isRotationContinuation,
        DateTimeOffset end,
        bool isFinal)
    {
        if (appIdentityKey == null || start == default)
            return [];

        var snapshots = new List<ForegroundSegmentSnapshot>();
        while (end >= start + SegmentRotationPolicy.RotateAfter)
        {
            var boundary = start + SegmentRotationPolicy.RotateAfter;
            var finalized = BuildSegment(
                id,
                ref revision,
                appIdentityKey,
                appDisplayName,
                title,
                start,
                boundary,
                isFinal: true);
            if (finalized is not null)
                snapshots.Add(finalized);

            if (isFinal && boundary == end)
                return snapshots;

            id = Guid.CreateVersion7();
            revision = 0;
            start = boundary;
            isRotationContinuation = true;
        }

        var current = BuildSegment(
            id,
            ref revision,
            appIdentityKey,
            appDisplayName,
            title,
            start,
            end,
            isFinal,
            allowPositiveSubsecond: isRotationContinuation);
        if (current is not null)
            snapshots.Add(current);
        return snapshots;
    }

    private static ForegroundSegmentSnapshot? BuildSegment(
        Guid id,
        ref long revision,
        string? appIdentityKey,
        string? appDisplayName,
        string? title,
        DateTimeOffset start,
        DateTimeOffset end,
        bool isFinal,
        bool allowPositiveSubsecond = false)
    {
        if (appIdentityKey == null || start == default) return null;
        var duration = end - start;
        if (duration <= TimeSpan.Zero
            || (duration.TotalSeconds < 1 && !allowPositiveSubsecond))
            return null;

        revision++;
        return new ForegroundSegmentSnapshot(
            id,
            revision,
            SystemIdentity.Key(appIdentityKey, title),
            appIdentityKey,
            appDisplayName,
            title,
            start,
            end,
            isFinal);
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
