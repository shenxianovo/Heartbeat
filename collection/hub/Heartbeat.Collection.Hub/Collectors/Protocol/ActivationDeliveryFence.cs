namespace Heartbeat.Collection.Hub.Collectors.Protocol;

internal sealed class ActivationDeliveryFence
{
    private readonly object _gate = new();
    private bool _fenced;

    public bool IsFenced => Volatile.Read(ref _fenced);

    public void Fence()
    {
        lock (_gate)
            _fenced = true;
    }

    public bool TryCommit(Action commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        lock (_gate)
        {
            if (_fenced)
                return false;
            commit();
            return true;
        }
    }
}
