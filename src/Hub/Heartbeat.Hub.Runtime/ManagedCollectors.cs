using System.Text.Json;
using Heartbeat.Management;

namespace Heartbeat.Hub.Runtime;

public interface ICollectorFactory
{
    CollectorType Type { get; }
    IManagedCollector Create(string target);
}

public interface IManagedCollector : IAsyncDisposable
{
    CollectorState State { get; }
    // Return only public, persistable settings. Credentials belong to the implementation's secret store.
    Task<JsonElement> ConfigureAsync(JsonElement configuration, CancellationToken cancellationToken);
    Task StartAsync(CancellationToken cancellationToken);
    Task PauseAsync(CancellationToken cancellationToken);
    Task RemoveAsync(CancellationToken cancellationToken);
}

public sealed class CollectorManager(HubLocalStorage storage, IEnumerable<ICollectorFactory> factories) : ICollectorManager, IAsyncDisposable
{
    private readonly Dictionary<string, ICollectorFactory> _factories = factories.ToDictionary(x => x.Type.Key, StringComparer.Ordinal);
    private readonly Dictionary<(string Key, string Target), Entry> _entries = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _snapshotLock = new();
    public IReadOnlyList<CollectorType> Types => _factories.Values.Select(x => x.Type).ToArray();
    public IReadOnlyList<CollectorState> Collectors
    {
        get { lock (_snapshotLock) return _entries.Values.Select(x => x.Collector.State with {
            Configuration = x.Configuration, Error = x.RestoreError ?? x.Collector.State.Error }).ToArray(); }
    }

    public async Task RestoreAsync(CancellationToken token)
    {
        var saved = storage.ReadDocument<SavedCollector[]>("collectors");
        if (saved is null) return;
        await _gate.WaitAsync(token);
        try
        {
            foreach (var item in saved)
            {
                try
                {
                    await ExecuteCoreAsync(new("configure", item.Key, item.Target, item.Configuration), token, save: false);
                    if (item.Enabled) await ExecuteCoreAsync(new("start", item.Key, item.Target), token, save: false);
                }
                catch (Exception) when (!token.IsCancellationRequested)
                {
                    if (_entries.TryGetValue((item.Key, item.Target), out var entry))
                        entry.RestoreError = "无法恢复采集，请检查配置或重新认证。";
                }
            }
        }
        finally { _gate.Release(); }
    }

    public async Task ExecuteAsync(CollectorOperation operation, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(operation.Target) || operation.Target.Length > 255)
            throw new ArgumentException("A stable observation target is required.");
        await _gate.WaitAsync(cancellationToken);
        try { await ExecuteCoreAsync(operation, cancellationToken); }
        finally { _gate.Release(); }
    }

    private async Task ExecuteCoreAsync(CollectorOperation operation, CancellationToken token, bool save = true)
    {
        var key = (operation.Key, operation.Target!);
        var entry = GetEntry(operation, key);
        switch (operation.Action)
        {
            case "configure":
                if (operation.Configuration is not { ValueKind: JsonValueKind.Object } config)
                    throw new ArgumentException("Collector configuration must be an object.");
                await entry.Collector.PauseAsync(token);
                entry.Enabled = false;
                if (save) Save(token);
                entry.Configuration = await entry.Collector.ConfigureAsync(config, token);
                break;
            case "start":
                await entry.Collector.StartAsync(token);
                entry.Enabled = true;
                break;
            case "pause":
                await entry.Collector.PauseAsync(token);
                entry.Enabled = false;
                break;
            case "remove":
                await entry.Collector.RemoveAsync(token);
                await entry.Collector.DisposeAsync();
                lock (_snapshotLock) _entries.Remove(key);
                break;
            default: throw new ArgumentException("Unknown Collector operation.");
        }
        entry.RestoreError = null;
        if (save) Save(token);
    }

    private void Save(CancellationToken token)
    {
        var saved = _entries.Select(x => new SavedCollector(x.Key.Key, x.Key.Target, x.Value.Configuration, x.Value.Enabled)).ToArray();
        token.ThrowIfCancellationRequested();
        storage.WriteDocument("collectors", saved);
    }

    private Entry GetEntry(CollectorOperation operation, (string Key, string Target) key)
    {
        if (!_entries.TryGetValue(key, out var entry))
        {
            if (operation.Action != "configure" || !_factories.TryGetValue(operation.Key, out var factory))
                throw new ArgumentException("Collector is not installed or configured on this Hub.");
            entry = new(factory.Create(operation.Target!));
            lock (_snapshotLock) _entries.Add(key, entry);
        }
        return entry;
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try { foreach (var entry in _entries.Values) await entry.Collector.DisposeAsync(); }
        finally { _gate.Release(); }
    }

    private sealed class Entry(IManagedCollector collector)
    {
        public IManagedCollector Collector { get; } = collector;
        public JsonElement Configuration = JsonSerializer.SerializeToElement(new { });
        public bool Enabled;
        public string? RestoreError;
    }
    private sealed record SavedCollector(string Key, string Target, JsonElement Configuration, bool Enabled);
}
