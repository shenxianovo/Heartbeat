namespace Heartbeat.Collection.Hub.Collectors.Protocol;

internal enum CollectorActivationStopCause
{
    ActivationFailed,
    Deactivated,
    RuntimeStopping,
    Supervision,
    UpdateRequested
}

internal record CollectorActivationStopIntent(CollectorActivationStopCause Cause);

internal abstract record ExternalHostDrainEvidence
{
    internal sealed record NotReported : ExternalHostDrainEvidence;

    internal sealed record HostReported(InProcessCollectorDrainResult Result) : ExternalHostDrainEvidence;
}

internal sealed record ExternalHostCollectorActivationStopIntent(
    ExternalHostActivationStopReason Reason,
    ExternalHostDrainEvidence DrainEvidence)
    : CollectorActivationStopIntent(Reason == ExternalHostActivationStopReason.RuntimeStopping
        ? CollectorActivationStopCause.RuntimeStopping
        : CollectorActivationStopCause.Deactivated);

internal enum CollectorReadyOutcome
{
    Published,
    Stopping
}

internal sealed class CollectorReadyPublication
{
    private readonly Func<ValueTask> _publish;

    public CollectorReadyPublication(Action publish)
    {
        ArgumentNullException.ThrowIfNull(publish);
        _publish = () =>
        {
            publish();
            return ValueTask.CompletedTask;
        };
    }

    public CollectorReadyPublication(Func<ValueTask> publish) =>
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));

    internal ValueTask PublishAsync() => _publish();
}

internal sealed record CollectorActivationTerminalResult(
    CollectorActivationStopIntent WinningIntent,
    DateTimeOffset Deadline,
    CollectorActivationDrainOutcome DrainOutcome,
    int StopAttempts,
    Exception? StopError,
    bool OwnershipReleased,
    Exception? ReleaseError,
    CollectorActivationExecution Execution);

internal abstract record CollectorActivationExecution;

internal sealed record InProcessStoppedExecution : CollectorActivationExecution;

internal sealed record InProcessFencedExecution : CollectorActivationExecution;

internal sealed record ManagedProcessExitedExecution(int? ExitCode) : CollectorActivationExecution;

internal enum ManagedProcessTerminationCause
{
    BeforeReady,
    DrainWriteFailed,
    DeadlineExceeded,
    ProtocolFailure,
    StartupAborted,
    StopFailed
}

internal sealed record ManagedProcessTerminatedExecution(
    ManagedProcessTerminationCause Cause,
    int? ExitCode) : CollectorActivationExecution;

internal sealed record ExternalHostLeaseRevokedExecution(
    ExternalHostActivationStopReason Reason,
    ExternalHostDrainEvidence DrainEvidence) : CollectorActivationExecution;

internal sealed record CollectorActivationDriverStopResult(
    CollectorActivationDrainOutcome DrainOutcome,
    CollectorActivationExecution Execution);

internal sealed record CollectorActivationDrainOutcome(
    int? PendingFacts,
    int? PendingGaps,
    CollectorDrainReason Reason,
    bool RemainderDurable,
    CollectorDrainCompletionReason CompletionReason,
    string? CompletionError = null)
{
    public bool IsFullyDrained =>
        CompletionReason == CollectorDrainCompletionReason.Completed &&
        Reason == CollectorDrainReason.Drained &&
        RemainderDurable &&
        PendingFacts == 0 &&
        PendingGaps == 0;

    internal static CollectorActivationDrainOutcome FromInProcess(
        InProcessCollectorDrainResult result) => new(
        result.PendingFacts,
        result.PendingGaps,
        result.LogicalResult.Reason,
        result.LogicalResult.RemainderDurable,
        result.CompletionReason,
        result.CompletionError);

    internal InProcessCollectorDrainResult ToInProcess() => new(
        new InProcessCollectorLogicalDrainResult(
            PendingFacts,
            PendingGaps,
            Reason,
            RemainderDurable),
        CompletionReason,
        CompletionError);
}

internal interface ICollectorActivationLifetimeDriver
{
    int CooperativeStopAttempts => 1;

    ValueTask<CollectorActivationDriverStopResult> StopAsync(
        CollectorActivationStopIntent intent,
        DateTimeOffset deadline,
        CancellationToken cancellationToken);

    CollectorActivationExecution ProjectFailedStop(CollectorDrainReason reason) =>
        new InProcessFencedExecution();

    void FenceAfterDeadline() { }
}

internal sealed class InProcessCollectorActivationLifetimeDriver(
    IInProcessCollector collector,
    CollectorActivationSession session) : ICollectorActivationLifetimeDriver
{
    public int CooperativeStopAttempts => 2;

    public async ValueTask<CollectorActivationDriverStopResult> StopAsync(
        CollectorActivationStopIntent intent,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        session.BeginDrain();
        var result = await collector.StopAsync(deadline, cancellationToken).ConfigureAwait(false);
        return new CollectorActivationDriverStopResult(
            CollectorActivationDrainOutcome.FromInProcess(result),
            new InProcessStoppedExecution());
    }

    public void FenceAfterDeadline()
    {
        if (collector is IInProcessCollectorDeadlineFence fence)
        {
            Observe(Task.Factory.StartNew(
                fence.FenceAfterDeadline,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default));
        }
    }

    private static void Observe(Task task) => _ = task.ContinueWith(
        static completed => _ = completed.Exception,
        CancellationToken.None,
        TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
        TaskScheduler.Default);
}

internal sealed class ExternalHostCollectorActivationLifetimeDriver(
    CollectorActivationSession session) : ICollectorActivationLifetimeDriver
{
    public ValueTask<CollectorActivationDriverStopResult> StopAsync(
        CollectorActivationStopIntent intent,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        session.BeginDrain();
        var external = intent as ExternalHostCollectorActivationStopIntent;
        var reason = external?.Reason ?? intent.Cause switch
        {
            CollectorActivationStopCause.RuntimeStopping => ExternalHostActivationStopReason.RuntimeStopping,
            _ => ExternalHostActivationStopReason.LeaseExpired
        };
        var evidence = external?.DrainEvidence ?? new ExternalHostDrainEvidence.NotReported();
        session.RecordExternalHostStopReason(reason);
        var drain = evidence switch
        {
            ExternalHostDrainEvidence.HostReported reported =>
                CollectorActivationDrainOutcome.FromInProcess(reported.Result),
            _ => new CollectorActivationDrainOutcome(
                null,
                null,
                CollectorDrainReason.FlushCancelled,
                RemainderDurable: false,
                CollectorDrainCompletionReason.Completed)
        };
        return ValueTask.FromResult(new CollectorActivationDriverStopResult(
            drain,
            new ExternalHostLeaseRevokedExecution(reason, evidence)));
    }

    public CollectorActivationExecution ProjectFailedStop(CollectorDrainReason reason) =>
        new ExternalHostLeaseRevokedExecution(
            ExternalHostActivationStopReason.RuntimeStopping,
            new ExternalHostDrainEvidence.NotReported());
}

/// <summary>
/// Owns one Hub-side Collector Activation transaction from accepted Hello through terminal
/// fencing and release. Ready and Stop Intent linearize here; callers can cancel only their wait.
/// </summary>
internal sealed class CollectorActivationLifetime
{
    private readonly object _gate = new();
    private readonly ICollectorActivationLifetimeDriver _driver;
    private readonly Action _fence;
    private readonly Action _release;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _drainBudget;
    private readonly CancellationTokenSource _stopRequested = new();
    private readonly TaskCompletionSource<CollectorActivationTerminalResult> _terminal = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private Task<CollectorReadyOutcome>? _readyPublication;
    private CollectorActivationStopIntent? _winningIntent;
    private DateTimeOffset _deadline;
    private int _released;
    private int _stopIntentSubmitted;

    internal CollectorActivationLifetime(
        ICollectorActivationLifetimeDriver driver,
        Action fence,
        Action release,
        TimeProvider timeProvider,
        TimeSpan drainBudget)
    {
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));
        _fence = fence ?? throw new ArgumentNullException(nameof(fence));
        _release = release ?? throw new ArgumentNullException(nameof(release));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        if (drainBudget <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(drainBudget));
        _drainBudget = drainBudget;
    }

    public CancellationToken StopRequested => _stopRequested.Token;
    public Task<CollectorActivationTerminalResult> Terminal => _terminal.Task;
    internal bool HasStopIntent => Volatile.Read(ref _stopIntentSubmitted) != 0;

    public ValueTask<CollectorReadyOutcome> PublishReadyAsync(
        CollectorReadyPublication publication,
        CancellationToken waitCancellation = default)
    {
        ArgumentNullException.ThrowIfNull(publication);
        Task<CollectorReadyOutcome>? ready;
        TaskCompletionSource<CollectorReadyOutcome>? owner = null;
        lock (_gate)
        {
            if (_winningIntent is not null)
                return ValueTask.FromResult(CollectorReadyOutcome.Stopping);
            if (_readyPublication is null)
            {
                owner = new TaskCompletionSource<CollectorReadyOutcome>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _readyPublication = owner.Task;
            }
            ready = _readyPublication;
        }

        if (owner is not null)
            _ = CompleteReadyPublicationAsync(publication, owner);
        return waitCancellation.CanBeCanceled
            ? new ValueTask<CollectorReadyOutcome>(ready.WaitAsync(waitCancellation))
            : new ValueTask<CollectorReadyOutcome>(ready);
    }

    public ValueTask<CollectorActivationTerminalResult> RequestStopAsync(
        CollectorActivationStopIntent intent,
        CancellationToken waitCancellation = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        var ownsTransaction = false;
        lock (_gate)
        {
            if (_winningIntent is null)
            {
                _winningIntent = intent;
                _deadline = _timeProvider.GetUtcNow() + _drainBudget;
                Volatile.Write(ref _stopIntentSubmitted, 1);
                ownsTransaction = true;
            }
        }

        if (ownsTransaction)
            _ = CompleteStopTransactionAsync();
        return waitCancellation.CanBeCanceled
            ? new ValueTask<CollectorActivationTerminalResult>(Terminal.WaitAsync(waitCancellation))
            : new ValueTask<CollectorActivationTerminalResult>(Terminal);
    }

    private static async Task CompleteReadyPublicationAsync(
        CollectorReadyPublication publication,
        TaskCompletionSource<CollectorReadyOutcome> completion)
    {
        try
        {
            await publication.PublishAsync().ConfigureAwait(false);
            completion.SetResult(CollectorReadyOutcome.Published);
        }
        catch (Exception exception)
        {
            completion.SetException(exception);
        }
    }

    private async Task CompleteStopTransactionAsync()
    {
        var intent = _winningIntent!;
        var deadline = _deadline;
        var ready = _readyPublication;
        if (ready is not null)
        {
            try
            {
                await ready.ConfigureAwait(false);
            }
            catch
            {
                // Ready publication reports its own protocol error. Stop still owns fencing and release.
            }
        }

        Observe(Task.Run(async () => await _stopRequested.CancelAsync().ConfigureAwait(false)));
        var remaining = deadline - _timeProvider.GetUtcNow();
        using var deadlineCancellation = new CancellationTokenSource(
            remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero,
            _timeProvider);
        var deadlineTask = Task.Delay(
            remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero,
            _timeProvider);

        CollectorActivationDriverStopResult? driverResult = null;
        Exception? stopError = null;
        var attempts = 0;
        var deadlineExceeded = false;
        while (attempts < _driver.CooperativeStopAttempts &&
               (attempts == 0 || !deadlineCancellation.IsCancellationRequested))
        {
            attempts++;
            var attempt = InvokeStopAsync(intent, deadline, deadlineCancellation.Token);
            if (!ReferenceEquals(await Task.WhenAny(attempt, deadlineTask).ConfigureAwait(false), attempt))
            {
                deadlineExceeded = true;
                Observe(attempt);
                break;
            }
            try
            {
                driverResult = await attempt.ConfigureAwait(false);
                stopError = null;
                break;
            }
            catch (Exception exception)
            {
                stopError = exception;
            }
        }

        if (driverResult is null)
        {
            driverResult = new CollectorActivationDriverStopResult(
                deadlineExceeded || deadlineCancellation.IsCancellationRequested
                    ? DeadlineExceeded()
                    : StopFailed(stopError),
                _driver.ProjectFailedStop(
                    deadlineExceeded || deadlineCancellation.IsCancellationRequested
                        ? CollectorDrainReason.DeadlineExceeded
                        : CollectorDrainReason.StopFailed));
        }

        Exception? releaseError = null;
        var fenced = false;
        try
        {
            if (driverResult.DrainOutcome.Reason is CollectorDrainReason.DeadlineExceeded or CollectorDrainReason.StopFailed)
                _driver.FenceAfterDeadline();
            _fence();
            fenced = true;
        }
        catch (Exception exception)
        {
            releaseError = exception;
        }
        if (fenced)
        {
            try
            {
                if (Interlocked.Exchange(ref _released, 1) == 0)
                    _release();
            }
            catch (Exception exception)
            {
                releaseError = exception;
            }
        }

        _terminal.TrySetResult(new CollectorActivationTerminalResult(
            intent,
            deadline,
            driverResult.DrainOutcome,
            attempts,
            stopError,
            Volatile.Read(ref _released) != 0 && releaseError is null,
            releaseError,
            driverResult.Execution));
    }

    private Task<CollectorActivationDriverStopResult> InvokeStopAsync(
        CollectorActivationStopIntent intent,
        DateTimeOffset deadline,
        CancellationToken cancellationToken) => Task.Factory.StartNew(
        async () => await _driver.StopAsync(intent, deadline, cancellationToken).ConfigureAwait(false),
        CancellationToken.None,
        TaskCreationOptions.LongRunning,
        TaskScheduler.Default).Unwrap();

    private static CollectorActivationDrainOutcome DeadlineExceeded() => new(
        null,
        null,
        CollectorDrainReason.DeadlineExceeded,
        RemainderDurable: false,
        CollectorDrainCompletionReason.DeadlineExceeded);

    private static CollectorActivationDrainOutcome StopFailed(Exception? error) => new(
        null,
        null,
        CollectorDrainReason.StopFailed,
        RemainderDurable: false,
        CollectorDrainCompletionReason.CompletionFailed,
        error?.Message);

    private static void Observe(Task task) => _ = task.ContinueWith(
        static completed => _ = completed.Exception,
        CancellationToken.None,
        TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
        TaskScheduler.Default);
}
