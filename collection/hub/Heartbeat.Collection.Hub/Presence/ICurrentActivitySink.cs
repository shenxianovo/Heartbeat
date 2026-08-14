namespace Heartbeat.Collection.Hub.Presence;

public sealed record CurrentActivity(string AppIdentityKey, string? AppDisplayName);

/// <summary>Current Activity 的写入口；hub 是唯一生产 adapter。</summary>
public interface ICurrentActivitySink
{
    void Report(CurrentActivity? activity);
}
