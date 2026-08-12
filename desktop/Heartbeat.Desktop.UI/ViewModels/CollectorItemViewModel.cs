using CommunityToolkit.Mvvm.ComponentModel;

namespace Heartbeat.Desktop.UI.ViewModels;

public partial class CollectorItemViewModel : ObservableObject
{
    private readonly Action<string, bool>? _setEnabled;
    private readonly Action<bool>? _setRecordingEnabled;
    private bool _suppressEnabled;
    private bool _suppressRecording;
    private bool _interactionSignalAvailable = true;

    public CollectorItemViewModel(
        string source,
        bool isSystem,
        Action<string, bool>? setEnabled,
        Action<bool>? setRecordingEnabled = null)
    {
        Source = source;
        IsSystem = isSystem;
        _setEnabled = setEnabled;
        _setRecordingEnabled = setRecordingEnabled;
    }

    public string Source { get; }
    public bool IsSystem { get; }
    public bool CanToggle => !IsSystem;
    public bool CanToggleRecording { get; private set; }
    public string InteractionSignalDescription => IsSystem
        ? _interactionSignalAvailable
            ? "仅本地，不保存、不上传"
            : "App-only 模式下不可用"
        : string.Empty;

    [ObservableProperty]
    private bool _isActive;

    public string ActivityText => IsActive ? "活跃" : "不活跃";

    [ObservableProperty]
    private bool _enabled = true;

    [ObservableProperty]
    private bool _recordingEnabled = true;

    public string IconGlyph => char.ConvertFromUtf32(Source switch
    {
        "system" => 0xE7F4,
        "browser" => 0xE774,
        "vscode" => 0xE943,
        _ => 0xEA86,
    });

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

    partial void OnEnabledChanged(bool value)
    {
        if (!_suppressEnabled)
            _setEnabled?.Invoke(Source, value);
    }

    partial void OnIsActiveChanged(bool value) => OnPropertyChanged(nameof(ActivityText));

    public void SetRecordingEnabledSilently(bool value)
    {
        _suppressRecording = true;
        RecordingEnabled = value;
        _suppressRecording = false;
    }

    public void SetSystemCapabilities(bool interactionSignalAvailable, bool inputRecordingAvailable)
    {
        _interactionSignalAvailable = interactionSignalAvailable;
        CanToggleRecording = IsSystem && inputRecordingAvailable;
        OnPropertyChanged(nameof(InteractionSignalDescription));
        OnPropertyChanged(nameof(CanToggleRecording));
    }

    partial void OnRecordingEnabledChanged(bool value)
    {
        if (!_suppressRecording)
            _setRecordingEnabled?.Invoke(value);
    }
}
