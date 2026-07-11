using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heartbeat.Agent.Configuration;
using Heartbeat.Agent.Services;
using Heartbeat.Core;
using Heartbeat.WPF.Logging;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Events;
using System.Windows;

namespace Heartbeat.WPF.ViewModels
{
    public partial class MainViewModel : ObservableObject, IDisposable
    {
        private readonly ConfigManager _configManager;
        private readonly ICollectionStatus _status;
        private readonly IAutoStartService _autoStartService;

        [ObservableProperty]
        private string _currentApp = "(未检测)";

        [ObservableProperty]
        private string _apiBaseUrl = string.Empty;

        [ObservableProperty]
        private string _apiKey = string.Empty;

        [ObservableProperty]
        private string _authServiceBaseUrl = string.Empty;

        [ObservableProperty]
        private string _deviceName = string.Empty;

        [ObservableProperty]
        private string _uploadIntervalMinutes = "1";

        [ObservableProperty]
        private string _statusUploadIntervalSeconds = "30";

        [ObservableProperty]
        private bool _autoStartEnabled;

        [ObservableProperty]
        private string _saveStatusMessage = string.Empty;

        [ObservableProperty]
        private bool _saveStatusVisible;

        [ObservableProperty]
        private bool _saveStatusIsError;

        [ObservableProperty]
        private string _logText = string.Empty;

        [ObservableProperty]
        private LogEventLevel _selectedLogLevel = LogEventLevel.Information;

        private readonly List<LogEntry> _allLogs = [];
        private int _logLineCount;
        private readonly int _maxLogLines = App.LogSink.Capacity;
        private bool _suppressAutoStartEvent;

        public MainViewModel()
        {
            _configManager = App.ConfigManager;
            _status = App.Services.GetRequiredService<ICollectionStatus>();
            _autoStartService = App.Services.GetRequiredService<IAutoStartService>();

            LoadConfig();
            LoadAutoStartState();

            // 显示当前前台应用（hub 集面读模型，ADR-021）
            CurrentApp = FormatApp(_status.CurrentApp) ?? "(未检测)";

            // 订阅事件
            _status.CurrentAppChanged += HandleCurrentAppChanged;
            App.LogSink.LogChanged += HandleLogChanged;

            // 加载已有日志（GetAll 同时会同步 lastNotifiedAt，后续事件只推增量）
            var existingLogs = App.LogSink.GetAll();
            if (existingLogs.Count > 0)
            {
                _allLogs.AddRange(existingLogs);
                RebuildLogText();
            }
        }

        partial void OnSelectedLogLevelChanged(LogEventLevel value)
        {
            RebuildLogText();
        }

        [RelayCommand]
        private void SetLogLevel(string level)
        {
            SelectedLogLevel = Enum.Parse<LogEventLevel>(level);
        }

        private void RebuildLogText()
        {
            var filtered = _allLogs.Where(e => e.Level >= SelectedLogLevel).Select(e => e.Message);
            LogText = string.Join(Environment.NewLine, filtered);
            _logLineCount = LogText.Split(Environment.NewLine).Length;
        }

        private void LoadConfig()
        {
            var config = _configManager.Current;
            ApiBaseUrl = config.ApiBaseUrl;
            ApiKey = config.ApiKey;
            AuthServiceBaseUrl = config.AuthServiceBaseUrl;
            DeviceName = config.DeviceName;
            UploadIntervalMinutes = config.UploadIntervalMinutes.ToString();
            StatusUploadIntervalSeconds = config.StatusUploadIntervalSeconds.ToString();
        }

        private void LoadAutoStartState()
        {
            _suppressAutoStartEvent = true;
            AutoStartEnabled = _autoStartService.IsEnabled;
            _suppressAutoStartEvent = false;
        }

        partial void OnAutoStartEnabledChanged(bool value)
        {
            if (_suppressAutoStartEvent) return;

            if (value)
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                {
                    _autoStartService.Enable(exePath);
                }
            }
            else
            {
                _autoStartService.Disable();
            }
        }

        [RelayCommand]
        private void SaveConfig()
        {
            if (!int.TryParse(UploadIntervalMinutes, out var uploadInterval) || uploadInterval < 1)
            {
                ShowSaveStatus("上传间隔必须为正整数", isError: true);
                return;
            }

            if (!int.TryParse(StatusUploadIntervalSeconds, out var statusInterval) || statusInterval < 1)
            {
                ShowSaveStatus("状态间隔必须为正整数", isError: true);
                return;
            }

            _configManager.Update(c =>
            {
                c.ApiBaseUrl = ApiBaseUrl.Trim();
                c.ApiKey = ApiKey.Trim();
                c.AuthServiceBaseUrl = AuthServiceBaseUrl.Trim();
                c.DeviceName = DeviceName.Trim();
                c.UploadIntervalMinutes = uploadInterval;
                c.StatusUploadIntervalSeconds = statusInterval;
            });

            ShowSaveStatus("配置已保存，下次上传周期将使用新配置");
        }

        private void ShowSaveStatus(string message, bool isError = false)
        {
            SaveStatusMessage = message;
            SaveStatusIsError = isError;
            SaveStatusVisible = true;

            // 3 秒后隐藏
            Task.Delay(3000).ContinueWith(_ =>
            {
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    SaveStatusVisible = false;
                });
            });
        }

        /// <summary>away 原样来自读模型（ADR-021），显示语义在此解释。</summary>
        private static string? FormatApp(string? app)
            => app == SyntheticApps.Away ? "(离开)" : app;

        private void HandleCurrentAppChanged(string? appName)
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                CurrentApp = FormatApp(appName) ?? "(无)";
            });
        }

        private void HandleLogChanged(IReadOnlyList<LogEntry> newLogs)
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (newLogs.Count == 0) return;

                _allLogs.AddRange(newLogs);

                // 超过 2 倍容量时裁剪
                if (_allLogs.Count > _maxLogLines * 2)
                {
                    _allLogs.RemoveRange(0, _allLogs.Count - _maxLogLines);
                }

                var filtered = newLogs.Where(e => e.Level >= SelectedLogLevel).Select(e => e.Message);
                var newText = string.Join(Environment.NewLine, filtered);
                if (string.IsNullOrEmpty(newText)) return;

                LogText = string.IsNullOrEmpty(LogText)
                    ? newText
                    : LogText + Environment.NewLine + newText;
                _logLineCount += newLogs.Count(e => e.Level >= SelectedLogLevel);

                if (_logLineCount > _maxLogLines * 2)
                {
                    RebuildLogText();
                }
            });
        }

        public void Dispose()
        {
            _status.CurrentAppChanged -= HandleCurrentAppChanged;
            App.LogSink.LogChanged -= HandleLogChanged;
            GC.SuppressFinalize(this);
        }
    }
}
