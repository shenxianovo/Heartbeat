namespace Heartbeat.Collection.Hub.Http;

public sealed record ClientCompatibilitySnapshot(
    bool UpdateRequired,
    string? Message = null);

/// <summary>全局 strict-protocol 状态；presence、icon 与 Upload Stream 共用。</summary>
public interface IClientCompatibilityStatus
{
    ClientCompatibilitySnapshot Current { get; }
    event Action<ClientCompatibilitySnapshot>? Changed;
}

public sealed class ClientCompatibilityStatus : IClientCompatibilityStatus
{
    private readonly object _gate = new();
    private ClientCompatibilitySnapshot _current = new(false);

    public ClientCompatibilitySnapshot Current
    {
        get { lock (_gate) return _current; }
    }

    public event Action<ClientCompatibilitySnapshot>? Changed;

    public void RequireUpdate(string? message)
    {
        ClientCompatibilitySnapshot snapshot;
        lock (_gate)
        {
            if (_current.UpdateRequired) return;
            snapshot = new(true, message ?? "服务器要求更新 Heartbeat 后再继续上传。");
            _current = snapshot;
        }
        Changed?.Invoke(snapshot);
    }
}
