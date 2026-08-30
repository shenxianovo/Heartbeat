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
    private bool _durableStageInProgress;
    private readonly Queue<DesktopObservation> _deferredObservations = [];
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
        var startedAt = clock.UtcNow;
        publisher.RecoverInterruptedSegment(startedAt);
        lock (_lock)
            _isStopping = false;
        if (initialApp != null)
        {
            lock (_lock)
            {
                StartSegment(initialApp, initial.AppDisplayName, initial.Title, startedAt);
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

        // No new platform observations can enter after the source is stopped. Let an observation
        // that was already deferred behind the durable rollover boundary commit before fencing the
        // terminal snapshot, otherwise Stop could silently discard a transition that already returned
        // to the platform callback.
        lock (_lock)
            _isStopping = true;

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
        bool plannedAway;
        Guid id;
        long revision;
        DateTimeOffset start;
        bool continuation;
        lock (_lock)
        {
            if (_isStopping && !isFinal)
                return;
            if (_durableStageInProgress)
                return;
            _durableStageInProgress = true;
            var now = clock.UtcNow;
            plannedAway = _isAway;
            id = plannedAway ? _awayId : _currentId;
            revision = plannedAway ? _awayRevision : _currentRevision;
            start = plannedAway ? _awayStart : _currentStart;
            continuation = plannedAway
                ? _awayIsRotationContinuation
                : _currentIsRotationContinuation;
            snapshots = plannedAway
                ? BuildSegmentsThrough(
                    ref id,
                    ref revision,
                    AppIdentityKeys.Away,
                    "离开",
                    null,
                    ref start,
                    ref continuation,
                    now,
                    isFinal)
                : BuildSegmentsThrough(
                    ref id,
                    ref revision,
                    _currentApp,
                    _currentAppDisplayName,
                    _segmentTitle,
                    ref start,
                    ref continuation,
                    now,
                    isFinal);
        }

        try
        {
            publisher.StageDurableBatch(snapshots);
            lock (_lock)
            {
                if (plannedAway)
                {
                    _awayId = id;
                    _awayRevision = revision;
                    _awayStart = start;
                    _awayIsRotationContinuation = continuation;
                }
                else
                {
                    _currentId = id;
                    _currentRevision = revision;
                    _currentStart = start;
                    _currentIsRotationContinuation = continuation;
                }
            }
        }
        finally
        {
            DesktopObservation[] deferred;
            lock (_lock)
            {
                _durableStageInProgress = false;
                deferred = [.. _deferredObservations];
                _deferredObservations.Clear();
            }
            foreach (var observation in deferred)
                OnObservation(observation);
        }
    }

    private void OnObservation(DesktopObservation observation)
    {
        switch (observation.Kind)
        {
            case DesktopObservationKind.EnteredAway:
                EnterAway(observation);
                break;
            case DesktopObservationKind.ExitedAway:
                ExitAway(observation);
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
            if (DeferObservationDuringDurableStage(observation) || _isAway)
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

        publisher.PublishBatch(closed);
        if (reportActivity)
            activitySink.Report(ToCurrentActivity(newApp, newAppDisplayName));
    }

    private void EnterAway(DesktopObservation observation)
    {
        IReadOnlyList<ForegroundSegmentSnapshot> closed;
        lock (_lock)
        {
            if (DeferObservationDuringDurableStage(observation) || _isAway) return;
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

        publisher.PublishBatch(closed);
        activitySink.Report(new CurrentActivity(AppIdentityKeys.Away, "离开"));
    }

    private void ExitAway(DesktopObservation observation)
    {
        var resumed = observation.Activity;
        var resumedApp = Normalize(resumed.AppIdentityKey);
        IReadOnlyList<ForegroundSegmentSnapshot> awayFinal;
        lock (_lock)
        {
            if (DeferObservationDuringDurableStage(observation) || !_isAway) return;
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

        publisher.PublishBatch(awayFinal);
        activitySink.Report(ToCurrentActivity(resumedApp, resumed.AppDisplayName));
    }

    private bool DeferObservationDuringDurableStage(DesktopObservation observation)
    {
        if (_isStopping)
            return true;
        if (!_durableStageInProgress)
            return false;
        _deferredObservations.Enqueue(observation);
        return true;
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
            allowPositiveSubsecond: isRotationContinuation,
            allowZeroDuration: isRotationContinuation && end == start);
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
        bool allowPositiveSubsecond = false,
        bool allowZeroDuration = false)
    {
        if (appIdentityKey == null || start == default) return null;
        var duration = end - start;
        if (duration < TimeSpan.Zero
            || (duration == TimeSpan.Zero && !allowZeroDuration)
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
