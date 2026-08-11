namespace Heartbeat.Hub.Core.Configuration;

/// <summary>
/// Durable InputEvent Recording 的独立许可。关闭时调度器不得读取缓存或 drain 输入源。
/// </summary>
public interface IInputEventRecordingPolicy
{
    bool Enabled { get; }
    event Action<bool>? Changed;
}

public sealed class EnabledInputEventRecordingPolicy : IInputEventRecordingPolicy
{
    public bool Enabled => true;
    public event Action<bool>? Changed { add { } remove { } }
}
