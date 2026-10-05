namespace Heartbeat.Management;

// Process-lifetime snapshot counts; later submissions of the same Record count again.
public sealed record DeliveryActivitySnapshot(Guid Epoch, long CapturedAt, long Accepted, long Delivered)
{
    public const int MaximumBodyBytes = 1024;
}
public sealed record HubActivityReport(Guid SessionId, DeliveryActivitySnapshot Activity);
