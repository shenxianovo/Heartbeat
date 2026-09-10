namespace Heartbeat.Server.Entities;

/// <summary>A device's stable context for an App product, shared across profiles and windows.</summary>
public sealed class ApplicationContextRecord
{
    public long Id { get; set; }
    public required string OwnerId { get; set; }
    public long DeviceId { get; set; }
    public Device Device { get; set; } = null!;
    public long AppId { get; set; }
    public App App { get; set; } = null!;
}
