namespace Heartbeat.Collection.CollectorProtocol;

internal sealed class CollectorDeliveryCommitFence(
    Action? beforeCommit = null,
    Func<Action, bool>? commitBoundary = null)
{
    private readonly object _gate = new();
    private int _epoch;

    public int CaptureEpoch() => Volatile.Read(ref _epoch);

    public void Fence()
    {
        lock (_gate)
            _epoch++;
    }

    public bool TryCommit(int expectedEpoch, Action commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        beforeCommit?.Invoke();
        lock (_gate)
        {
            if (_epoch != expectedEpoch)
                return false;
            if (commitBoundary is not null)
                return commitBoundary(commit);
            commit();
            return true;
        }
    }
}
