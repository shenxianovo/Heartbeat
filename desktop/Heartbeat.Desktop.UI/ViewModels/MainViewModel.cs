using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heartbeat.Core;
using Heartbeat.Desktop.UI.Logging;
using Heartbeat.Desktop.UI.Presentation;
using Heartbeat.Hub.Core.Presence;
using Heartbeat.Hub.Core.Upload;
using System.Collections.ObjectModel;
using Serilog.Events;

namespace Heartbeat.Desktop.UI.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IDesktopState _desktopState;
    private readonly IUpdateController _updates;
    private readonly IWindowController _window;
    private readonly IPresentationScheduler _scheduler;
    private readonly ILogFeed _logs;
    private readonly IDisposable _activityRefresh;
    private DesktopSettingsSnapshot? _lastSettings;
    private bool? _lastLoginStart;
    private bool _suppressLoginStart;
    private readonly List<LogEntry> _allLogs = [];

    [ObservableProperty]
    private string _currentApp = "(未检测)";

    [ObservableProperty]
    private UpdateState _updateState;

    [ObservableProperty]
    private string? _updateVersion;

    [ObservableProperty]
    private int? _updateDownloadProgress;

    [ObservableProperty]
    private string? _updateError;

    [ObservableProperty]
    private UpdateCheckResult? _lastUpdateCheckResult;

    [ObservableProperty]
    private string _updateCheckMessage = string.Empty;

    [ObservableProperty]
    private string _apiKey = string.Empty;

    [ObservableProperty]
    private string _deviceName = string.Empty;

    [ObservableProperty]
    private string _uploadIntervalMinutes = "1";

    [ObservableProperty]
    private bool _loginStartEnabled;

    [ObservableProperty]
    private string _saveStatusMessage = string.Empty;

    [ObservableProperty]
    private bool _saveStatusIsError;

    [ObservableProperty]
    private string? _capabilityMessage;

    [ObservableProperty]
    private string _logText = string.Empty;

    [ObservableProperty]
    private LogEventLevel _selectedLogLevel = LogEventLevel.Information;

    public ObservableCollection<CollectorItemViewModel> Collectors { get; } = [];
    public ObservableCollection<OperationalNoticeViewModel> OperationalNotices { get; } = [];
    public ObservableCollection<CapabilityItemViewModel> Capabilities { get; } = [];

    public MainViewModel(
        IDesktopState desktopState,
        IUpdateController updates,
        IWindowController window,
        IPresentationScheduler scheduler,
        ILogFeed logs)
    {
        _desktopState = desktopState;
        _updates = updates;
        _window = window;
        _scheduler = scheduler;
        _logs = logs;

        ApplyState(desktopState.Current);
        ApplyUpdateState(updates.Current);
        _desktopState.Changed += HandleStateChanged;
        _updates.Changed += HandleUpdateChanged;
        _logs.Changed += HandleLogsChanged;
        _activityRefresh = scheduler.SchedulePeriodic(
            TimeSpan.FromSeconds(5),
            () => scheduler.Post(() => RefreshCollectorActivity(_desktopState.Current)));

        _allLogs.AddRange(_logs.GetAll());
        RebuildLogText();
    }

    private void HandleStateChanged(DesktopStateSnapshot snapshot) =>
        _scheduler.Post(() => ApplyState(snapshot));

    private void HandleUpdateChanged(UpdateSnapshot snapshot) =>
        _scheduler.Post(() => ApplyUpdateState(snapshot));

    private void HandleLogsChanged(IReadOnlyList<LogEntry> entries) =>
        _scheduler.Post(() =>
        {
            _allLogs.AddRange(entries);
            if (_allLogs.Count > _logs.Capacity * 2)
                _allLogs.RemoveRange(0, _allLogs.Count - _logs.Capacity);
            RebuildLogText();
        });

    private void ApplyUpdateState(UpdateSnapshot snapshot)
    {
        UpdateState = snapshot.State;
        UpdateVersion = snapshot.Version;
        UpdateDownloadProgress = snapshot.DownloadProgress;
        UpdateError = snapshot.Error;
        ApplyUpdateCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedLogLevelChanged(LogEventLevel value) => RebuildLogText();

    private void RebuildLogText()
    {
        LogText = string.Join(
            Environment.NewLine,
            _allLogs.Where(entry => entry.Level >= SelectedLogLevel).Select(entry => entry.Message));
    }

    [RelayCommand]
    private void SetLogLevel(string level) =>
        SelectedLogLevel = Enum.Parse<LogEventLevel>(level);

    private void ApplyState(DesktopStateSnapshot snapshot)
    {
        CurrentApp = FormatActivity(snapshot.CurrentActivity) ?? "(未检测)";
        ApplySettings(snapshot);
        RebuildCollectors(snapshot);
        RebuildOperationalNotices(snapshot);
        RebuildCapabilities(snapshot.Capabilities);
    }

    private void RebuildCapabilities(DesktopCapabilitySnapshot capabilities)
    {
        Capabilities.Clear();
        Capabilities.Add(new CapabilityItemViewModel(
            "前台应用",
            "识别当前活动的 AppIdentity",
            capabilities.AppObservation));
        Capabilities.Add(new CapabilityItemViewModel(
            "窗口活动",
            "识别 focused-window 与标题变化",
            capabilities.FocusedWindowObservation));
        Capabilities.Add(new CapabilityItemViewModel(
            "交互信号",
            "仅本地用于标题噪声门控，不保存、不上传",
            capabilities.InteractionSignal));
        Capabilities.Add(new CapabilityItemViewModel(
            "输入事件记录",
            "持久化并上传物理按键与鼠标统计",
            capabilities.InputEventRecording));
        CapabilityMessage = capabilities.Message;
    }

    private void ApplySettings(DesktopStateSnapshot snapshot)
    {
        if (_lastSettings != snapshot.Settings)
        {
            ApiKey = snapshot.Settings.ApiKey;
            DeviceName = snapshot.Settings.DeviceName;
            UploadIntervalMinutes = snapshot.Settings.UploadIntervalMinutes.ToString();
            _lastSettings = snapshot.Settings;
        }

        if (_lastLoginStart != snapshot.LoginStartEnabled)
        {
            _suppressLoginStart = true;
            LoginStartEnabled = snapshot.LoginStartEnabled;
            _suppressLoginStart = false;
            _lastLoginStart = snapshot.LoginStartEnabled;
        }
    }

    private void RebuildOperationalNotices(DesktopStateSnapshot snapshot)
    {
        OperationalNotices.Clear();

        if (snapshot.Compatibility.UpdateRequired)
        {
            OperationalNotices.Add(new OperationalNoticeViewModel(
                OperationalNoticeKind.UpdateRequired,
                "需要更新 Heartbeat",
                snapshot.Compatibility.Message ?? "服务器要求更新 Heartbeat 后再继续上传。",
                "检查并应用更新"));
        }

        foreach (var (stream, status) in snapshot.UploadStreams.OrderBy(pair => pair.Key))
        {
            var notice = status.State switch
            {
                UploadStreamState.CacheMigrationFailed => new OperationalNoticeViewModel(
                    OperationalNoticeKind.CacheMigrationFailed,
                    $"{stream}缓存迁移失败",
                    status.Message ?? "缓存迁移失败，上传已暂停。",
                    status.Action,
                    status.RecoveryPath),
                UploadStreamState.CacheWriteFailed => new OperationalNoticeViewModel(
                    OperationalNoticeKind.CacheWriteFailed,
                    $"{stream}缓存写入失败",
                    status.Message ?? "离线数据无法安全写入缓存。",
                    status.Action,
                    status.RecoveryPath),
                UploadStreamState.DeadLetterWriteFailed => new OperationalNoticeViewModel(
                    OperationalNoticeKind.DeadLetterWriteFailed,
                    $"{stream}隔离记录写入失败",
                    status.Message ?? "永久拒绝的记录无法写入 dead letter。",
                    status.Action,
                    status.DeadLetterPath),
                _ => null
            };

            if (notice != null)
                OperationalNotices.Add(notice);

            if (status.DeadLetterCount > 0)
            {
                OperationalNotices.Add(new OperationalNoticeViewModel(
                    OperationalNoticeKind.DeadLettersAvailable,
                    $"{stream}存在已隔离记录",
                    $"有 {status.DeadLetterCount} 条记录被永久拒绝，可检查诊断文件。",
                    "查看诊断文件",
                    status.DeadLetterPath));
            }
        }
    }

    private void RebuildCollectors(DesktopStateSnapshot snapshot)
    {
        var wanted = new List<string> { ActivitySources.System };
        wanted.AddRange(snapshot.Collectors.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase));

        for (var index = Collectors.Count - 1; index >= 0; index--)
        {
            if (!wanted.Contains(Collectors[index].Source, StringComparer.OrdinalIgnoreCase))
                Collectors.RemoveAt(index);
        }

        for (var index = 0; index < wanted.Count; index++)
        {
            var source = wanted[index];
            var item = Collectors.FirstOrDefault(existing =>
                string.Equals(existing.Source, source, StringComparison.OrdinalIgnoreCase));
            if (item == null)
            {
                var isSystem = string.Equals(source, ActivitySources.System, StringComparison.OrdinalIgnoreCase);
                item = new CollectorItemViewModel(
                    source,
                    isSystem,
                    isSystem ? null : _desktopState.SetCollectorEnabled,
                    isSystem ? _desktopState.SetInputEventRecordingEnabled : null);
                Collectors.Insert(Math.Min(index, Collectors.Count), item);
            }

            if (item.IsSystem)
                item.SetRecordingEnabledSilently(snapshot.Settings.InputEventRecordingEnabled);
            if (snapshot.Collectors.TryGetValue(source, out var registration))
                item.SetEnabledSilently(registration.Enabled);
        }

        RefreshCollectorActivity(snapshot);
    }

    private void RefreshCollectorActivity(DesktopStateSnapshot snapshot)
    {
        var now = _scheduler.UtcNow;
        foreach (var item in Collectors)
        {
            if (item.IsSystem)
            {
                item.IsActive = true;
                continue;
            }

            snapshot.Collectors.TryGetValue(item.Source, out var registration);
            DateTimeOffset? lastSeen = snapshot.SourceLastSeen.TryGetValue(item.Source, out var seen)
                ? seen
                : null;
            item.IsActive = IsCollectorActive(lastSeen, registration?.FlushPeriodMs, now);
        }
    }

    private static bool IsCollectorActive(DateTimeOffset? lastSeen, int? flushPeriodMs, DateTimeOffset now)
    {
        if (lastSeen is not { } seen) return false;
        var window = flushPeriodMs is > 0
            ? TimeSpan.FromMilliseconds((long)flushPeriodMs.Value * 3)
            : TimeSpan.FromSeconds(90);
        return now - seen < window;
    }

    private static string? FormatActivity(CurrentActivity? activity)
    {
        if (activity == null) return null;
        if (activity.AppIdentityKey == AppIdentityKeys.Away) return "(离开)";
        return string.IsNullOrWhiteSpace(activity.AppDisplayName)
            ? activity.AppIdentityKey
            : activity.AppDisplayName;
    }

    [RelayCommand]
    private void CloseSettings() => _window.HideSettings();

    partial void OnLoginStartEnabledChanged(bool value)
    {
        if (!_suppressLoginStart)
            _desktopState.SetLoginStartEnabled(value);
    }

    [RelayCommand]
    private void SaveConfig()
    {
        if (!int.TryParse(UploadIntervalMinutes, out var uploadInterval) || uploadInterval < 1)
        {
            SaveStatusMessage = "上传间隔必须为正整数";
            SaveStatusIsError = true;
            return;
        }

        _desktopState.SaveSettings(new DesktopSettingsInput(
            ApiKey.Trim(),
            DeviceName.Trim(),
            uploadInterval));
        SaveStatusMessage = "配置已保存，下次上传周期将使用新配置";
        SaveStatusIsError = false;
    }

    private bool CanApplyUpdate() => UpdateState == UpdateState.ReadyToApply;

    [RelayCommand(CanExecute = nameof(CanApplyUpdate))]
    private Task ApplyUpdateAsync() => _updates.ApplyAsync();

    [RelayCommand]
    private async Task CheckForUpdateAsync()
    {
        LastUpdateCheckResult = await _updates.CheckAsync();
        UpdateCheckMessage = LastUpdateCheckResult switch
        {
            UpdateCheckResult.UpToDate => "当前已是最新版本。",
            UpdateCheckResult.UpdateFound => "发现新版本，正在下载。",
            UpdateCheckResult.CheckFailed => "检查更新失败，请检查网络后重试。",
            _ => "更新检查已跳过：下载或待应用的更新优先。"
        };
    }

    public void Dispose()
    {
        _desktopState.Changed -= HandleStateChanged;
        _updates.Changed -= HandleUpdateChanged;
        _logs.Changed -= HandleLogsChanged;
        _activityRefresh.Dispose();
        GC.SuppressFinalize(this);
    }
}
