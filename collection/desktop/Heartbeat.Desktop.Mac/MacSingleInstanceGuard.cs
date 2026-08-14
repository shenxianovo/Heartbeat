namespace Heartbeat.Desktop.Mac;

internal sealed class MacSingleInstanceGuard : IDisposable
{
    private const string MutexName = "com.shenxianovo.heartbeat.agent";
    private Mutex? _mutex;

    public MacSingleInstanceGuard()
    {
        var options = new NamedWaitHandleOptions
        {
            CurrentUserOnly = true,
            CurrentSessionOnly = false
        };
        _mutex = new Mutex(true, MutexName, options, out var created);
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
