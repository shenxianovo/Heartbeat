namespace Heartbeat.Collector.System.Collection;

/// <summary>
/// Keeps one ordered handoff active until every item deferred before or during replay has crossed
/// the replay seam. Callers do their potentially blocking work outside this module's short lock.
/// </summary>
internal sealed class OrderedDeferredHandoff<T>
{
    private readonly object _gate = new();
    private readonly Queue<T> _deferred = [];
    private bool _active;

    public bool TryBegin()
    {
        lock (_gate)
        {
            if (_active)
                return false;
            _active = true;
            return true;
        }
    }

    public bool TryDefer(Func<T> itemFactory)
    {
        lock (_gate)
        {
            if (!_active)
                return false;
            _deferred.Enqueue(itemFactory());
            return true;
        }
    }

    public void Complete(Action<T> replay)
    {
        List<Exception>? failures = null;
        while (true)
        {
            T item;
            lock (_gate)
            {
                if (_deferred.Count == 0)
                {
                    _active = false;
                    break;
                }
                item = _deferred.Dequeue();
            }
            try
            {
                replay(item);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (failures is [var failure])
            global::System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        if (failures is { Count: > 1 })
            throw new AggregateException(failures);
    }
}
