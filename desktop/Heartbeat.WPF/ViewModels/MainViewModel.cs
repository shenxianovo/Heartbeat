using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heartbeat.Agent.Configuration;
using Heartbeat.Agent.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Windows;

namespace Heartbeat.WPF.ViewModels
{
    public partial class MainViewModel : ObservableObject, IDisposable
    {
        private readonly ConfigManager _configManager;
        private readonly AppMonitorService _monitor;
        private readonly IAutoStartService _autoStartService;

        [ObservableProperty]
        private string _currentApp = "(未检测)";

        [ObservableProperty]
        private string _apiBaseUrl = string.Empty;

        [ObservableProperty]
        private string _apiKey = string.Empty;

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

        private bool _suppressAutoStartEvent;

        public MainViewModel()
        {
            _configManager = App.ConfigManager;
            _monitor = App.Services.GetRequiredService<AppMonitorService>();
            _autoStartService = App.Services.GetRequiredService<IAutoStartService>();

            LoadConfig();
            LoadAutoStartState();

            // 显示当前前台应用
            var current = _monitor.GetCurrentApp();
            CurrentApp = current ?? "(未检测)";

            // 订阅事件
            _monitor.CurrentAppChanged += HandleCurrentAppChanged;
            App.LogSink.LogChanged += HandleLogChanged;

            // 加载已有日志
            var existingLogs = App.LogSink.GetAll();
            if (existingLogs.Count > 0)
            {
                LogText = string.Join(Environment.NewLine, existingLogs);
            }
        }

        private void LoadConfig()
        {
            var config = _configManager.Current;
            ApiBaseUrl = config.ApiBaseUrl;
            ApiKey = config.ApiKey;
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

        private void HandleCurrentAppChanged(string? appName)
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                CurrentApp = appName ?? "(无)";
            });
        }

        private void HandleLogChanged(IReadOnlyList<string> logs)
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                LogText = string.Join(Environment.NewLine, logs);
            });
        }

        public void Dispose()
        {
            _monitor.CurrentAppChanged -= HandleCurrentAppChanged;
            App.LogSink.LogChanged -= HandleLogChanged;
            GC.SuppressFinalize(this);
        }
    }
}
