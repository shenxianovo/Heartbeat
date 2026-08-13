using Heartbeat.Desktop.UI.Presentation;

namespace Heartbeat.Desktop.UI.ViewModels;

public sealed record CapabilityItemViewModel(
    string Name,
    string Description,
    CapabilityAvailability Availability)
{
    public bool IsAvailable => Availability == CapabilityAvailability.Available;
    public bool IsPermissionRequired => Availability == CapabilityAvailability.PermissionRequired;
    public bool IsUnavailable => Availability == CapabilityAvailability.Unavailable;

    public string AvailabilityText => Availability switch
    {
        CapabilityAvailability.Available => "可用",
        CapabilityAvailability.PermissionRequired => "需要权限",
        _ => "不可用"
    };
}
