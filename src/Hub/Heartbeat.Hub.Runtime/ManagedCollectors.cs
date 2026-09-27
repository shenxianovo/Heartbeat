using Heartbeat.Management;

namespace Heartbeat.Hub.Runtime;

public interface ICollectorFactory
{
    CollectorType Type { get; }
    ICollectorSession Create(string? target);
}

// An unbound session exists only during login. Record identity is established by authentication.
public interface ICollectorSession : IAsyncDisposable
{
    CollectorState? State { get; }
    Task<CollectorLoginResult> LoginAsync(System.Text.Json.JsonElement input, CancellationToken token);
    Task RestoreAsync(CancellationToken token);
    Task StartAsync(CancellationToken cancellationToken);
}

public sealed class CollectorManager(HubLocalStorage storage, IEnumerable<ICollectorFactory> factories) : ICollectorManager, IAsyncDisposable
{
    private readonly Dictionary<string, ICollectorFactory> _factories = factories.ToDictionary(x => x.Type.Key, StringComparer.Ordinal);
    private readonly Dictionary<(string Key, string Target), ICollectorSession> _collectors = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _snapshotLock = new();
    private PendingLogin? _pending;
    private CancellationTokenSource? _loginTimeout;
    private Task? _expiration;
    public IReadOnlyList<CollectorType> Types => _factories.Values.Select(x => x.Type).ToArray();
    public IReadOnlyList<CollectorState> Collectors
    {
        get { lock (_snapshotLock) return _collectors.Values.Select(x => x.State!).ToArray(); }
    }

    public async Task RestoreAsync(CancellationToken token)
    {
        var saved = storage.ReadDocument<SavedCollector[]>("collectors") ?? [];
        await _gate.WaitAsync(token);
        try
        {
            foreach (var item in saved)
            {
                var collector = _factories[item.Key].Create(item.Target);
                lock (_snapshotLock) _collectors.Add((item.Key, item.Target), collector);
                await collector.RestoreAsync(token);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<CollectorLoginResult> LoginAsync(CollectorLoginRequest request, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var pending = await GetLoginAsync(request, token);
            var result = await pending.Collector.LoginAsync(request.Input, token);
            if (result.Target is null) return result with { SessionId = pending.Id };
            if (request.Target is not null && result.Target != request.Target)
                throw new InvalidOperationException("登录账号与原 Collector 不一致。");
            var key = (request.Key, result.Target);
            if (_collectors.TryGetValue(key, out var previous)) await previous.DisposeAsync();
            lock (_snapshotLock) _collectors[key] = pending.Collector;
            _pending = null;
            _loginTimeout?.Cancel();
            storage.WriteDocument("collectors", _collectors.Keys.Select(x => new SavedCollector(x.Key, x.Target)).ToArray());
            await pending.Collector.StartAsync(token);
            return result;
        }
        finally { _gate.Release(); }
    }

    private async Task<PendingLogin> GetLoginAsync(CollectorLoginRequest request, CancellationToken token)
    {
        if (request.SessionId is { } id)
        {
            if (_pending is not { } pending || pending.Id != id || pending.Key != request.Key || pending.Target != request.Target)
                throw new InvalidOperationException("登录会话已失效，请重新登录。");
            return pending;
        }
        if (!_factories.TryGetValue(request.Key, out var factory)) throw new ArgumentException("Collector is not installed on this Hub.");
        if (request.Target is not null && !_collectors.ContainsKey((request.Key, request.Target)))
            throw new ArgumentException("Collector does not belong to this Hub.");
        await ClearLoginAsync();
        var created = new PendingLogin(Guid.NewGuid(), request.Key, request.Target, factory.Create(request.Target));
        _pending = created;
        _loginTimeout = new CancellationTokenSource();
        _expiration = ExpireLoginAsync(created.Id, _loginTimeout.Token);
        token.ThrowIfCancellationRequested();
        return created;
    }

    private async Task ExpireLoginAsync(Guid id, CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(5), token);
            await _gate.WaitAsync(token);
            try { if (_pending?.Id == id) await ClearLoginAsync(); }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task ClearLoginAsync()
    {
        _loginTimeout?.Cancel();
        _loginTimeout?.Dispose();
        _loginTimeout = null;
        if (_pending is { } pending) { _pending = null; await pending.Collector.DisposeAsync(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await ClearLoginAsync();
            foreach (var collector in _collectors.Values) await collector.DisposeAsync();
        }
        finally { _gate.Release(); }
        if (_expiration is not null) await _expiration;
    }

    private sealed record PendingLogin(Guid Id, string Key, string? Target, ICollectorSession Collector);
    private sealed record SavedCollector(string Key, string Target);
}
