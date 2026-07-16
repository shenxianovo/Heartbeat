using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heartbeat.Agent.Configuration;
using Heartbeat.Agent.Models;
using Heartbeat.Agent.Services;
using Heartbeat.Core;
using Heartbeat.WPF.Logging;
using Serilog.Events;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;

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
        private string _apiKey = string.Empty;

        [ObservableProperty]
        private string _deviceName = string.Empty;

        [ObservableProperty]
        private string _uploadIntervalMinutes = "1";

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
        private readonly int _maxLogLines;
        private readonly RingBufferSink _logSink;
        private bool _suppressAutoStartEvent;

        /// <summary>采集器栏（ADR-026 §5）：system + 各已注册 plugin。</summary>
        public ObservableCollection<CollectorItemViewModel> Collectors { get; } = [];

        /// <summary>Active 是时间派生量，需周期重算（关浏览器后应转灰，无事件驱动）。</summary>
        private readonly DispatcherTimer _activityTimer;

        /// <summary>依赖全部构造注入（ADR-021）：VM 不再 service-locate，可脱离运行中的 App 实例化。</summary>
        public MainViewModel(
            ConfigManager configManager,
            ICollectionStatus status,
            IAutoStartService autoStartService,
            RingBufferSink logSink)
        {
            _configManager = configManager;
            _status = status;
            _autoStartService = autoStartService;
            _logSink = logSink;
            _maxLogLines = logSink.Capacity;

            LoadConfig();
            LoadAutoStartState();

            // 显示当前前台应用（hub 集面读模型，ADR-021）
            CurrentApp = FormatApp(_status.CurrentApp) ?? "(未检测)";

            // 订阅事件
            _status.CurrentAppChanged += HandleCurrentAppChanged;
            _logSink.LogChanged += HandleLogChanged;

            // 采集器栏（ADR-026）：注册表变化（新发现/开关）重建列表；定时器周期重算 Active。
            _configManager.ConfigChanged += HandleConfigChanged;
            RebuildCollectors();
            _activityTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _activityTimer.Tick += (_, _) => RefreshCollectorActivity();
            _activityTimer.Start();

            // 加载已有日志（GetAll 同时会同步 lastNotifiedAt，后续事件只推增量）
            var existingLogs = _logSink.GetAll();
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
            ApiKey = config.ApiKey;
            DeviceName = config.DeviceName;
            UploadIntervalMinutes = config.UploadIntervalMinutes.ToString();
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

            _configManager.Update(c =>
            {
                c.ApiKey = ApiKey.Trim();
                c.DeviceName = DeviceName.Trim();
                c.UploadIntervalMinutes = uploadInterval;
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

        // ---- 采集器栏（ADR-026 §5） ----

        /// <summary>
        /// 重建列表：system 恒在首位（只读、恒 Active——Agent 自身在跑即证明）；
        /// plugin 来自注册表（"已安装 = 被 hub 见过"）。就地增改删，保持条目实例稳定，
        /// 避免用户操作开关时列表重建打断交互。
        /// </summary>
        private void RebuildCollectors()
        {
            var registry = _configManager.Current.Collectors;
            var wanted = new List<string> { ActivitySources.System };
            wanted.AddRange(registry.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));

            // 删除已不在注册表的条目（倒序删避免索引位移）
            for (var i = Collectors.Count - 1; i >= 0; i--)
            {
                if (!wanted.Contains(Collectors[i].Source))
                    Collectors.RemoveAt(i);
            }

            // 按序补齐/更新
            for (var i = 0; i < wanted.Count; i++)
            {
                var source = wanted[i];
                var existing = Collectors.FirstOrDefault(c => c.Source == source);
                if (existing == null)
                {
                    var isSystem = source == ActivitySources.System;
                    existing = new CollectorItemViewModel(source, isSystem, isSystem ? null : SetCollectorEnabled);
                    Collectors.Insert(Math.Min(i, Collectors.Count), existing);
                }
                if (registry.TryGetValue(source, out var entry))
                    existing.SetEnabledSilently(entry.Enabled);
            }

            RefreshCollectorActivity();
        }

        /// <summary>Active 重算：窗口从采集器自报 flushPeriodMs 派生（ADR-026 §3）。system 恒真。</summary>
        private void RefreshCollectorActivity()
        {
            var registry = _configManager.Current.Collectors;
            var lastSeen = _status.SourceLastSeen;
            var now = DateTimeOffset.UtcNow;

            foreach (var item in Collectors)
            {
                if (item.IsSystem)
                {
                    item.IsActive = true;
                    continue;
                }
                registry.TryGetValue(item.Source, out var entry);
                DateTimeOffset? seen = lastSeen.TryGetValue(item.Source, out var t) ? t : null;
                item.IsActive = CollectorActivity.IsActive(seen, entry, now);
            }
        }

        /// <summary>用户翻开关 → 写注册表；hub 的 403 强制层与采集器的礼貌层随之生效（ADR-026 §4）。</summary>
        private void SetCollectorEnabled(string source, bool enabled)
        {
            _configManager.Update(c =>
            {
                if (c.Collectors.TryGetValue(source, out var entry))
                    entry.Enabled = enabled;
            });
        }

        /// <summary>注册表变化（采集器新注册/别处翻开关）→ 重建列表。事件可能来自 ingest 线程，回 UI 线程。</summary>
        private void HandleConfigChanged(AgentConfig _)
        {
            Application.Current?.Dispatcher.BeginInvoke(RebuildCollectors);
        }

        public void Dispose()
        {
            _status.CurrentAppChanged -= HandleCurrentAppChanged;
            _logSink.LogChanged -= HandleLogChanged;
            _configManager.ConfigChanged -= HandleConfigChanged;
            _activityTimer.Stop();
            GC.SuppressFinalize(this);
        }
    }
}
