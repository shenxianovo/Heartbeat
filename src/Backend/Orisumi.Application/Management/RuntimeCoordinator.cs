using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Orisumi.Application.Management;

// Process-local delivery state. It never owns configuration or survives an API restart.
public sealed class RuntimeCoordinator(IManagementStore store, TimeProvider clock)
{
    private sealed class Connection
    {
        public int Waiting { get; set; }
        public DateTimeOffset? ReturnedAt { get; set; }
    }
    private sealed class PendingOperation(ObserverOperation value, DateTimeOffset created)
    {
        public ObserverOperation Value { get; set; } = value;
        public DateTimeOffset Created { get; } = created;
        public DateTimeOffset? Delivered { get; set; }
        public DateTimeOffset? Finished { get; set; }
    }
    private readonly object _gate = new();
    private readonly Dictionary<string, Pairing> _pairings = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, Connection> _connections = [];
    private readonly Dictionary<Guid, PendingOperation> _operations = [];
    private TaskCompletionSource _changed = NewSignal();
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static string CredentialHash(string credential) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(credential)));

    public void Notify()
    {
        lock (_gate)
        {
            var signal = _changed;
            _changed = NewSignal();
            signal.TrySetResult();
        }
    }

    public bool Connected(Guid host)
    {
        lock (_gate) return ConnectedUnderLock(host);
    }
    private bool ConnectedUnderLock(Guid host) => _connections.TryGetValue(host, out var connection)
        && (connection.Waiting > 0 || connection.ReturnedAt is { } returned && clock.GetUtcNow() - returned <= TimeSpan.FromSeconds(10));

    public async Task<Pairing> CreatePairingAsync(Guid? host, CancellationToken token)
    {
        if (host is { } id) await store.HostAsync(id, token);
        lock (_gate)
        {
            foreach (var expired in _pairings.Where(pair => pair.Value.ExpiresAt <= clock.GetUtcNow()).Select(pair => pair.Key).ToArray()) _pairings.Remove(expired);
            const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            string code;
            do { code = new string(Enumerable.Range(0, 8).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray()); }
            while (_pairings.ContainsKey(code));
            var pairing = new Pairing(code, clock.GetUtcNow().AddMinutes(10), host);
            _pairings.Add(code, pairing);
            return pairing;
        }
    }

    public async Task<HostCredential> PairAsync(string code, Guid? host, string name, string kind, CancellationToken token)
    {
        Pairing pairing;
        lock (_gate)
        {
            if (!_pairings.TryGetValue(code, out pairing!) || pairing.ExpiresAt <= clock.GetUtcNow())
                throw new ManagementException(400, "pairing_code_invalid", "The pairing code is invalid, expired, or already used.");
            if (pairing.HostId != host) throw new ManagementException(409, "pairing_mismatch", "The pairing code belongs to a different host.");
            _pairings.Remove(code);
        }
        var credential = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        try
        {
            var id = await store.PairAsync(host, name, kind, CredentialHash(credential), token);
            ResetConnection(id);
            return new(id, credential);
        }
        catch
        {
            lock (_gate) if (pairing.ExpiresAt > clock.GetUtcNow()) _pairings.TryAdd(code, pairing);
            throw;
        }
    }

    public void ResetConnection(Guid host)
    {
        lock (_gate) _connections.Remove(host);
        Notify();
    }

    public async Task<RuntimePoll> PollAsync(Guid host, string credentialHash, IReadOnlyList<InstanceRevision> known, CancellationToken token)
    {
        var revisions = known.ToDictionary(item => item.EntityId, item => item.Revision);
        var end = clock.GetUtcNow().AddSeconds(25);
        Connection connection;
        lock (_gate)
        {
            if (!_connections.TryGetValue(host, out connection!)) _connections.Add(host, connection = new());
            connection.Waiting++;
        }
        try
        {
            while (true)
            {
                Task signal;
                lock (_gate) signal = _changed.Task;
                await store.AuthenticateAsync(credentialHash, token);
                var all = await store.ConfigurationsAsync(host, token);
                var instances = all.Where(item => !revisions.TryGetValue(item.EntityId, out var revision) || revision != item.Revision).ToArray();
                List<RuntimeOperation> operations = [];
                lock (_gate)
                {
                    Prune();
                    foreach (var operation in _operations.Values.Where(item => item.Value.HostId == host && item.Value.State == "queued"))
                    {
                        operation.Delivered = clock.GetUtcNow();
                        operation.Value = operation.Value with { State = "delivered" };
                        var value = operation.Value;
                        operations.Add(new(value.OperationId, value.Action, value.EntityId, value.PackageId, value.NewInstance));
                    }
                }
                if (instances.Length > 0 || operations.Count > 0 || clock.GetUtcNow() >= end) return new(instances, operations);
                var remaining = end - clock.GetUtcNow();
                if (remaining <= TimeSpan.Zero) continue;
                try { await signal.WaitAsync(remaining, clock, token); }
                catch (TimeoutException) { }
            }
        }
        finally
        {
            lock (_gate)
            {
                connection.Waiting--;
                if (!token.IsCancellationRequested) connection.ReturnedAt = clock.GetUtcNow();
            }
        }
    }

    public async Task<ObserverOperation> CreateOperationAsync(Guid host, OperationRequest request, CancellationToken token)
    {
        var owner = await store.HostAsync(host, token);
        if (owner.Revoked) throw new ManagementException(409, "host_offline", "The host is offline.");
        if (request.EntityId is { } entity)
        {
            var instance = await store.InstanceAsync(entity, token);
            if (instance.HostId != host) throw new ManagementException(403, "host_mismatch", "The instance belongs to another host.");
            if (instance.Removed) throw new ManagementException(410, "instance_removed", "The instance was removed.");
        }
        ObserverOperation operation;
        lock (_gate)
        {
            Prune();
            if (!ConnectedUnderLock(host)) throw new ManagementException(409, "host_offline", "The host is offline.");
            operation = new(Guid.CreateVersion7(), host, request.Action, request.EntityId, request.PackageId,
                request.NewInstance, "queued", null, null);
            _operations.Add(operation.OperationId, new(operation, clock.GetUtcNow()));
        }
        Notify();
        return operation;
    }

    public ObserverOperation Operation(Guid host, Guid id)
    {
        lock (_gate)
        {
            Prune();
            return _operations.TryGetValue(id, out var operation) && operation.Value.HostId == host
                ? operation.Value : throw new ManagementException(404, null, "Operation not found.");
        }
    }

    public void Report(Guid host, Guid id, OperationResult result)
    {
        lock (_gate)
        {
            Prune();
            if (!_operations.TryGetValue(id, out var operation)) throw new ManagementException(404, null, "Operation not found.");
            if (operation.Value.HostId != host) throw new ManagementException(403, "host_mismatch", "The operation belongs to another host.");
            if (operation.Value.State != "delivered") throw new ManagementException(404, null, "The operation is no longer awaiting a result.");
            operation.Value = operation.Value with { State = result.Outcome, Error = result.Error, Result = result.Result?.Clone() };
            operation.Finished = clock.GetUtcNow();
        }
    }

    private void Prune()
    {
        var now = clock.GetUtcNow();
        foreach (var operation in _operations.Values)
        {
            var state = operation.Value.State;
            if (state == "queued" && now - operation.Created >= TimeSpan.FromSeconds(30))
            {
                operation.Value = operation.Value with { State = "expired" };
                operation.Finished = operation.Created.AddSeconds(30);
            }
            else if (state == "delivered" && operation.Delivered is { } delivered)
            {
                var timeout = TimeSpan.FromMinutes(operation.Value.Action is "install" or "update" ? 10 : 1);
                if (now - delivered >= timeout)
                {
                    operation.Value = operation.Value with { State = "unconfirmed" };
                    operation.Finished = delivered + timeout;
                }
            }
        }
        foreach (var id in _operations.Where(pair => pair.Value.Finished is { } time && now - time >= TimeSpan.FromMinutes(10)).Select(pair => pair.Key).ToArray()) _operations.Remove(id);
    }
}
