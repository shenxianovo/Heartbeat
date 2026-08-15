namespace Heartbeat.Collector.System.Input;

/// <summary>
/// Whether the local-only click signal currently has an effective consumer.
/// This is independent from durable InputEvent Recording.
/// </summary>
public interface IInteractionSignalPolicy
{
    bool Enabled { get; }
    event Action<bool>? Changed;
}
