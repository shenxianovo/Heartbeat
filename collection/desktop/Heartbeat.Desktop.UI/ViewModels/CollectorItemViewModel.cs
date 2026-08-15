using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Heartbeat.Desktop.UI.Presentation;

namespace Heartbeat.Desktop.UI.ViewModels;

public partial class CollectorItemViewModel : ObservableObject
{
    private static readonly SystemCapability[] SystemCapabilityOrder =
    [
        SystemCapability.ForegroundApp,
        SystemCapability.WindowActivity,
        SystemCapability.InteractionSignal,
        SystemCapability.InputEventRecording,
    ];

    private readonly Action<string, bool>? _setEnabled;
    private readonly Action<SystemCapability, bool>? _setSystemCapabilityEnabled;
    private readonly Action<SystemCapability>? _recoverSystemCapability;
    private readonly Action<SystemCapability>? _revealSystemCapabilityApplication;
    private bool _suppressEnabled;

    public CollectorItemViewModel(
        string source,
        bool isSystem,
        Action<string, bool>? setEnabled,
        Action<SystemCapability, bool>? setSystemCapabilityEnabled = null,
        Action<SystemCapability>? recoverSystemCapability = null,
        Action<SystemCapability>? revealSystemCapabilityApplication = null)
    {
        Source = source;
        IsSystem = isSystem;
        _setEnabled = setEnabled;
        _setSystemCapabilityEnabled = setSystemCapabilityEnabled;
        _recoverSystemCapability = recoverSystemCapability;
        _revealSystemCapabilityApplication = revealSystemCapabilityApplication;
    }

    public string Source { get; }
    public bool IsSystem { get; }
    public bool IsExternal => !IsSystem;
    public bool CanToggle => !IsSystem;
    public ObservableCollection<SystemCapabilityItemViewModel> Capabilities { get; } = [];
    public bool HasCapabilities => Capabilities.Count > 0;

    [ObservableProperty]
    private bool _isExpanded;

    public bool IsCollapsed => !IsExpanded;

    [ObservableProperty]
    private bool _isActive;

    public string ActivityText => IsActive ? "活跃" : "不活跃";
    public bool IsInactive => !IsActive;

    [ObservableProperty]
    private bool _enabled = true;

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
            return $"基础采集运行中 · {enabled} 项可选能力已启用";
        }
    }

    public string Description => Source switch
    {
        "system" => "内置系统采集器，不可停用",
        "browser" => "浏览器采集器，采集标签页活动",
        _ => "外部采集器，经 loopback 汇入",
    };

    public void SetEnabledSilently(bool value)
    {
        _suppressEnabled = true;
        Enabled = value;
        _suppressEnabled = false;
    }

    public void SetSystemCapabilities(DesktopCapabilitySnapshot snapshot)
    {
        if (!IsSystem) return;

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

    partial void OnEnabledChanged(bool value)
    {
        if (!_suppressEnabled)
            _setEnabled?.Invoke(Source, value);
    }

    partial void OnIsActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(ActivityText));
        OnPropertyChanged(nameof(IsInactive));
    }

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(IsCollapsed));
}
