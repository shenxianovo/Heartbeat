namespace Heartbeat.Collection.CollectorProtocol;

internal enum CollectorDeliveryCommitOutcome
{
    Committed,
    Superseded,
    Fenced
}

internal enum CollectorDeliveryStepResult
{
    Progressed,
    Superseded,
    Fenced,
    PersistenceFailed
}

internal enum CollectorAdmissionOutcome
{
    Committed,
    Superseded,
    Closed
}

internal sealed class CollectorAdmissionClosedException()
    : InvalidOperationException("Collector Protocol admission is closed after drain ownership transfer.");

internal sealed class CollectorDeliveryOwnership(Func<Action, bool>? commitBoundary = null)
{
    private readonly object _gate = new();
    private readonly Func<Action, bool> _commitBoundary = commitBoundary ?? Commit;
    private CollectorDeliveryLease? _background;
    private CollectorDrainTransition? _drain;
    private int _epoch;
    private bool _ordinaryAdmissionOpen = true;
    private bool _fenced;

    public CollectorDeliveryLease BeginBackground()
    {
        lock (_gate)
            return _background ??= new CollectorDeliveryLease(this, _epoch);
    }

    public CollectorAdmissionLease BeginOrdinaryAdmission()
    {
        lock (_gate)
        {
            if (!_ordinaryAdmissionOpen)
                throw new CollectorAdmissionClosedException();
            return new CollectorAdmissionLease(this, _epoch, null);
        }
    }

    public CollectorDrainTransition BeginDrain(CollectorDrainRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            if (_drain is not null)
                return _drain;
            _ordinaryAdmissionOpen = false;
            _epoch++;
            var delivery = new CollectorDeliveryLease(this, _epoch);
            _drain = new CollectorDrainTransition(this, request, delivery);
            return _drain;
        }
    }

    internal CollectorDeliveryCommitOutcome TryCommit(
        CollectorDeliveryLease lease,
        Action commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        lock (_gate)
        {
            if (lease.Epoch != _epoch)
                return CollectorDeliveryCommitOutcome.Superseded;
            if (_fenced || !_commitBoundary(commit))
                return CollectorDeliveryCommitOutcome.Fenced;
            return CollectorDeliveryCommitOutcome.Committed;
        }
    }

    internal CollectorDeliveryCommitOutcome Check(CollectorDeliveryLease lease)
    {
        lock (_gate)
        {
            if (lease.Epoch != _epoch)
                return CollectorDeliveryCommitOutcome.Superseded;
            return _fenced
                ? CollectorDeliveryCommitOutcome.Fenced
                : CollectorDeliveryCommitOutcome.Committed;
        }
    }

    internal CollectorAdmissionLease BeginTailAdmission(CollectorDrainTransition transition)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_drain, transition) || _fenced || transition.IsTailSealed)
                throw new CollectorAdmissionClosedException();
            return new CollectorAdmissionLease(this, _epoch, transition);
        }
    }

    internal CollectorAdmissionOutcome TryCommitAdmission(
        CollectorAdmissionLease admission,
        Func<bool> commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        lock (_gate)
        {
            if (admission.Epoch != _epoch)
                return CollectorAdmissionOutcome.Superseded;
            if (admission.Transition is { } transition &&
                (!ReferenceEquals(_drain, transition) || transition.IsTailSealed))
                return CollectorAdmissionOutcome.Closed;
            if (_fenced || !commit())
                return CollectorAdmissionOutcome.Closed;
            return CollectorAdmissionOutcome.Committed;
        }
    }

    internal void Fence(CollectorDrainTransition transition)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_drain, transition) || _fenced)
                return;
            transition.SealTailAdmissionLocked();
            _fenced = true;
        }
    }

    internal void SealTailAdmission(CollectorDrainTransition transition)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_drain, transition))
                transition.SealTailAdmissionLocked();
        }
    }

    private static bool Commit(Action commit)
    {
        commit();
        return true;
    }
}

internal sealed class CollectorDeliveryLease(
    CollectorDeliveryOwnership owner,
    int epoch)
{
    internal int Epoch { get; } = epoch;

    public CollectorDeliveryCommitOutcome TryCommit(Action commit) =>
        owner.TryCommit(this, commit);

    public CollectorDeliveryCommitOutcome Check() => owner.Check(this);
}

internal sealed class CollectorAdmissionLease(
    CollectorDeliveryOwnership owner,
    int epoch,
    CollectorDrainTransition? transition)
{
    internal int Epoch { get; } = epoch;
    internal CollectorDrainTransition? Transition { get; } = transition;

    public CollectorAdmissionOutcome TryCommit(Action commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        return owner.TryCommitAdmission(this, () =>
        {
            commit();
            return true;
        });
    }

    public CollectorAdmissionOutcome TryCommit(Func<bool> commit) =>
        owner.TryCommitAdmission(this, commit);
}

internal sealed class CollectorDrainTransition(
    CollectorDeliveryOwnership owner,
    CollectorDrainRequest request,
    CollectorDeliveryLease delivery)
{
    public DateTimeOffset Deadline { get; } = request.Deadline;
    public CollectorDeliveryLease Delivery { get; } = delivery;
    internal bool IsTailSealed { get; private set; }

    public CollectorAdmissionLease BeginTailAdmission() => owner.BeginTailAdmission(this);

    public void SealTailAdmission() => owner.SealTailAdmission(this);

    public void Fence() => owner.Fence(this);

    internal void SealTailAdmissionLocked() => IsTailSealed = true;
}
