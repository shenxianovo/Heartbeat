using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Heartbeat.Desktop.UI.Presentation;

namespace Heartbeat.Desktop.UI.ViewModels;

/// <summary>
/// Desktop 采集器页的 System BuiltIn 卡片。通用 ExternalHost / Instance UI 落地前，
/// presentation 不从历史 source 级 Collector Registry 猜测外部卡片。
/// </summary>
public partial class CollectorItemViewModel : ObservableObject
{
    private static readonly SystemCapability[] SystemCapabilityOrder =
    [
        SystemCapability.ForegroundApp,
        SystemCapability.WindowActivity,
        SystemCapability.InteractionSignal,
        SystemCapability.InputEventRecording,
    ];

    private readonly Action<SystemCapability, bool>? _setSystemCapabilityEnabled;
    private readonly Action<SystemCapability>? _recoverSystemCapability;
    private readonly Action<SystemCapability>? _revealSystemCapabilityApplication;
    public CollectorItemViewModel(
        string source,
        Action<SystemCapability, bool> setSystemCapabilityEnabled,
        Action<SystemCapability> recoverSystemCapability,
        Action<SystemCapability> revealSystemCapabilityApplication)
    {
        Source = source;
        _setSystemCapabilityEnabled = setSystemCapabilityEnabled;
        _recoverSystemCapability = recoverSystemCapability;
        _revealSystemCapabilityApplication = revealSystemCapabilityApplication;
    }

    public string Source { get; }
    public ObservableCollection<SystemCapabilityItemViewModel> Capabilities { get; } = [];
    public bool HasCapabilities => Capabilities.Count > 0;

    [ObservableProperty]
    private bool _isExpanded;

    public bool IsCollapsed => !IsExpanded;

    public string Summary
    {
        get
        {
            var actionable = Capabilities.FirstOrDefault(item =>
                item.RequestedEnabled && item.Availability is
                    CapabilityAvailability.PermissionRequired or
                    CapabilityAvailability.Unavailable or
                    CapabilityAvailability.Paused);
            if (actionable != null)
                return $"{actionable.Name}{actionable.StatusText}";

            var enabled = Capabilities.Count(item => item.HasToggle && item.RequestedEnabled);
            return $"System BuiltIn · {enabled} 项可选能力已启用";
        }
    }

    public void SetSystemCapabilities(DesktopCapabilitySnapshot snapshot)
    {
        foreach (var capability in SystemCapabilityOrder)
        {
            var item = Capabilities.FirstOrDefault(existing => existing.Id == capability);
            if (item == null)
            {
                item = new SystemCapabilityItemViewModel(
                    capability,
                    _setSystemCapabilityEnabled,
                    _recoverSystemCapability,
                    _revealSystemCapabilityApplication);
                Capabilities.Add(item);
            }
            item.Update(snapshot.Get(capability));
        }

        OnPropertyChanged(nameof(HasCapabilities));
        OnPropertyChanged(nameof(Summary));
    }

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(IsCollapsed));
}
