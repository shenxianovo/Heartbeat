using Heartbeat.Collection.Hub.Collectors.Protocol;

namespace Heartbeat.Collection.Hub.Tests.Collectors.Protocol;

public sealed class CollectorActivationLifetimeTests
{
    [Fact]
    public async Task ConcurrentStopIntentsSharePersistentTerminalResult()
    {
        var driver = new ControlledDriver();
        var lifetime = CreateLifetime(driver);

        var first = lifetime.RequestStopAsync(
            new CollectorActivationStopIntent(CollectorActivationStopCause.RuntimeStopping));
        var second = lifetime.RequestStopAsync(
            new CollectorActivationStopIntent(CollectorActivationStopCause.ActivationFailed));

        Assert.Same(lifetime.Terminal, first.AsTask());
        Assert.Same(lifetime.Terminal, second.AsTask());
        driver.Complete(Drained());

        var firstResult = await first;
        var secondResult = await second;
        Assert.Same(firstResult, secondResult);
        Assert.Equal(CollectorActivationStopCause.RuntimeStopping, firstResult.WinningIntent.Cause);
    }

    [Fact]
    public async Task WaiterCancellationCancelsOnlyItsWait()
    {
        var driver = new ControlledDriver();
        var lifetime = CreateLifetime(driver);
        using var waiter = new CancellationTokenSource();

        var waiting = lifetime.RequestStopAsync(
            new CollectorActivationStopIntent(CollectorActivationStopCause.RuntimeStopping),
            waiter.Token);
        await driver.StopEntered;
        waiter.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.AsTask());
        Assert.False(lifetime.Terminal.IsCompleted);

        driver.Complete(Drained());
        Assert.Equal(CollectorDrainReason.Drained, (await lifetime.Terminal).DrainOutcome.Reason);
    }

    [Fact]
    public async Task StopWinningReadyRaceDoesNotPublishWriter()
    {
        var driver = new ControlledDriver();
        var lifetime = CreateLifetime(driver);
        var published = 0;

        _ = lifetime.RequestStopAsync(
            new CollectorActivationStopIntent(CollectorActivationStopCause.RuntimeStopping));
        await driver.StopEntered;
        var ready = await lifetime.PublishReadyAsync(
            new CollectorReadyPublication(() => Interlocked.Increment(ref published)));

        Assert.Equal(CollectorReadyOutcome.Stopping, ready);
        Assert.Equal(0, published);
        driver.Complete(Drained());
        await lifetime.Terminal;
    }

    [Fact]
    public async Task BlockingReadyPreparationCannotCommitAfterStopDeadline()
    {
        var time = new ControlledTimeProvider();
        var neverStops = new TaskCompletionSource<CollectorActivationDriverStopResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var driver = new DelegateDriver((_, _) => new ValueTask<CollectorActivationDriverStopResult>(neverStops.Task));
        var fenced = 0;
        var release = 0;
        var preparationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowPreparation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var committed = 0;
        var lifetime = CreateLifetime(
            driver,
            fence: () => Interlocked.Increment(ref fenced),
            release: () => Interlocked.Increment(ref release),
            timeProvider: time,
            drainBudget: TimeSpan.FromSeconds(10));

        var publication = new CollectorReadyPublication(
            async () =>
            {
                preparationEntered.SetResult();
                await allowPreparation.Task;
            },
            () => Interlocked.Increment(ref committed));
        var ready = lifetime.PublishReadyAsync(publication);
        await preparationEntered.Task;

        var stopping = lifetime.RequestStopAsync(
            new CollectorActivationStopIntent(CollectorActivationStopCause.RuntimeStopping));
        Assert.True(lifetime.StopRequested.IsCancellationRequested);
        Assert.False(ready.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(10));
        var terminal = await stopping;
        Assert.Equal(CollectorDrainReason.DeadlineExceeded, terminal.DrainOutcome.Reason);
        Assert.Equal(1, fenced);
        Assert.Equal(1, release);

        allowPreparation.SetResult();
        Assert.Equal(0, committed);
        Assert.Equal(CollectorReadyOutcome.Stopping, await ready);
    }

    [Fact]
    public async Task PreparedReadyCommitWinningStopRacePublishesBeforeStop()
    {
        var driver = new ControlledDriver();
        var committed = 0;
        var commitEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = CreateLifetime(driver);
        var stopping = Task.Run(async () =>
        {
            await commitEntered.Task;
            return await lifetime.RequestStopAsync(
                new CollectorActivationStopIntent(CollectorActivationStopCause.RuntimeStopping));
        });

        var ready = await lifetime.PublishReadyAsync(new CollectorReadyPublication(() =>
        {
            commitEntered.SetResult();
            Interlocked.Increment(ref committed);
        }));

        Assert.Equal(CollectorReadyOutcome.Published, ready);
        Assert.Equal(1, committed);
        await driver.StopEntered;
        driver.Complete(Drained());
        _ = await stopping;
    }

    [Fact]
    public async Task StalePreparedReadyCommitIsDisposedAndReprepared()
    {
        var preparations = 0;
        var disposals = 0;
        var lifetime = CreateLifetime(new ControlledDriver());
        var publication = new CollectorReadyPublication(() =>
        {
            var attempt = Interlocked.Increment(ref preparations);
            return ValueTask.FromResult(new CollectorReadyPreparedCommit(
                () => attempt == 2,
                new DelegateDisposable(() => Interlocked.Increment(ref disposals))));
        });

        var outcome = await lifetime.PublishReadyAsync(publication);

        Assert.Equal(CollectorReadyOutcome.Published, outcome);
        Assert.Equal(2, preparations);
        Assert.Equal(2, disposals);
    }

    [Fact]
    public async Task StopWinningBlockedPreparationStabilizesLaterPreparationFailureAsStopping()
    {
        var driver = new ControlledDriver();
        var preparationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new IOException("late preparation failed");
        var lifetime = CreateLifetime(driver);
        var ready = lifetime.PublishReadyAsync(new CollectorReadyPublication(
            async () =>
            {
                preparationEntered.SetResult();
                await allowFailure.Task;
                throw failure;
            },
            () => throw new InvalidOperationException("commit must not run")));
        await preparationEntered.Task;

        var stopping = lifetime.RequestStopAsync(
            new CollectorActivationStopIntent(CollectorActivationStopCause.RuntimeStopping));
        await driver.StopEntered;
        driver.Complete(Drained());
        _ = await stopping;
        allowFailure.SetResult();

        Assert.Equal(CollectorReadyOutcome.Stopping, await ready);
    }

    [Fact]
    public async Task StopWinningBlockedPreparationStabilizesLaterDisposeFailureAsStopping()
    {
        var driver = new ControlledDriver();
        var preparationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowPreparation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = CreateLifetime(driver);
        var ready = lifetime.PublishReadyAsync(new CollectorReadyPublication(async () =>
        {
            preparationEntered.SetResult();
            await allowPreparation.Task;
            return new CollectorReadyPreparedCommit(
                () => throw new InvalidOperationException("commit must not run"),
                new DelegateDisposable(() => throw new IOException("late dispose failed")));
        }));
        await preparationEntered.Task;

        var stopping = lifetime.RequestStopAsync(
            new CollectorActivationStopIntent(CollectorActivationStopCause.RuntimeStopping));
        await driver.StopEntered;
        driver.Complete(Drained());
        _ = await stopping;
        allowPreparation.SetResult();

        Assert.Equal(CollectorReadyOutcome.Stopping, await ready);
    }

    [Fact]
    public async Task PreparationFailureWithoutStopPropagatesOriginalError()
    {
        var failure = new IOException("preparation failed");
        var lifetime = CreateLifetime(new ControlledDriver());

        var observed = await Assert.ThrowsAsync<IOException>(() =>
            lifetime.PublishReadyAsync(new CollectorReadyPublication(
                () => throw failure,
                () => throw new InvalidOperationException("commit must not run"))).AsTask());

        Assert.Same(failure, observed);
    }

    [Fact]
    public async Task DisposeFailureAfterReadyWinsPropagatesOriginalError()
    {
        var failure = new IOException("dispose failed");
        var lifetime = CreateLifetime(new ControlledDriver());

        var observed = await Assert.ThrowsAsync<IOException>(() =>
            lifetime.PublishReadyAsync(new CollectorReadyPublication(() =>
                ValueTask.FromResult(new CollectorReadyPreparedCommit(
                    () => true,
                    new DelegateDisposable(() => throw failure))))).AsTask());

        Assert.Same(failure, observed);
    }

    [Fact]
    public async Task DriverEntryObservesStopRequestedAlreadyCanceled()
    {
        CollectorActivationLifetime? lifetime = null;
        var observedCanceled = false;
        var driver = new DelegateDriver((_, _) =>
        {
            observedCanceled = lifetime!.StopRequested.IsCancellationRequested;
            return ValueTask.FromResult(Drained());
        });
        lifetime = CreateLifetime(driver);

        _ = await lifetime.RequestStopAsync(
            new CollectorActivationStopIntent(CollectorActivationStopCause.RuntimeStopping));

        Assert.True(observedCanceled);
    }

    [Fact]
    public async Task FirstStopIntentFixesOneAbsoluteDeadlineAcrossAttemptsAndCallers()
    {
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 8, 31, 1, 2, 3, TimeSpan.Zero));
        var observedDeadlines = new List<DateTimeOffset>();
        var driver = new DelegateDriver((deadline, _) =>
        {
            observedDeadlines.Add(deadline);
            if (observedDeadlines.Count == 1)
            {
                time.Advance(TimeSpan.FromSeconds(3));
                throw new IOException("transient");
            }
            return ValueTask.FromResult(Drained());
        });
        var lifetime = CreateLifetime(driver, timeProvider: time, drainBudget: TimeSpan.FromSeconds(10));

        var first = lifetime.RequestStopAsync(
            new CollectorActivationStopIntent(CollectorActivationStopCause.RuntimeStopping));
        var second = lifetime.RequestStopAsync(
            new CollectorActivationStopIntent(CollectorActivationStopCause.ActivationFailed));
        var result = await first;

        Assert.Equal(new DateTimeOffset(2026, 8, 31, 1, 2, 13, TimeSpan.Zero), result.Deadline);
        Assert.Equal([result.Deadline, result.Deadline], observedDeadlines);
        Assert.Same(result, await second);
    }

    [Fact]
    public async Task StopCompletingAtDeadlineIsRejectedEvenWhenAttemptWinsWhenAnyRace()
    {
        var time = new ControlledTimeProvider();
        var driver = new DelegateDriver((_, _) =>
        {
            time.Advance(TimeSpan.FromSeconds(10));
            return ValueTask.FromResult(Drained());
        });
        var lifetime = CreateLifetime(
            driver,
            timeProvider: time,
            drainBudget: TimeSpan.FromSeconds(10));

        var terminal = await lifetime.RequestStopAsync(
            new CollectorActivationStopIntent(CollectorActivationStopCause.RuntimeStopping));

        Assert.Equal(CollectorDrainReason.DeadlineExceeded, terminal.DrainOutcome.Reason);
        Assert.IsType<InProcessFencedExecution>(terminal.Execution);
    }

    [Fact]
    public async Task InProcessStopFailureRetryPolicyIsInternalAndBoundedToTwoAttempts()
    {
        var attempts = 0;
        var driver = new DelegateDriver((_, _) =>
        {
            attempts++;
            if (attempts == 1)
                throw new IOException("transient");
            return ValueTask.FromResult(Drained());
        });
        var lifetime = CreateLifetime(driver);

        var first = lifetime.RequestStopAsync(
            new CollectorActivationStopIntent(CollectorActivationStopCause.RuntimeStopping));
        var second = lifetime.RequestStopAsync(
            new CollectorActivationStopIntent(CollectorActivationStopCause.Deactivated));
        var result = await first;

        Assert.Equal(2, attempts);
        Assert.Equal(2, result.StopAttempts);
        Assert.Null(result.StopError);
        Assert.Same(result, await second);
    }

    [Fact]
    public async Task PermanentStopFailureFencesAndReleasesExactlyOnce()
    {
        var driver = new DelegateDriver((_, _) => throw new IOException("permanent"));
        var fenced = 0;
        var released = 0;
        var lifetime = CreateLifetime(
            driver,
            fence: () => Interlocked.Increment(ref fenced),
            release: () => Interlocked.Increment(ref released));

        var result = await lifetime.RequestStopAsync(
            new CollectorActivationStopIntent(CollectorActivationStopCause.RuntimeStopping));
        var replay = await lifetime.RequestStopAsync(
            new CollectorActivationStopIntent(CollectorActivationStopCause.ActivationFailed));

        Assert.Same(result, replay);
        Assert.Equal(2, result.StopAttempts);
        Assert.IsType<IOException>(result.StopError);
        Assert.Equal(CollectorDrainReason.StopFailed, result.DrainOutcome.Reason);
        Assert.Equal(1, fenced);
        Assert.Equal(1, released);
        Assert.True(result.OwnershipReleased);
        Assert.Null(result.ReleaseError);
    }

    [Fact]
    public async Task DriverProjectsFailedStopBeforeHubFenceAndOwnershipRelease()
    {
        var ordering = new List<string>();
        var lifetime = CreateLifetime(
            new ManagedFailureProjectionDriver(() => ordering.Add("driver-fence")),
            fence: () => ordering.Add("hub-fence"),
            release: () => ordering.Add("release"));

        var terminal = await lifetime.RequestStopAsync(
            new CollectorActivationStopIntent(CollectorActivationStopCause.RuntimeStopping));

        var execution = Assert.IsType<ManagedProcessTerminatedExecution>(terminal.Execution);
        Assert.Equal(ManagedProcessTerminationCause.StopFailed, execution.Cause);
        Assert.Equal(["driver-fence", "hub-fence", "release"], ordering);
    }

    [Fact]
    public async Task FenceFailureRetainsOwnershipAndIsATerminalValue()
    {
        var released = 0;
        var fenceFailure = new IOException("fence failed");
        var lifetime = CreateLifetime(
            new DelegateDriver((_, _) => ValueTask.FromResult(Drained())),
            fence: () => throw fenceFailure,
            release: () => Interlocked.Increment(ref released));

        var result = await lifetime.RequestStopAsync(
            new CollectorActivationStopIntent(CollectorActivationStopCause.RuntimeStopping));

        Assert.False(result.OwnershipReleased);
        Assert.Same(fenceFailure, result.ReleaseError);
        Assert.Equal(0, released);
        Assert.Same(result, await lifetime.Terminal);
    }

    [Fact]
    public async Task DeadlineFencesCancellationIgnoringStopAndReleasesExactlyOnce()
    {
        var never = new TaskCompletionSource<CollectorActivationDriverStopResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var driver = new DelegateDriver((_, _) => new ValueTask<CollectorActivationDriverStopResult>(never.Task));
        var fenced = 0;
        var released = 0;
        var lifetime = CreateLifetime(
            driver,
            fence: () => Interlocked.Increment(ref fenced),
            release: () => Interlocked.Increment(ref released),
            drainBudget: TimeSpan.FromMilliseconds(30));

        var result = await lifetime.RequestStopAsync(
            new CollectorActivationStopIntent(CollectorActivationStopCause.RuntimeStopping));

        Assert.Equal(CollectorDrainReason.DeadlineExceeded, result.DrainOutcome.Reason);
        Assert.Equal(1, result.StopAttempts);
        Assert.Equal(1, fenced);
        Assert.Equal(1, released);
        Assert.Same(result, await lifetime.Terminal);
    }

    private static CollectorActivationLifetime CreateLifetime(
        ICollectorActivationLifetimeDriver driver,
        Action? fence = null,
        Action? release = null,
        TimeProvider? timeProvider = null,
        TimeSpan? drainBudget = null) => new(
            driver,
            fence ?? (() => { }),
            release ?? (() => { }),
            timeProvider ?? TimeProvider.System,
            drainBudget ?? TimeSpan.FromSeconds(5));

    private static CollectorActivationDriverStopResult Drained() => new(
        new CollectorActivationDrainOutcome(
            0,
            0,
            CollectorDrainReason.Drained,
            RemainderDurable: true,
            CollectorDrainCompletionReason.Completed),
        new InProcessStoppedExecution());

    private sealed class ControlledDriver : ICollectorActivationLifetimeDriver
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<CollectorActivationDriverStopResult> _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StopEntered => _entered.Task;
        public int CooperativeStopAttempts => 2;

        public ValueTask<CollectorActivationDriverStopResult> StopAsync(
            CollectorActivationStopIntent intent,
            DateTimeOffset deadline,
            CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            return new ValueTask<CollectorActivationDriverStopResult>(_completion.Task);
        }

        public void Complete(CollectorActivationDriverStopResult result) => _completion.TrySetResult(result);
    }

    private sealed class DelegateDriver(
        Func<DateTimeOffset, CancellationToken, ValueTask<CollectorActivationDriverStopResult>> stop)
        : ICollectorActivationLifetimeDriver
    {
        public int CooperativeStopAttempts => 2;

        public ValueTask<CollectorActivationDriverStopResult> StopAsync(
            CollectorActivationStopIntent intent,
            DateTimeOffset deadline,
            CancellationToken cancellationToken) => stop(deadline, cancellationToken);
    }

    private sealed class ManagedFailureProjectionDriver(Action fence) : ICollectorActivationLifetimeDriver
    {
        public ValueTask<CollectorActivationDriverStopResult> StopAsync(
            CollectorActivationStopIntent intent,
            DateTimeOffset deadline,
            CancellationToken cancellationToken) => throw new IOException("managed stop failed");

        public CollectorActivationExecution ProjectFailedStop(CollectorDrainReason reason) =>
            new ManagedProcessTerminatedExecution(ManagedProcessTerminationCause.StopFailed, null);

        public void FenceAfterDeadline() => fence();
    }

    private sealed class DelegateDisposable(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan value) => now += value;
    }

    private sealed class ControlledTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ControlledTimer> _timers = [];
        private DateTimeOffset _utcNow = new(2026, 8, 31, 1, 2, 3, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
                return _utcNow;
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ControlledTimer(this, callback, state, dueTime, period);
            lock (_gate)
                _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan duration)
        {
            ControlledTimer[] due;
            lock (_gate)
            {
                _utcNow += duration;
                due = _timers.Where(timer => timer.IsDue(_utcNow)).ToArray();
            }
            foreach (var timer in due)
                timer.Fire();
        }

        private sealed class ControlledTimer(
            ControlledTimeProvider owner,
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) : ITimer
        {
            private DateTimeOffset _dueAt = owner.GetUtcNow() + dueTime;
            private bool _disposed;

            public bool IsDue(DateTimeOffset now) => !_disposed && now >= _dueAt;

            public void Fire()
            {
                if (_disposed)
                    return;
                if (period == Timeout.InfiniteTimeSpan)
                    _disposed = true;
                else
                    _dueAt += period;
                callback(state);
            }

            public bool Change(TimeSpan newDueTime, TimeSpan newPeriod)
            {
                if (_disposed)
                    return false;
                dueTime = newDueTime;
                period = newPeriod;
                _dueAt = owner.GetUtcNow() + newDueTime;
                return true;
            }

            public void Dispose() => _disposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
