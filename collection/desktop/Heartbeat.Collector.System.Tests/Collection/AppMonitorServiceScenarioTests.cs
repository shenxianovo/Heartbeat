using Heartbeat.Core;
using Heartbeat.Collector.System.Collection;
using Heartbeat.Collector.System.Configuration;
using Heartbeat.Collector.System.Input;
using Heartbeat.Collector.System.Observations;
using Heartbeat.Collection.Hub.Presence;
using Heartbeat.Collection.Hub.Time;

namespace Heartbeat.Collector.System.Tests.Collection;

/// <summary>
/// system Collector 的语义场景契约。测试只喂平台观察、只读输出事实，不知道 Win32/macOS adapter。
/// </summary>
public class AppMonitorServiceScenarioTests
{
    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UnixEpoch;
        public void Advance(TimeSpan duration) => UtcNow += duration;
    }

    private sealed class CoordinatedClock : IClock
    {
        private readonly ManualResetEventSlim _blockedReadEntered = new(false);
        private readonly ManualResetEventSlim _releaseBlockedRead = new(false);
        private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;
        private int _blockNextRead;

        public DateTimeOffset UtcNow
        {
            get
            {
                var captured = _utcNow;
                if (Interlocked.Exchange(ref _blockNextRead, 0) == 1)
                {
                    _blockedReadEntered.Set();
                    _releaseBlockedRead.Wait();
                }
                return captured;
            }
        }

        public void Advance(TimeSpan duration) => _utcNow += duration;
        public void BlockNextRead() => Interlocked.Exchange(ref _blockNextRead, 1);
        public void WaitForBlockedRead() => Assert.True(_blockedReadEntered.Wait(TimeSpan.FromSeconds(5)));
        public void ReleaseBlockedRead() => _releaseBlockedRead.Set();
    }

    private sealed class ManualTimerClock : TimeProvider, IClock
    {
        private readonly object _lock = new();
        private readonly List<ManualTimer> _timers = [];
        private readonly ManualResetEventSlim _timerCreated = new(false);
        private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;

        public DateTimeOffset UtcNow => GetUtcNow();
        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration)
        {
            _utcNow += duration;
            ManualTimer[] timers;
            lock (_lock)
                timers = [.. _timers];
            foreach (var timer in timers)
                timer.FireIfDue(_utcNow);
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, _utcNow + dueTime, period);
            lock (_lock)
                _timers.Add(timer);
            _timerCreated.Set();
            return timer;
        }

        public void WaitForTimer() => Assert.True(_timerCreated.Wait(TimeSpan.FromSeconds(5)));

        private void Remove(ManualTimer timer)
        {
            lock (_lock)
                _timers.Remove(timer);
        }

        private sealed class ManualTimer(
            ManualTimerClock owner,
            TimerCallback callback,
            object? state,
            DateTimeOffset dueAt,
            TimeSpan period) : ITimer
        {
            private DateTimeOffset _dueAt = dueAt;
            private TimeSpan _period = period;
            private bool _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan newPeriod)
            {
                if (_disposed) return false;
                _dueAt = owner.GetUtcNow() + dueTime;
                _period = newPeriod;
                return true;
            }

            public void FireIfDue(DateTimeOffset now)
            {
                if (_disposed || now < _dueAt) return;
                _dueAt = _period == Timeout.InfiniteTimeSpan
                    ? DateTimeOffset.MaxValue
                    : now + _period;
                callback(state);
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                owner.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class FakeObservations : IDesktopObservationSource
    {
        public event Action<DesktopObservation>? Observation;
        public DesktopActivity CurrentActivity { get; set; } = DesktopActivity.None;
        public void Start() { }
        public void Stop() { }

        public void Activate(string? appIdentityKey, string? title = null)
            => Raise(DesktopObservation.AppActivated(new DesktopActivity(appIdentityKey, title)));

        public void Focus(string? appIdentityKey, string? title = null)
            => Raise(DesktopObservation.FocusedWindowChanged(new DesktopActivity(appIdentityKey, title)));

        public void ChangeTitle(string? appIdentityKey, string? title)
            => Raise(DesktopObservation.TitleChanged(new DesktopActivity(appIdentityKey, title)));

        public void EnterAway() => Raise(DesktopObservation.EnteredAway());

        public void ExitAway(string? appIdentityKey, string? title = null)
            => Raise(DesktopObservation.ExitedAway(new DesktopActivity(appIdentityKey, title)));

        private void Raise(DesktopObservation observation)
        {
            if (observation.Kind is DesktopObservationKind.AppActivated
                or DesktopObservationKind.FocusedWindowChanged
                or DesktopObservationKind.TitleChanged
                or DesktopObservationKind.ExitedAway)
            {
                CurrentActivity = observation.Activity;
            }
            Observation?.Invoke(observation);
        }
    }

    private sealed class FakeInteractionSignal : IInputActivitySignal
    {
        public bool RecentClick { get; set; }
        public void MarkClick() => RecentClick = true;
        public bool ClickedWithin(TimeSpan window) => RecentClick;
    }

    private sealed class FakeSettings : IDesktopSettings
    {
        public IReadOnlyList<string> AwayProcessNames { get; private set; } = ["LockApp"];
        public bool SplitFocusedWindowChangesUnconditionally { get; set; } = true;
        public event Action<IReadOnlyList<string>>? AwayProcessNamesChanged;

        public void SetAwayProcessNames(params string[] names)
        {
            AwayProcessNames = names;
            AwayProcessNamesChanged?.Invoke(AwayProcessNames);
        }
    }

    private sealed class CapturingSink : ISystemSegmentPublisher
    {
        public List<ForegroundSegmentSnapshot> Items { get; } = [];
        public void Publish(ForegroundSegmentSnapshot snapshot) => Items.Add(snapshot);
        public void StageDurableBatch(IReadOnlyList<ForegroundSegmentSnapshot> snapshots) => Items.AddRange(snapshots);
        public void RecoverInterruptedSegment(DateTimeOffset recoveredAt) { }
        public void ClearActiveCheckpoint(Guid factId, long revision) { }
        public List<ForegroundSegmentSnapshot> Drain()
        {
            var result = Items.ToList();
            Items.Clear();
            return result;
        }
    }

    private sealed class BlockingSink : ISystemSegmentPublisher
    {
        private readonly ManualResetEventSlim _entered = new(false);
        private readonly ManualResetEventSlim _release = new(false);
        private int _blockNext = 1;

        public List<ForegroundSegmentSnapshot> Items { get; } = [];

        public void Publish(ForegroundSegmentSnapshot snapshot)
        {
            if (Interlocked.Exchange(ref _blockNext, 0) == 1)
            {
                _entered.Set();
                _release.Wait();
            }
            Items.Add(snapshot);
        }

        public void StageDurableBatch(IReadOnlyList<ForegroundSegmentSnapshot> snapshots)
        {
            foreach (var snapshot in snapshots)
                Publish(snapshot);
        }
        public void RecoverInterruptedSegment(DateTimeOffset recoveredAt) { }
        public void ClearActiveCheckpoint(Guid factId, long revision) { }

        public void WaitUntilBlocked() => Assert.True(_entered.Wait(TimeSpan.FromSeconds(5)));
        public void Release() => _release.Set();
    }

    private sealed class CapturingActivity : ICurrentActivitySink
    {
        public List<CurrentActivity?> Values { get; } = [];
        public void Report(CurrentActivity? activity) => Values.Add(activity);
    }

    private static (
        AppMonitorService Service,
        FakeClock Clock,
        FakeObservations Observations,
        FakeInteractionSignal Interaction,
        FakeSettings Settings,
        CapturingSink Segments,
        CapturingActivity Activity) Build(string? initialAppIdentityKey = null, string? initialTitle = null)
    {
        var clock = new FakeClock();
        var observations = new FakeObservations
        {
            CurrentActivity = new DesktopActivity(initialAppIdentityKey, initialTitle)
        };
        var interaction = new FakeInteractionSignal();
        var settings = new FakeSettings();
        var segments = new CapturingSink();
        var activity = new CapturingActivity();
        var service = new AppMonitorService(clock, observations, interaction, segments, activity, settings);
        service.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        return (service, clock, observations, interaction, settings, segments, activity);
    }

    private static List<ForegroundSegmentSnapshot> Flush(AppMonitorService service, CapturingSink sink)
    {
        service.PushCurrentSnapshot();
        return sink.Drain();
    }

    [Fact]
    public void AppActivation_ClosesPreviousSegment_AndRefreshesCurrentActivity()
    {
        var x = Build("win:code", "main.cs");
        x.Clock.Advance(TimeSpan.FromSeconds(60));

        x.Observations.Activate("win:chrome", "Docs");

        var segment = Assert.Single(x.Segments.Drain());
        Assert.Equal("win:code", segment.AppIdentityKey);
        Assert.Equal("main.cs", segment.Title);
        Assert.Equal(60, (segment.End - segment.Start).TotalSeconds);
        Assert.Equal("win:chrome", x.Activity.Values[^1]!.AppIdentityKey);
    }

    [Fact]
    public void FocusedWindowChange_AlwaysSplits_EvenWhenTextIsIdentical()
    {
        var x = Build("win:code", "README.md");
        x.Clock.Advance(TimeSpan.FromSeconds(30));

        x.Observations.Focus("win:code", "README.md");

        var segment = Assert.Single(x.Segments.Drain());
        Assert.Equal("win:code", segment.AppIdentityKey);
        Assert.Equal("README.md", segment.Title);
        Assert.Equal(30, (segment.End - segment.Start).TotalSeconds);
    }

    [Fact]
    public void LegacyWindowsFocusPolicy_PreservesOldSameTextOutput()
    {
        var x = Build("win:code", "README.md");
        x.Settings.SplitFocusedWindowChangesUnconditionally = false;
        x.Clock.Advance(TimeSpan.FromSeconds(30));

        x.Observations.Focus("win:code", "README.md");

        Assert.Empty(x.Segments.Drain());
    }

    [Fact]
    public void SameWindowTitleNoise_WithoutInteraction_DoesNotSplit()
    {
        var x = Build("win:windowsterminal", "✳ Claude Code");
        x.Clock.Advance(TimeSpan.FromSeconds(30));
        x.Interaction.RecentClick = false;
        x.Observations.ChangeTitle("win:windowsterminal", "⠐ Claude Code");
        x.Clock.Advance(TimeSpan.FromSeconds(30));

        var segment = Assert.Single(Flush(x.Service, x.Segments));
        Assert.Equal("✳ Claude Code", segment.Title);
        Assert.Equal(60, (segment.End - segment.Start).TotalSeconds);
    }

    [Fact]
    public void SameWindowTitleChange_WithInteraction_Splits()
    {
        var x = Build("win:msedge", "YouTube");
        x.Clock.Advance(TimeSpan.FromSeconds(30));
        x.Interaction.RecentClick = true;

        x.Observations.ChangeTitle("win:msedge", "GitHub");

        var segment = Assert.Single(x.Segments.Drain());
        Assert.Equal("YouTube", segment.Title);
    }

    [Fact]
    public void AwayTransition_ClosesActivity_EmitsAway_AndReopensForeground()
    {
        var x = Build("win:code");
        x.Clock.Advance(TimeSpan.FromSeconds(20));
        x.Observations.EnterAway();
        x.Clock.Advance(TimeSpan.FromMinutes(5));
        x.Observations.ExitAway("win:chrome", "Docs");

        var segments = x.Segments.Drain();
        Assert.Equal(2, segments.Count);
        Assert.Equal("win:code", segments[0].AppIdentityKey);
        Assert.Equal("sys:away", segments[1].AppIdentityKey);
        Assert.Equal(300, (segments[1].End - segments[1].Start).TotalSeconds);
        Assert.Equal(
            ["win:code", "sys:away", "win:chrome"],
            x.Activity.Values.Select(value => value?.AppIdentityKey));
    }

    [Fact]
    public void CurrentActivity_IsPublishedAtEverySemanticTransition()
    {
        var x = Build("win:code");

        x.Observations.Activate("win:chrome");
        x.Observations.EnterAway();
        x.Observations.ExitAway("win:terminal");

        Assert.Equal(
            ["win:code", "win:chrome", "sys:away", "win:terminal"],
            x.Activity.Values.Select(value => value?.AppIdentityKey));
    }

    [Fact]
    public void SnapshotAtRotationBoundary_FinalizesAndContinuesActiveSegment()
    {
        var x = Build("win:code", "main.cs");
        var rotateAfter = SegmentValidationPolicy.MaxDuration - TimeSpan.FromHours(1);
        x.Clock.Advance(rotateAfter);

        var boundary = Flush(x.Service, x.Segments);
        Assert.Equal(2, boundary.Count);
        var finalized = boundary[0];
        var continuation = boundary[1];

        Assert.True(finalized.IsFinal);
        Assert.Equal(rotateAfter, finalized.End - finalized.Start);
        Assert.Equal(1, finalized.Revision);
        Assert.Equal(7, finalized.FactId.Version);
        Assert.False(continuation.IsFinal);
        Assert.Equal(finalized.End, continuation.Start);
        Assert.Equal(continuation.Start, continuation.End);
        Assert.NotEqual(finalized.FactId, continuation.FactId);
        Assert.Equal(1, continuation.Revision);

        x.Clock.Advance(TimeSpan.FromSeconds(30));
        var continued = Assert.Single(Flush(x.Service, x.Segments));
        Assert.False(continued.IsFinal);
        Assert.Equal(continuation.FactId, continued.FactId);
        Assert.Equal(finalized.End, continued.Start);
        Assert.Equal(2, continued.Revision);
        Assert.Equal("win:code", continued.AppIdentityKey);
        Assert.Equal("main.cs", continued.Title);
    }

    [Fact]
    public void SnapshotBeforeRotationBoundary_RemainsOnCurrentFact()
    {
        var x = Build("win:code", "main.cs");
        var beforeBoundary = SegmentRotationPolicy.RotateAfter - TimeSpan.FromSeconds(1);
        x.Clock.Advance(beforeBoundary);

        var snapshot = Assert.Single(Flush(x.Service, x.Segments));

        Assert.False(snapshot.IsFinal);
        Assert.Equal(beforeBoundary, snapshot.End - snapshot.Start);
        Assert.Equal(1, snapshot.Revision);
    }

    [Fact]
    public void AwaySnapshotAtRotationBoundary_FinalizesAndContinuesAwaySegment()
    {
        var x = Build("win:code");
        x.Observations.EnterAway();
        x.Segments.Drain();
        x.Clock.Advance(SegmentRotationPolicy.RotateAfter);

        var boundary = Flush(x.Service, x.Segments);
        Assert.Equal(2, boundary.Count);
        var finalized = boundary[0];
        var continuationAtBoundary = boundary[1];
        x.Clock.Advance(TimeSpan.FromSeconds(30));
        var continued = Assert.Single(Flush(x.Service, x.Segments));

        Assert.True(finalized.IsFinal);
        Assert.Equal(AppIdentityKeys.Away, finalized.AppIdentityKey);
        Assert.False(continued.IsFinal);
        Assert.Equal(AppIdentityKeys.Away, continued.AppIdentityKey);
        Assert.Equal(continuationAtBoundary.FactId, continued.FactId);
        Assert.Equal(finalized.End, continued.Start);
        Assert.Equal(2, continued.Revision);
    }

    [Fact]
    public void SnapshotAfterMultipleRotationBoundaries_EmitsContinuousBoundedChunks()
    {
        var x = Build("win:code", "main.cs");
        var elapsed = SegmentRotationPolicy.RotateAfter * 2 + TimeSpan.FromMinutes(5);
        x.Clock.Advance(elapsed);

        var snapshots = Flush(x.Service, x.Segments);

        Assert.Equal(3, snapshots.Count);
        Assert.True(snapshots[0].IsFinal);
        Assert.True(snapshots[1].IsFinal);
        Assert.False(snapshots[2].IsFinal);
        Assert.Equal(snapshots[0].End, snapshots[1].Start);
        Assert.Equal(snapshots[1].End, snapshots[2].Start);
        Assert.All(snapshots, snapshot =>
        {
            Assert.True(snapshot.End - snapshot.Start <= SegmentRotationPolicy.RotateAfter);
            Assert.Equal(1, snapshot.Revision);
        });
        Assert.Equal(3, snapshots.Select(snapshot => snapshot.FactId).Distinct().Count());
        Assert.Equal(elapsed, snapshots.Aggregate(
            TimeSpan.Zero, (total, snapshot) => total + (snapshot.End - snapshot.Start)));
    }

    [Fact]
    public void AppTransitionAtRotationBoundary_DoesNotOpenDuplicateContinuation()
    {
        var x = Build("win:code", "main.cs");
        x.Clock.Advance(SegmentRotationPolicy.RotateAfter);

        x.Observations.Activate("win:chrome", "Docs");

        var finalized = Assert.Single(x.Segments.Drain());
        Assert.True(finalized.IsFinal);
        Assert.Equal("win:code", finalized.AppIdentityKey);

        x.Clock.Advance(TimeSpan.FromSeconds(30));
        var next = Assert.Single(Flush(x.Service, x.Segments));
        Assert.Equal("win:chrome", next.AppIdentityKey);
        Assert.Equal(finalized.End, next.Start);
        Assert.NotEqual(finalized.FactId, next.FactId);
    }

    [Fact]
    public async Task BoundaryTickAndTransition_ReadTimeInsideTheStateLock()
    {
        var clock = new CoordinatedClock();
        var observations = new FakeObservations
        {
            CurrentActivity = new DesktopActivity("win:code", "main.cs")
        };
        var segments = new CapturingSink();
        var service = new AppMonitorService(
            clock,
            observations,
            new FakeInteractionSignal(),
            segments,
            new CapturingActivity(),
            new FakeSettings());
        await service.StartAsync(CancellationToken.None);
        clock.Advance(SegmentRotationPolicy.RotateAfter - TimeSpan.FromSeconds(1));
        clock.BlockNextRead();

        var transition = Task.Run(() => observations.Activate("win:chrome", "Docs"));
        clock.WaitForBlockedRead();
        clock.Advance(TimeSpan.FromSeconds(1));
        var tick = Task.Run(service.PushCurrentSnapshot);
        clock.ReleaseBlockedRead();
        await Task.WhenAll(transition, tick);
        clock.Advance(TimeSpan.FromSeconds(2));
        service.PushCurrentSnapshot();

        var snapshots = segments.Drain()
            .GroupBy(snapshot => snapshot.FactId)
            .Select(group => group.MaxBy(snapshot => snapshot.Revision)!)
            .OrderBy(snapshot => snapshot.Start)
            .ToList();
        Assert.Equal(2, snapshots.Count);
        Assert.Equal("win:code", snapshots[0].AppIdentityKey);
        Assert.Equal("win:chrome", snapshots[1].AppIdentityKey);
        Assert.Equal(snapshots[0].End, snapshots[1].Start);
    }

    [Fact]
    public async Task TransitionDuringDurableRolloverStage_DoesNotPersistAnOverlappingPlan()
    {
        var clock = new FakeClock();
        var observations = new FakeObservations
        {
            CurrentActivity = new DesktopActivity("win:code", "main.cs")
        };
        var segments = new BlockingSink();
        var service = new AppMonitorService(
            clock,
            observations,
            new FakeInteractionSignal(),
            segments,
            new CapturingActivity(),
            new FakeSettings());
        await service.StartAsync(CancellationToken.None);
        clock.Advance(SegmentRotationPolicy.RotateAfter);

        var rollover = Task.Run(service.PushCurrentSnapshot);
        segments.WaitUntilBlocked();
        clock.Advance(TimeSpan.FromSeconds(1));
        var transition = Task.Run(() => observations.Activate("win:chrome", "Docs"));
        await transition.WaitAsync(TimeSpan.FromSeconds(1));
        segments.Release();
        await rollover.WaitAsync(TimeSpan.FromSeconds(2));
        clock.Advance(TimeSpan.FromSeconds(1));
        service.PushCurrentSnapshot();

        var latest = segments.Items
            .GroupBy(snapshot => snapshot.FactId)
            .Select(group => group.MaxBy(snapshot => snapshot.Revision)!)
            .OrderBy(snapshot => snapshot.Start)
            .ThenBy(snapshot => snapshot.End)
            .ToList();
        Assert.DoesNotContain(
            segments.Items.GroupBy(snapshot => (snapshot.FactId, snapshot.Revision)),
            attempts => attempts.Distinct().Count() != 1);
        Assert.All(latest.Zip(latest.Skip(1)), pair => Assert.True(
            pair.First.End <= pair.Second.Start,
            $"Segments {pair.First.FactId} and {pair.Second.FactId} overlap."));
        Assert.Equal("win:code", latest[0].AppIdentityKey);
        Assert.Equal("win:chrome", latest[^1].AppIdentityKey);
        Assert.Equal("win:chrome", Assert.Single(latest, snapshot => !snapshot.IsFinal).AppIdentityKey);
    }

    [Fact]
    public async Task StopDuringDurableRolloverStage_CommitsAnAlreadyDeferredTransition()
    {
        var clock = new FakeClock();
        var observations = new FakeObservations
        {
            CurrentActivity = new DesktopActivity("win:code", "main.cs")
        };
        var segments = new BlockingSink();
        var activities = new CapturingActivity();
        var service = new AppMonitorService(
            clock,
            observations,
            new FakeInteractionSignal(),
            segments,
            activities,
            new FakeSettings());
        await service.StartAsync(CancellationToken.None);
        clock.Advance(SegmentRotationPolicy.RotateAfter);

        var rollover = Task.Run(service.PushCurrentSnapshot);
        segments.WaitUntilBlocked();
        clock.Advance(TimeSpan.FromSeconds(1));
        observations.Activate("win:chrome", "Docs");

        var stop = service.StopAsync(CancellationToken.None);
        Assert.False(stop.IsCompleted);
        segments.Release();
        await Task.WhenAll(rollover, stop);

        Assert.Contains(activities.Values, activity => activity?.AppIdentityKey == "win:chrome");
        var latest = segments.Items
            .GroupBy(snapshot => snapshot.FactId)
            .Select(group => group.MaxBy(snapshot => snapshot.Revision)!)
            .OrderBy(snapshot => snapshot.Start)
            .ThenBy(snapshot => snapshot.End)
            .ToList();
        Assert.Contains(latest, snapshot =>
            snapshot.AppIdentityKey == "win:code"
            && snapshot.IsFinal
            && snapshot.End == clock.UtcNow);
        Assert.All(latest.Zip(latest.Skip(1)), pair => Assert.True(pair.First.End <= pair.Second.Start));
    }

    [Fact]
    public async Task RealSnapshotTimer_RotatesWithoutObservationChange_AndStopsBeforeFinal()
    {
        var clock = new ManualTimerClock();
        var observations = new FakeObservations
        {
            CurrentActivity = new DesktopActivity("win:code", "main.cs")
        };
        var segments = new CapturingSink();
        var service = new AppMonitorService(
            clock,
            observations,
            new FakeInteractionSignal(),
            segments,
            new CapturingActivity(),
            new FakeSettings(),
            clock);
        await service.StartAsync(CancellationToken.None);
        clock.WaitForTimer();

        clock.Advance(SegmentRotationPolicy.RotateAfter);
        await WaitUntilAsync(() => segments.Items.Count != 0);
        var rotated = segments.Drain();
        Assert.Equal(2, rotated.Count);
        Assert.True(rotated[0].IsFinal);
        Assert.False(rotated[1].IsFinal);
        Assert.Equal(rotated[0].End, rotated[1].Start);
        Assert.Equal(rotated[1].Start, rotated[1].End);

        clock.Advance(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(() => segments.Items.Count != 0);
        await service.StopAsync(CancellationToken.None);
        var stopped = segments.Drain();
        Assert.True(stopped[^1].IsFinal);

        clock.Advance(TimeSpan.FromMinutes(1));
        await Task.Yield();
        Assert.Empty(segments.Drain());
    }

    [Fact]
    public async Task StopWaitsForInFlightTimerSnapshotBeforePublishingFinalRevision()
    {
        var clock = new ManualTimerClock();
        var observations = new FakeObservations
        {
            CurrentActivity = new DesktopActivity("win:code", "main.cs")
        };
        var segments = new BlockingSink();
        var service = new AppMonitorService(
            clock,
            observations,
            new FakeInteractionSignal(),
            segments,
            new CapturingActivity(),
            new FakeSettings(),
            clock);
        await service.StartAsync(CancellationToken.None);
        clock.WaitForTimer();

        var advance = Task.Run(() => clock.Advance(TimeSpan.FromMinutes(1)));
        segments.WaitUntilBlocked();
        var stop = service.StopAsync(CancellationToken.None);
        Assert.False(stop.IsCompleted);

        segments.Release();
        await Task.WhenAll(advance, stop);
        Assert.Equal(2, segments.Items.Count);
        Assert.False(segments.Items[0].IsFinal);
        Assert.True(segments.Items[1].IsFinal);
        Assert.Equal(segments.Items[0].FactId, segments.Items[1].FactId);
        Assert.True(segments.Items[1].Revision > segments.Items[0].Revision);

        clock.Advance(TimeSpan.FromMinutes(1));
        await Task.Yield();
        Assert.Equal(2, segments.Items.Count);
    }

    [Fact]
    public async Task StopAfterRotationBoundary_FinalizesEveryContinuousChunk()
    {
        var x = Build("win:code", "main.cs");
        var elapsed = SegmentRotationPolicy.RotateAfter + TimeSpan.FromMinutes(5);
        x.Clock.Advance(elapsed);

        await x.Service.StopAsync(CancellationToken.None);

        var snapshots = x.Segments.Drain();
        Assert.Equal(2, snapshots.Count);
        Assert.All(snapshots, snapshot => Assert.True(snapshot.IsFinal));
        Assert.Equal(snapshots[0].End, snapshots[1].Start);
        Assert.Equal(elapsed, snapshots.Aggregate(
            TimeSpan.Zero, (total, snapshot) => total + (snapshot.End - snapshot.Start)));
    }

    [Fact]
    public async Task StopWithinOneSecondAfterRotationBoundary_PreservesContinuationTail()
    {
        var x = Build("win:code", "main.cs");
        var elapsed = SegmentRotationPolicy.RotateAfter + TimeSpan.FromMilliseconds(500);
        x.Clock.Advance(elapsed);

        await x.Service.StopAsync(CancellationToken.None);

        var snapshots = x.Segments.Drain();
        Assert.Equal(2, snapshots.Count);
        Assert.All(snapshots, snapshot => Assert.True(snapshot.IsFinal));
        Assert.Equal(snapshots[0].End, snapshots[1].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(500), snapshots[1].End - snapshots[1].Start);
        Assert.Equal(elapsed, snapshots.Aggregate(
            TimeSpan.Zero, (total, snapshot) => total + (snapshot.End - snapshot.Start)));
    }

    [Fact]
    public async Task StopAsync_PushesFinalSnapshot()
    {
        var x = Build("win:code", "main.cs");
        x.Clock.Advance(TimeSpan.FromSeconds(45));

        await x.Service.StopAsync(CancellationToken.None);

        var segment = Assert.Single(x.Segments.Drain());
        Assert.Equal("win:code", segment.AppIdentityKey);
        Assert.Equal(45, (segment.End - segment.Start).TotalSeconds);
    }

    [Fact]
    public void AwayProcessNormalization_HotReloadsThroughSettingsSeam()
    {
        var x = Build("win:code");
        x.Settings.SetAwayProcessNames("ScreenLock");
        x.Clock.Advance(TimeSpan.FromSeconds(10));

        x.Observations.Activate("win:screenlock");

        Assert.Equal("sys:away", x.Activity.Values[^1]!.AppIdentityKey);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeout)
                throw new TimeoutException("The asynchronous test condition was not reached.");
            await Task.Delay(10);
        }
    }
}
