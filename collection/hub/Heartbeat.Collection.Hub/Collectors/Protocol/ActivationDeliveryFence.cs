namespace Heartbeat.Collection.Hub.Collectors.Protocol;

internal sealed class ActivationDeliveryFence : Runtime.ICollectorProjectionCommitFence
{
    private readonly object _gate = new();
    private bool _fenced;

    public bool IsFenced => Volatile.Read(ref _fenced);

    public void Fence()
    {
        lock (_gate)
            _fenced = true;
    }

    public bool TryPublishFile(string preparedPath, string authoritativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(preparedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(authoritativePath);
        lock (_gate)
        {
            if (_fenced)
                return false;
            File.Move(preparedPath, authoritativePath, overwrite: true);
            return true;
        }
    }

    internal bool TryCommitHost(Action commit)
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
