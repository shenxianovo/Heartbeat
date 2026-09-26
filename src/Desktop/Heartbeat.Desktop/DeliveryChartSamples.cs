using Heartbeat.Management;

namespace Heartbeat.Desktop;

public sealed record DeliveryChartPoint(double At, long? Accepted, long? Delivered);

// Each native view owns its samples; Hub and API retain only cumulative counters.
public sealed class DeliveryChartSamples
{
    public const int WindowSeconds = 60;
    private DeliveryActivitySnapshot? _previous;
    private Guid? _epoch;
    private readonly List<DeliveryChartPoint> _points = [];
    public IReadOnlyList<DeliveryChartPoint> Points => _points;

    public void Observe(DeliveryActivitySnapshot? current)
    {
        if (current is null) { _previous = null; return; }
        if (current.Epoch != _epoch)
        {
            _points.Clear();
            _previous = null;
            _epoch = current.Epoch;
        }
        if (_previous is { } previous && current.CapturedAt <= previous.CapturedAt) return;
        var at = current.CapturedAt / 1000d;
        _points.Add(new(at, current.Accepted - _previous?.Accepted, current.Delivered - _previous?.Delivered));
        _previous = current;
        _points.RemoveAll(point => point.At < at - WindowSeconds);
    }
}
