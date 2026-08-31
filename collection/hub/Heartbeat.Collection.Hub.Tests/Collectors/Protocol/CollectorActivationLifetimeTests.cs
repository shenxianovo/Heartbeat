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
    public async Task ReadyWinningStopRacePublishesBeforeStopAndOwnerStillReleases()
    {
        var driver = new ControlledDriver();
        var release = 0;
        var publicationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowPublication = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = CreateLifetime(driver, release: () => Interlocked.Increment(ref release));

        var ready = lifetime.PublishReadyAsync(new CollectorReadyPublication(async () =>
        {
            publicationEntered.SetResult();
            await allowPublication.Task;
        }));
        await publicationEntered.Task;

        _ = lifetime.RequestStopAsync(
            new CollectorActivationStopIntent(CollectorActivationStopCause.RuntimeStopping));
        Assert.False(driver.StopEntered.IsCompleted);
        allowPublication.SetResult();

        Assert.Equal(CollectorReadyOutcome.Published, await ready);
        await driver.StopEntered;
        driver.Complete(Drained());
        await lifetime.Terminal;
        Assert.Equal(1, release);
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
        var never = new TaskCompletionSource<CollectorActivationDrainOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var driver = new DelegateDriver((_, _) => new ValueTask<CollectorActivationDrainOutcome>(never.Task));
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

    private static CollectorActivationDrainOutcome Drained() => new(
        0,
        0,
        CollectorDrainReason.Drained,
        RemainderDurable: true,
        CollectorDrainCompletionReason.Completed);

    private sealed class ControlledDriver : ICollectorActivationLifetimeDriver
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<CollectorActivationDrainOutcome> _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StopEntered => _entered.Task;
        public int CooperativeStopAttempts => 2;

        public ValueTask<CollectorActivationDrainOutcome> StopAsync(
            DateTimeOffset deadline,
            CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            return new ValueTask<CollectorActivationDrainOutcome>(_completion.Task);
        }

        public void Complete(CollectorActivationDrainOutcome result) => _completion.TrySetResult(result);
    }

    private sealed class DelegateDriver(
        Func<DateTimeOffset, CancellationToken, ValueTask<CollectorActivationDrainOutcome>> stop)
        : ICollectorActivationLifetimeDriver
    {
        public int CooperativeStopAttempts => 2;

        public ValueTask<CollectorActivationDrainOutcome> StopAsync(
            DateTimeOffset deadline,
            CancellationToken cancellationToken) => stop(deadline, cancellationToken);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan value) => now += value;
    }
}
