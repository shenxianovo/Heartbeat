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
    private readonly Action<string>? _importPackage;
    private bool _suppressEnabled;

    public CollectorItemViewModel(
        string source,
        bool isSystem,
        Action<string, bool>? setEnabled,
        Action<SystemCapability, bool>? setSystemCapabilityEnabled = null,
        Action<SystemCapability>? recoverSystemCapability = null,
        Action<SystemCapability>? revealSystemCapabilityApplication = null,
        Action<string>? importPackage = null)
    {
        Source = source;
        IsSystem = isSystem;
        _setEnabled = setEnabled;
        _setSystemCapabilityEnabled = setSystemCapabilityEnabled;
        _recoverSystemCapability = recoverSystemCapability;
        _revealSystemCapabilityApplication = revealSystemCapabilityApplication;
        _importPackage = importPackage;
    }

    public string Source { get; }
    public bool IsSystem { get; }
    public bool IsExternal => !IsSystem;
    public bool IsBrowser => Source == ActivitySources.Browser;
    public bool IsNotBrowser => !IsBrowser;
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

    [ObservableProperty]
    private string _importPath = string.Empty;

    [ObservableProperty]
    private string _importError = string.Empty;

    public bool HasImportError => !string.IsNullOrWhiteSpace(ImportError);
    public ExternalHostRuntimeStatus? RuntimeStatus { get; private set; }
    public string RuntimeStatusText => RuntimeStatus?.ToString() ?? string.Empty;
    public string PackageVersionText => IsPackageInstalled
        ? $"Package {PackageVersion}"
        : "尚未导入 Package";
    public string PreviousKnownGoodText => PreviousKnownGoodVersion is { Length: > 0 } version
        ? $"上一已知良好版本 {version} 已保留"
        : string.Empty;
    public bool HasPreviousKnownGood => PreviousKnownGoodVersion is { Length: > 0 };

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
        ImportError = string.Empty;
        OnPropertyChanged(nameof(RuntimeStatusText));
        OnPropertyChanged(nameof(PackageVersionText));
        OnPropertyChanged(nameof(PreviousKnownGoodText));
        OnPropertyChanged(nameof(HasPreviousKnownGood));
    }

    [RelayCommand]
    private void ImportPackage()
    {
        if (string.IsNullOrWhiteSpace(ImportPath))
        {
            ImportError = "请输入本地 Collector Package 目录。";
            return;
        }
        try
        {
            _importPackage?.Invoke(ImportPath.Trim());
            ImportError = string.Empty;
        }
        catch (Exception exception)
        {
            ImportError = exception.Message;
        }
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

    partial void OnImportErrorChanged(string value) => OnPropertyChanged(nameof(HasImportError));

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(IsCollapsed));
}
