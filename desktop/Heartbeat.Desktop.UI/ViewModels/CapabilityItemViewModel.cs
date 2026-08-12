using Heartbeat.Desktop.UI.Presentation;

namespace Heartbeat.Desktop.UI.ViewModels;

public sealed record CapabilityItemViewModel(
    string Name,
    string Description,
    CapabilityAvailability Availability)
{
    public string AvailabilityText => Availability switch
    {
        CapabilityAvailability.Available => "可用",
        CapabilityAvailability.PermissionRequired => "需要权限",
        _ => "不可用"
    };
}
