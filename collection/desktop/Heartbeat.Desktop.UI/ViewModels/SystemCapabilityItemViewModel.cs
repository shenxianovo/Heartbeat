using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heartbeat.Desktop.UI.Presentation;

namespace Heartbeat.Desktop.UI.ViewModels;

public partial class SystemCapabilityItemViewModel : ObservableObject
{
    private readonly Action<SystemCapability, bool>? _setEnabled;
    private readonly Action<SystemCapability>? _recover;
    private readonly Action<SystemCapability>? _revealApplication;
    private bool _suppressRequestedEnabled;

    public SystemCapabilityItemViewModel(
        SystemCapability id,
        Action<SystemCapability, bool>? setEnabled,
        Action<SystemCapability>? recover,
        Action<SystemCapability>? revealApplication)
    {
        Id = id;
        _setEnabled = setEnabled;
        _recover = recover;
        _revealApplication = revealApplication;
    }

    public SystemCapability Id { get; }
    public string Name => Id switch
    {
        SystemCapability.ForegroundApp => "前台应用",
        SystemCapability.WindowActivity => "窗口活动",
        SystemCapability.InteractionSignal => "点击辅助判断",
        SystemCapability.InputEventRecording => "输入事件记录",
        _ => Id.ToString(),
    };

    public string Description => Id switch
    {
        SystemCapability.ForegroundApp => "记录当前前台 App 与离开状态",
        SystemCapability.WindowActivity => "记录聚焦窗口切换与原始标题",
        SystemCapability.InteractionSignal => "无专用采集器时过滤标题噪声；不保存、不上传",
        SystemCapability.InputEventRecording => "保存并上传物理按键与鼠标统计",
        _ => string.Empty,
    };

    public bool HasToggle { get; private set; }
    public CapabilityAvailability Availability { get; private set; }
    public bool ShowRecoveryAction { get; private set; }
    public bool ShowApplicationLocationAction { get; private set; }
    public bool HasPermissionHelp => Availability == CapabilityAvailability.PermissionRequired;
    public string PermissionHelpText =>
        "“去授权”会先定位当前运行的 Heartbeat，再打开系统设置。若列表里没有它，请点左下角“+”，或从访达拖入列表。";
    public bool IsEffective => (!HasToggle || RequestedEnabled) && Availability == CapabilityAvailability.Available;
    public bool IsIneffective => !IsEffective;
    public string StatusText => !HasToggle
        ? AvailabilityText()
        : !RequestedEnabled
            ? "已关闭"
            : AvailabilityText();

    [ObservableProperty]
    private bool _requestedEnabled;

    public void Update(SystemCapabilityState state)
    {
        HasToggle = state.RequestedEnabled.HasValue;
        _suppressRequestedEnabled = true;
        RequestedEnabled = state.RequestedEnabled ?? true;
        _suppressRequestedEnabled = false;
        Availability = state.Availability;
        ShowRecoveryAction = state.RecoveryActionAvailable;
        ShowApplicationLocationAction = state.ApplicationLocationActionAvailable;
        OnPropertyChanged(nameof(HasToggle));
        OnPropertyChanged(nameof(Availability));
        OnPropertyChanged(nameof(ShowRecoveryAction));
        OnPropertyChanged(nameof(ShowApplicationLocationAction));
        OnPropertyChanged(nameof(HasPermissionHelp));
        OnPropertyChanged(nameof(PermissionHelpText));
        OnPropertyChanged(nameof(IsEffective));
        OnPropertyChanged(nameof(IsIneffective));
        OnPropertyChanged(nameof(StatusText));
    }

    partial void OnRequestedEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEffective));
        OnPropertyChanged(nameof(IsIneffective));
        OnPropertyChanged(nameof(StatusText));
        if (!_suppressRequestedEnabled && HasToggle)
            _setEnabled?.Invoke(Id, value);
    }

    [RelayCommand]
    private void Recover()
    {
        if (ShowRecoveryAction)
            _recover?.Invoke(Id);
    }

    [RelayCommand]
    private void RevealApplication()
    {
        if (ShowApplicationLocationAction)
            _revealApplication?.Invoke(Id);
    }

    private string AvailabilityText() => Availability switch
    {
        CapabilityAvailability.Available => "采集中",
        CapabilityAvailability.PermissionRequired => "需要授权",
        CapabilityAvailability.Paused => "等待窗口活动",
        _ => "不可用",
    };
}
