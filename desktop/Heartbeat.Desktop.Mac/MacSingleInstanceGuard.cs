namespace Heartbeat.Desktop.Mac;

internal sealed class MacSingleInstanceGuard : IDisposable
{
    private Mutex? _mutex;

    public MacSingleInstanceGuard()
    {
        _mutex = new Mutex(true, "com.shenxianovo.heartbeat.agent", out var created);
        IsFirstInstance = created;
        if (!created)
        {
            _mutex.Dispose();
            _mutex = null;
        }
    }

    public bool IsFirstInstance { get; }

    public void Dispose()
    {
        if (_mutex == null) return;
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex.Dispose();
        _mutex = null;
    }
}
