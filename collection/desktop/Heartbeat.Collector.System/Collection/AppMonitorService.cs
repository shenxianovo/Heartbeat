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
/// 将 SystemActivityModel 的业务转场映射为 foreground Segment Fact 和 Current Activity。
/// 此处管理 Fact 身份、修订、定时快照与交付顺序；观测规则留在运行模型中，
/// 原生 API 与平台生命周期留在 adapter 与 platform head（ADR-020/021/033）。
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
    private readonly SystemActivityModel _activity = new();
    private Guid _currentId;
    private long _currentRevision;
    private DateTimeOffset _currentStart;
    private bool _currentIsRotationContinuation;

    private readonly OrderedDeferredHandoff<TimedDesktopObservation> _durableStageHandoff = new();
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
        var startedAt = clock.UtcNow;
        publisher.RecoverInterruptedSegment(startedAt);
        lock (_lock)
        {
            _isStopping = false;
            _activity.Start(initial, _awayProcessNames);
            StartSegment(startedAt);
            if (_activity.Current.AppIdentityKey is { } app)
                Log.Information("初始前台应用: {App}", app);
        }
        activitySink.Report(ToCurrentActivity(_activity.Current));

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
        Guid id;
        long revision;
        DateTimeOffset start;
        bool continuation;
        lock (_lock)
        {
            if (_isStopping && !isFinal)
                return;
            if (!_durableStageHandoff.TryBegin())
                return;
            var now = clock.UtcNow;
            id = _currentId;
            revision = _currentRevision;
            start = _currentStart;
            continuation = _currentIsRotationContinuation;
            snapshots = BuildSegmentsThrough(
                ref id,
                ref revision,
                _activity.Current.AppIdentityKey,
                _activity.Current.AppDisplayName,
                _activity.Current.Title,
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
                _currentId = id;
                _currentRevision = revision;
                _currentStart = start;
                _currentIsRotationContinuation = continuation;
            }
        }
        finally
        {
            _durableStageHandoff.Complete(
                observation => OnObservation(
                    observation.Observation,
                    observation.ObservedAt,
                    isDeferredReplay: true));
        }
    }

    private void OnObservation(DesktopObservation observation) =>
        OnObservation(observation, null, isDeferredReplay: false);

    private void OnObservation(
        DesktopObservation observation,
        DateTimeOffset? observedAt,
        bool isDeferredReplay)
    {
        IReadOnlyList<ForegroundSegmentSnapshot> closed;
        DesktopActivity current;
        lock (_lock)
        {
            if (!isDeferredReplay && DeferObservationDuringDurableStage(observation))
                return;
            var now = observedAt ?? clock.UtcNow;
            var previous = _activity.Current;
            if (!_activity.Observe(
                    observation,
                    inputActivity.ClickedWithin(TitleGateWindow),
                    settings.SplitFocusedWindowChangesUnconditionally,
                    _awayProcessNames))
                return;

            closed = CloseCurrentSegment(previous, now);
            StartSegment(now);
            current = _activity.Current;
            Log.Debug("桌面转场 {Kind}: {App} / {Title}",
                observation.Kind, current.AppIdentityKey, current.Title);
        }

        publisher.PublishBatch(closed);
        activitySink.Report(ToCurrentActivity(current));
    }

    private bool DeferObservationDuringDurableStage(DesktopObservation observation)
    {
        if (_isStopping)
            return true;
        return _durableStageHandoff.TryDefer(
            () => new TimedDesktopObservation(observation, clock.UtcNow));
    }

    private readonly record struct TimedDesktopObservation(
        DesktopObservation Observation,
        DateTimeOffset ObservedAt);

    private void StartSegment(DateTimeOffset now)
    {
        _currentId = Guid.CreateVersion7();
        _currentRevision = 0;
        _currentStart = now;
        _currentIsRotationContinuation = false;
    }

    private IReadOnlyList<ForegroundSegmentSnapshot> CloseCurrentSegment(
        DesktopActivity activity, DateTimeOffset now)
        => BuildSegmentsThrough(
            ref _currentId,
            ref _currentRevision,
            activity.AppIdentityKey,
            activity.AppDisplayName,
            activity.Title,
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
        if (start == default)
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
        if (start == default) return null;
        var duration = end - start;
        if (duration < TimeSpan.Zero
            || (duration == TimeSpan.Zero && !allowZeroDuration)
            || (duration.TotalSeconds < 1 && !allowPositiveSubsecond))
            return null;

        revision++;
        return new ForegroundSegmentSnapshot(
            id,
            revision,
            appIdentityKey is null ? null : SystemIdentity.Key(appIdentityKey, title),
            appIdentityKey,
            appDisplayName,
            title,
            start,
            end,
            isFinal,
            IsObservation: true);
    }

    private void OnAwayProcessNamesChanged(IReadOnlyList<string> names)
        => _awayProcessNames = [.. names];

    private static CurrentActivity? ToCurrentActivity(DesktopActivity activity)
        => activity.AppIdentityKey == null
            ? null
            : new CurrentActivity(activity.AppIdentityKey, activity.AppDisplayName);

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
