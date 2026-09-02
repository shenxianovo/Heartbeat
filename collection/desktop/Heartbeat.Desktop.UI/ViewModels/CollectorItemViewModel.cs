using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heartbeat.Desktop.UI.Presentation;
using Heartbeat.Core;

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
    private readonly Action<BrowserKind>? _openBrowserSetup;
    private readonly Action<string, bool>? _setBrowserAppEnabled;
    private readonly Action<string>? _copyText;
    private bool _suppressEnabled;

    public CollectorItemViewModel(
        string source,
        bool isSystem,
        Action<string, bool>? setEnabled,
        Action<SystemCapability, bool>? setSystemCapabilityEnabled = null,
        Action<SystemCapability>? recoverSystemCapability = null,
        Action<SystemCapability>? revealSystemCapabilityApplication = null,
        Action<BrowserKind>? openBrowserSetup = null,
        Action<string>? copyText = null,
        Action<string, bool>? setBrowserAppEnabled = null)
    {
        Source = source;
        IsSystem = isSystem;
        _setEnabled = setEnabled;
        _setSystemCapabilityEnabled = setSystemCapabilityEnabled;
        _recoverSystemCapability = recoverSystemCapability;
        _revealSystemCapabilityApplication = revealSystemCapabilityApplication;
        _openBrowserSetup = openBrowserSetup;
        _copyText = copyText;
        _setBrowserAppEnabled = setBrowserAppEnabled;
    }

    public string Source { get; }
    public bool IsSystem { get; }
    public bool IsExternal => !IsSystem;
    public bool IsBrowser => Source == ActivitySources.Browser;
    public bool IsNotBrowser => !IsBrowser;
    public bool CanToggle => !IsSystem;
    public ObservableCollection<SystemCapabilityItemViewModel> Capabilities { get; } = [];
    public ObservableCollection<BrowserAppItemViewModel> BrowserApps { get; } = [];
    public bool HasBrowserApps => BrowserApps.Count > 0;
    public bool HasCapabilities => Capabilities.Count > 0;

    [ObservableProperty]
    private bool _isExpanded;

    public bool IsCollapsed => !IsExpanded;

    [ObservableProperty]
    private bool _isActive;

    public string ActivityText => IsActive ? "活跃" : "不活跃";
    public bool IsInactive => !IsActive;

    [ObservableProperty]
    private bool _isPackageInstalled;

    [ObservableProperty]
    private string? _packageVersion;

    [ObservableProperty]
    private string? _sideloadDirectory;

    [ObservableProperty]
    private string _runtimeStatusDetail = string.Empty;

    [ObservableProperty]
    private bool _reloadRequired;

    [ObservableProperty]
    private string? _previousKnownGoodVersion;

    public ExternalHostRuntimeStatus? RuntimeStatus { get; private set; }
    public string BrowserStatusText => !IsPackageInstalled
        ? "未安装采集器包"
        : !Enabled
            ? "已停用"
            : RuntimeStatus switch
            {
                ExternalHostRuntimeStatus.Ready => "正在采集",
                ExternalHostRuntimeStatus.Degraded => "需要修复",
                _ => "等待浏览器启动"
            };
    public string BrowserStatusDetail => !IsPackageInstalled
        ? "浏览器采集器独立发布，需要单独安装采集器包后才能连接"
        : RuntimeStatus == ExternalHostRuntimeStatus.Degraded
            ? RuntimeStatusDetail
            : "采集每个浏览器窗口当前打开的标签页";
    public bool IsBrowserReady => Enabled && RuntimeStatus == ExternalHostRuntimeStatus.Ready;
    public bool IsBrowserDegraded => Enabled && RuntimeStatus == ExternalHostRuntimeStatus.Degraded;
    public bool IsBrowserWaiting => !IsBrowserReady && !IsBrowserDegraded;
    public string PackageVersionText => IsPackageInstalled
        ? $"Package {PackageVersion}"
        : "尚未导入 Package";
    public string PreviousKnownGoodText => PreviousKnownGoodVersion is { Length: > 0 } version
        ? $"上一已知良好版本 {version} 已保留"
        : string.Empty;
    public bool HasPreviousKnownGood => PreviousKnownGoodVersion is { Length: > 0 };

    [ObservableProperty]
    private bool _isBrowserSetupVisible;

    [ObservableProperty]
    private bool _isBrowserConfigurationExpanded;

    [ObservableProperty]
    private bool _isBrowserDetailsExpanded;

    [ObservableProperty]
    private BrowserKind _setupBrowser;

    [ObservableProperty]
    private string _browserSetupError = string.Empty;

    public string SetupBrowserName => SetupBrowser == BrowserKind.Edge ? "Edge" : "Chrome";
    public string BrowserSetupTitle => $"在 {SetupBrowserName} 中完成连接";
    public bool HasBrowserSetupError => !string.IsNullOrWhiteSpace(BrowserSetupError);
    public bool HasSideloadDirectory => !string.IsNullOrWhiteSpace(SideloadDirectory);
    public string BrowserDetailsToggleText => IsBrowserDetailsExpanded ? "收起诊断" : "高级诊断";

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
        ActivitySources.Browser => "浏览器采集器，采集标签页活动",
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

    public void UpdateBrowserRuntime(BrowserCollectorState snapshot)
    {
        if (!IsBrowser) return;
        IsPackageInstalled = snapshot.IsInstalled;
        PackageVersion = snapshot.PackageVersion;
        SideloadDirectory = snapshot.SideloadDirectory;
        RuntimeStatusDetail = snapshot.RuntimeStatusDetail;
        ReloadRequired = snapshot.ReloadRequired;
        PreviousKnownGoodVersion = snapshot.PreviousKnownGoodVersion;
        RuntimeStatus = snapshot.RuntimeStatus;
        SetEnabledSilently(snapshot.DesiredEnabled);
        var incoming = snapshot.Apps ?? [];
        foreach (var stale in BrowserApps.Where(item =>
                     !incoming.Any(app => app.AppHint == item.AppHint)).ToArray())
            BrowserApps.Remove(stale);
        foreach (var app in incoming)
        {
            var item = BrowserApps.FirstOrDefault(existing => existing.AppHint == app.AppHint);
            if (item is null)
            {
                item = new BrowserAppItemViewModel(app.AppHint, _setBrowserAppEnabled);
                BrowserApps.Add(item);
            }
            item.Update(app);
        }
        if (snapshot.RuntimeStatus == ExternalHostRuntimeStatus.Ready)
            IsBrowserSetupVisible = false;
        BrowserSetupError = string.Empty;
        OnPropertyChanged(nameof(BrowserStatusText));
        OnPropertyChanged(nameof(BrowserStatusDetail));
        OnPropertyChanged(nameof(IsBrowserReady));
        OnPropertyChanged(nameof(IsBrowserDegraded));
        OnPropertyChanged(nameof(IsBrowserWaiting));
        OnPropertyChanged(nameof(PackageVersionText));
        OnPropertyChanged(nameof(PreviousKnownGoodText));
        OnPropertyChanged(nameof(HasPreviousKnownGood));
        OnPropertyChanged(nameof(HasSideloadDirectory));
        OnPropertyChanged(nameof(HasBrowserApps));
    }

    [RelayCommand]
    private void OpenBrowserSetup(BrowserKind browser)
    {
        IsBrowserConfigurationExpanded = true;
        if (string.IsNullOrWhiteSpace(SideloadDirectory))
        {
            BrowserSetupError = "浏览器采集器目录尚未准备好。";
            IsBrowserSetupVisible = true;
            return;
        }
        try
        {
            SetupBrowser = browser;
            _copyText?.Invoke(SideloadDirectory);
            _openBrowserSetup?.Invoke(browser);
            BrowserSetupError = string.Empty;
            IsBrowserSetupVisible = true;
        }
        catch (Exception exception)
        {
            BrowserSetupError = exception.Message;
            IsBrowserSetupVisible = true;
        }
    }

    [RelayCommand]
    private void ToggleBrowserConfiguration() =>
        IsBrowserConfigurationExpanded = !IsBrowserConfigurationExpanded;

    [RelayCommand]
    private void ToggleBrowserDetails() => IsBrowserDetailsExpanded = !IsBrowserDetailsExpanded;

    partial void OnEnabledChanged(bool value)
    {
        if (!_suppressEnabled)
            _setEnabled?.Invoke(Source, value);
        OnPropertyChanged(nameof(BrowserStatusText));
        OnPropertyChanged(nameof(IsBrowserReady));
        OnPropertyChanged(nameof(IsBrowserDegraded));
        OnPropertyChanged(nameof(IsBrowserWaiting));
    }

    partial void OnIsActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(ActivityText));
        OnPropertyChanged(nameof(IsInactive));
    }

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(IsCollapsed));

    partial void OnSetupBrowserChanged(BrowserKind value)
    {
        OnPropertyChanged(nameof(SetupBrowserName));
        OnPropertyChanged(nameof(BrowserSetupTitle));
    }

    partial void OnBrowserSetupErrorChanged(string value) => OnPropertyChanged(nameof(HasBrowserSetupError));

    partial void OnIsBrowserDetailsExpandedChanged(bool value) =>
        OnPropertyChanged(nameof(BrowserDetailsToggleText));
}

public partial class BrowserAppItemViewModel : ObservableObject
{
    private readonly Action<string, bool>? _setEnabled;
    private bool _suppressEnabled;

    public BrowserAppItemViewModel(string appHint, Action<string, bool>? setEnabled)
    {
        AppHint = appHint;
        _setEnabled = setEnabled;
    }

    public string AppHint { get; }
    public string DisplayName => AppHint switch
    {
        "chrome" => "Google Chrome",
        "edge" => "Microsoft Edge",
        _ => AppHint,
    };

    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private string _statusText = string.Empty;

    public void Update(BrowserCollectorAppState state)
    {
        _suppressEnabled = true;
        Enabled = state.DesiredEnabled;
        _suppressEnabled = false;
        StatusText = state.RuntimeStatus switch
        {
            ExternalHostRuntimeStatus.Ready => $"正在采集 · Package {state.PackageVersion}",
            ExternalHostRuntimeStatus.Degraded => state.RuntimeStatusDetail,
            _ => state.DesiredEnabled ? "等待浏览器连接" : "已停用",
        };
    }

    partial void OnEnabledChanged(bool value)
    {
        if (!_suppressEnabled)
            _setEnabled?.Invoke(AppHint, value);
    }
}
