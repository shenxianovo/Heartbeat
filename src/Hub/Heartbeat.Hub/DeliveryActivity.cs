using Heartbeat.Management;

namespace Heartbeat.Hub;

// Only counters: display history belongs to the observing UI.
public sealed class DeliveryActivity(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Guid _epoch = Guid.NewGuid();
    private long _capturedAt;
    private long _accepted;
    private long _delivered;

    public DeliveryActivitySnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                _capturedAt = Math.Max(_capturedAt, _clock.GetUtcNow().ToUnixTimeMilliseconds());
                return new(_epoch, _capturedAt, _accepted, _delivered);
            }
        }
    }

    internal void Accept(int count) { lock (_gate) _accepted += count; }
    internal void Deliver(int count) { lock (_gate) _delivered += count; }
}
