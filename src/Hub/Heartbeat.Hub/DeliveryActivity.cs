using Heartbeat.Management;

namespace Heartbeat.Hub;

// Fixed memory; UI/network consumers never run on the custody or upload path.
public sealed class DeliveryActivity(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Guid _epoch = Guid.NewGuid();
    private readonly DeliveryActivityBucket?[] _buckets = new DeliveryActivityBucket[DeliveryActivitySnapshot.WindowSeconds];
    private long _lastTime;
    private long? _firstSecond;

    public DeliveryActivitySnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                var now = Now();
                var second = now / 1000;
                var start = Math.Max(_firstSecond!.Value, second - _buckets.Length + 1);
                var buckets = new List<DeliveryActivityBucket>();
                for (var at = start; at <= second; at++) buckets.Add(At(at));
                return new(_epoch, now, buckets);
            }
        }
    }

    internal void Receive(int count) => Add(count, 0, 0);
    internal void Send(int count) => Add(0, count, 0);
    internal void Confirm(int count) => Add(0, 0, count);

    private void Add(int received, int sent, int confirmed)
    {
        lock (_gate)
        {
            var second = Now() / 1000;
            var bucket = At(second);
            _buckets[second % _buckets.Length] = bucket with
            {
                Received = bucket.Received + received,
                Sent = bucket.Sent + sent,
                Confirmed = bucket.Confirmed + confirmed,
            };
        }
    }

    private long Now()
    {
        // A backwards wall-clock adjustment must not reorder already observed events.
        _lastTime = Math.Max(_lastTime, _clock.GetUtcNow().ToUnixTimeMilliseconds());
        _firstSecond ??= _lastTime / 1000;
        return _lastTime;
    }

    private DeliveryActivityBucket At(long second) =>
        _buckets[second % _buckets.Length] is { } bucket && bucket.Second == second
            ? bucket : new(second, 0, 0, 0);
}
