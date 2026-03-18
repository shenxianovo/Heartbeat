using Heartbeat.Agent.Utils;
using Heartbeat.Core.DTOs;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Heartbeat.Agent.Services
{
    public class AppMonitorService : IHostedService, IDisposable
    {
        private readonly object _lock = new();
        private string? _currentApp;
        private DateTimeOffset _currentStart;
        private readonly List<AppUsageItem> _usages = [];
        private Thread? _hookThread;

        /// <summary>
        /// 当前前台应用变更事件（UI 可订阅用于展示）
        /// </summary>
        public event Action<string?>? CurrentAppChanged;

        /// <summary>
        /// 启动前台窗口监听（由主机调用）
        /// </summary>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            Log.Information("应用监测服务启动");

            ActiveWindowHelper.ForegroundWindowChanged += OnForegroundChanged;

            var initialApp = ActiveWindowHelper.GetForegroundProcessName();
            if (initialApp != null)
            {
                lock (_lock)
                {
                    _currentApp = initialApp;
                    _currentStart = DateTimeOffset.UtcNow;
                    Log.Information("初始前台应用: {App}", initialApp);
                }
            }

            _hookThread = new Thread(() =>
            {
                try
                {
                    ActiveWindowHelper.StartHook();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "WinEvent 钩子线程异常");
                }
            })
            {
                IsBackground = true,
                Name = "WinEventHookThread"
            };
            _hookThread.Start();

            return Task.CompletedTask;
        }

        /// <summary>
        /// 停止监听（由主机调用）
        /// </summary>
        public Task StopAsync(CancellationToken cancellationToken)
        {
            Log.Information("应用监测服务停止");
            ActiveWindowHelper.ForegroundWindowChanged -= OnForegroundChanged;
            ActiveWindowHelper.StopHook();
            return Task.CompletedTask;
        }

        private void OnForegroundChanged(string? newApp)
        {
            var now = DateTimeOffset.UtcNow;

            lock (_lock)
            {
                if (string.Equals(_currentApp, newApp, StringComparison.OrdinalIgnoreCase))
                    return;

                if (_currentApp != null && _currentStart != default)
                {
                    var duration = now - _currentStart;
                    if (duration.TotalSeconds >= 1)
                    {
                        _usages.Add(new AppUsageItem
                        {
                            AppName = _currentApp,
                            StartTime = _currentStart,
                            EndTime = now
                        });
                        Log.Information("应用结束: {App}，时长 {Duration:F1}s", _currentApp, duration.TotalSeconds);
                    }
                }

                _currentApp = newApp;
                _currentStart = now;

                if (newApp != null)
                {
                    Log.Information("应用切换: {App}", newApp);
                }
            }

            // 在锁外触发事件
            CurrentAppChanged?.Invoke(newApp);
        }

        /// <summary>
        /// 获取当前前台应用名称
        /// </summary>
        public string? GetCurrentApp()
        {
            lock (_lock)
            {
                return _currentApp;
            }
        }

        /// <summary>
        /// 获取并清空已记录的使用数据（将当前活跃会话截断）
        /// </summary>
        public List<AppUsageItem> GetAndClearUsages()
        {
            var now = DateTimeOffset.UtcNow;

            lock (_lock)
            {
                if (_currentApp != null && _currentStart != default)
                {
                    var duration = now - _currentStart;
                    if (duration.TotalSeconds >= 1)
                    {
                        _usages.Add(new AppUsageItem
                        {
                            AppName = _currentApp,
                            StartTime = _currentStart,
                            EndTime = now
                        });
                    }
                    _currentStart = now;
                }

                var copy = new List<AppUsageItem>(_usages);
                _usages.Clear();

                Log.Information("收集到 {Count} 条使用记录，准备上传", copy.Count);
                foreach (var item in copy)
                {
                    Log.Information("  {App}: {Start:HH:mm:ss} - {End:HH:mm:ss} ({Duration:F1}s)",
                        item.AppName, item.StartTime.LocalDateTime, item.EndTime.LocalDateTime,
                        (item.EndTime - item.StartTime).TotalSeconds);
                }

                return copy;
            }
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }
    }
}
