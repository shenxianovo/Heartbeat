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
    private readonly Func<ValueTask<CollectorReadyPreparedCommit>> _prepare;

    public CollectorReadyPublication(Action commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        _prepare = () => ValueTask.FromResult(new CollectorReadyPreparedCommit(() =>
        {
            commit();
            return true;
        }));
    }

    public CollectorReadyPublication(Func<ValueTask> prepare, Action commit)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        ArgumentNullException.ThrowIfNull(commit);
        _prepare = () => PrepareCommitAsync(prepare, commit);
    }

    public CollectorReadyPublication(Func<ValueTask<CollectorReadyPreparedCommit>> prepare)
    {
        _prepare = prepare ?? throw new ArgumentNullException(nameof(prepare));
    }

    internal ValueTask<CollectorReadyPreparedCommit> PrepareAsync() => _prepare();

    private static async ValueTask<CollectorReadyPreparedCommit> PrepareCommitAsync(
        Func<ValueTask> prepare,
        Action commit)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        ArgumentNullException.ThrowIfNull(commit);
        await prepare().ConfigureAwait(false);
        return new CollectorReadyPreparedCommit(() =>
        {
            commit();
            return true;
        });
    }
}

internal sealed class CollectorReadyPreparedCommit(
    Func<bool> tryCommit,
    IDisposable? resource = null) : IDisposable
{
    private readonly Func<bool> _tryCommit = tryCommit ?? throw new ArgumentNullException(nameof(tryCommit));
    private IDisposable? _resource = resource;

    internal bool TryCommit() => _tryCommit();
    public void Dispose() => Interlocked.Exchange(ref _resource, null)?.Dispose();
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

/// <summary>
/// One atomically published ManagedProcess termination fact. Termination and its cause are the same
/// value, so no reader can observe a terminated Activation whose cause is still missing.
/// </summary>
internal sealed record ManagedProcessTermination(ManagedProcessTerminationCause Cause);

internal sealed record ManagedProcessTerminationDrainProjection(
    CollectorDrainReason Reason,
    CollectorDrainCompletionReason CompletionReason);

internal static class ManagedProcessTerminationProjector
{
    /// <summary>
    /// Picks the candidate cause a failed Hub stop would write first. It never reads the Collector
    /// Protocol Client: the published termination state stays the only authority for what happened.
    /// </summary>
    internal static ManagedProcessTerminationCause FromFailedStopReason(CollectorDrainReason reason) => reason switch
    {
        CollectorDrainReason.DeadlineExceeded => ManagedProcessTerminationCause.DeadlineExceeded,
        CollectorDrainReason.StopFailed => ManagedProcessTerminationCause.StopFailed,
        _ => throw new ArgumentOutOfRangeException(nameof(reason))
    };

    internal static ManagedProcessTerminationDrainProjection Project(
        ManagedProcessTerminationCause cause) => cause switch
        {
            ManagedProcessTerminationCause.BeforeReady => new(
                CollectorDrainReason.StopFailed,
                CollectorDrainCompletionReason.CompletionFailed),
            ManagedProcessTerminationCause.DrainWriteFailed => new(
                CollectorDrainReason.FlushCancelled,
                CollectorDrainCompletionReason.CompletionFailed),
            ManagedProcessTerminationCause.DeadlineExceeded => new(
                CollectorDrainReason.DeadlineExceeded,
                CollectorDrainCompletionReason.DeadlineExceeded),
            ManagedProcessTerminationCause.ProtocolFailure => new(
                CollectorDrainReason.FlushCancelled,
                CollectorDrainCompletionReason.CompletionFailed),
            ManagedProcessTerminationCause.StartupAborted => new(
                CollectorDrainReason.StopFailed,
                CollectorDrainCompletionReason.CompletionFailed),
            ManagedProcessTerminationCause.StopFailed => new(
                CollectorDrainReason.StopFailed,
                CollectorDrainCompletionReason.CompletionFailed),
            _ => throw new ArgumentOutOfRangeException(nameof(cause))
        };
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

    void FenceAfterFailedStop(CollectorDrainReason reason) { }
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

    public void FenceAfterFailedStop(CollectorDrainReason reason)
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
    private TaskCompletionSource<CollectorReadyOutcome>? _readyPublication;
    private CollectorActivationStopIntent? _winningIntent;
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
        TaskCompletionSource<CollectorReadyOutcome>? ready;
        var ownsPreparation = false;
        lock (_gate)
        {
            if (_winningIntent is not null)
                return ValueTask.FromResult(CollectorReadyOutcome.Stopping);
            if (_readyPublication is null)
            {
                _readyPublication = new TaskCompletionSource<CollectorReadyOutcome>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                ownsPreparation = true;
            }
            ready = _readyPublication;
        }

        if (ownsPreparation)
            _ = CompleteReadyPublicationAsync(publication, ready);
        return waitCancellation.CanBeCanceled
            ? new ValueTask<CollectorReadyOutcome>(ready.Task.WaitAsync(waitCancellation))
            : new ValueTask<CollectorReadyOutcome>(ready.Task);
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
                Volatile.Write(ref _stopIntentSubmitted, 1);
                ownsTransaction = true;
            }
        }

        if (ownsTransaction)
        {
            Observe(_stopRequested.CancelAsync());
            _ = CompleteStopTransactionAsync(intent);
        }
        return waitCancellation.CanBeCanceled
            ? new ValueTask<CollectorActivationTerminalResult>(Terminal.WaitAsync(waitCancellation))
            : new ValueTask<CollectorActivationTerminalResult>(Terminal);
    }

    private async Task CompleteReadyPublicationAsync(
        CollectorReadyPublication publication,
        TaskCompletionSource<CollectorReadyOutcome> completion)
    {
        var readyLinearized = false;
        try
        {
            while (true)
            {
                CollectorReadyOutcome? outcome = null;
                using (var prepared = await publication.PrepareAsync().ConfigureAwait(false))
                {
                    lock (_gate)
                    {
                        if (_winningIntent is not null)
                            outcome = CollectorReadyOutcome.Stopping;
                        else if (prepared.TryCommit())
                        {
                            readyLinearized = true;
                            outcome = CollectorReadyOutcome.Published;
                        }
                    }
                }
                if (outcome is not null)
                {
                    completion.TrySetResult(outcome.Value);
                    return;
                }
            }
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                if (!readyLinearized && _winningIntent is not null)
                    completion.TrySetResult(CollectorReadyOutcome.Stopping);
                else
                    completion.TrySetException(exception);
            }
        }
    }

    private async Task CompleteStopTransactionAsync(CollectorActivationStopIntent intent)
    {
        try
        {
            await CompleteStopTransactionCoreAsync(intent).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _terminal.TrySetException(exception);
        }
    }

    private async Task CompleteStopTransactionCoreAsync(CollectorActivationStopIntent intent)
    {
        var deadline = _timeProvider.GetUtcNow() + _drainBudget;
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
               !IsDeadlineReached(deadline, deadlineCancellation.Token))
        {
            attempts++;
            var attempt = InvokeStopAsync(intent, deadline, deadlineCancellation.Token);
            var completed = await Task.WhenAny(attempt, deadlineTask).ConfigureAwait(false);
            if (!ReferenceEquals(completed, attempt) ||
                IsDeadlineReached(deadline, deadlineCancellation.Token))
            {
                deadlineExceeded = true;
                Observe(attempt);
                break;
            }
            try
            {
                var candidate = await attempt.ConfigureAwait(false);
                if (IsDeadlineReached(deadline, deadlineCancellation.Token))
                {
                    deadlineExceeded = true;
                    break;
                }
                driverResult = candidate;
                stopError = null;
                break;
            }
            catch (Exception exception)
            {
                stopError = exception;
            }
        }

        var drainOutcome = driverResult?.DrainOutcome ??
            (deadlineExceeded || IsDeadlineReached(deadline, deadlineCancellation.Token)
                ? DeadlineExceeded()
                : StopFailed(stopError));

        Exception? releaseError = null;
        var fenced = false;
        try
        {
            if (drainOutcome.Reason is CollectorDrainReason.DeadlineExceeded or CollectorDrainReason.StopFailed)
                _driver.FenceAfterFailedStop(drainOutcome.Reason);
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

        // The fence is the only writer of a Driver's terminal cause, so the projection runs after it
        // and can only read back the first written cause instead of overwriting it.
        var execution = driverResult?.Execution ?? _driver.ProjectFailedStop(drainOutcome.Reason);

        _terminal.TrySetResult(new CollectorActivationTerminalResult(
            intent,
            deadline,
            drainOutcome,
            attempts,
            stopError,
            Volatile.Read(ref _released) != 0 && releaseError is null,
            releaseError,
            execution));
    }

    private Task<CollectorActivationDriverStopResult> InvokeStopAsync(
        CollectorActivationStopIntent intent,
        DateTimeOffset deadline,
        CancellationToken cancellationToken) => Task.Factory.StartNew(
        async () => await _driver.StopAsync(intent, deadline, cancellationToken).ConfigureAwait(false),
        CancellationToken.None,
        TaskCreationOptions.LongRunning,
        TaskScheduler.Default).Unwrap();

    private bool IsDeadlineReached(DateTimeOffset deadline, CancellationToken deadlineCancellation) =>
        deadlineCancellation.IsCancellationRequested || _timeProvider.GetUtcNow() >= deadline;

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
