namespace Heartbeat.Management;

// Counts Record snapshots, including retries/extensions, in Hub-time one-second buckets.
public sealed record DeliveryActivityBucket(long Second, long Received, long Sent, long Confirmed);
public sealed record DeliveryActivitySnapshot(Guid Epoch, long CapturedAt, IReadOnlyList<DeliveryActivityBucket> Buckets)
{
    public const int WindowSeconds = 60;
    public const int MaximumBodyBytes = 16_384;
}
public sealed record HubActivityReport(Guid SessionId, DeliveryActivitySnapshot Activity);
