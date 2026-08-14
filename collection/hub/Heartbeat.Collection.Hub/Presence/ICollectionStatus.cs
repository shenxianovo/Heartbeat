namespace Heartbeat.Collection.Hub.Presence;

/// <summary>hub 维护的 Current Activity 与 Collector Active 读表面。</summary>
public interface ICollectionStatus
{
    CurrentActivity? CurrentActivity { get; }
    event Action<CurrentActivity?>? CurrentActivityChanged;
    IReadOnlyDictionary<string, DateTimeOffset> SourceLastSeen { get; }
}
