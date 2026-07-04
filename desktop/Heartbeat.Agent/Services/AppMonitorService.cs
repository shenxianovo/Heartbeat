using Heartbeat.Agent.Configuration;
using Heartbeat.Agent.Models;
using Heartbeat.Agent.Utils;
using Heartbeat.Core;
using Heartbeat.Core.DTOs.Usage;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Heartbeat.Agent.Services
{
    public class AppMonitorService(
        IClock clock,
        IWindowEventMonitor windowMonitor,
        IPowerMonitor powerMonitor,
        IInputActivitySignal inputActivity,
        ConfigManager configManager) : IHostedService, IDisposable
    {
        // 标题变化门控窗口：标题变化前此时段内有点击才切段（ADR-016）。
        private static readonly TimeSpan TitleGateWindow = TimeSpan.FromSeconds(1);

        private readonly object _lock = new();
        private string? _currentApp;
        private string? _currentTitle;   // 最新观测标题：仅用于变化比较（门控），不写入段
        private string? _segmentTitle;   // 段标题：切段时刻定格，非门控抖动不改写（同 Id 快照身份不变，ADR-018）
        private Guid _currentId;         // 活动身份：段开始时生成，跨 flush 快照复用（ADR-018）
        private DateTimeOffset _currentStart;
        private readonly List<AppUsageItem> _usages = [];

        // away 状态（息屏 / 睡眠）。详见 ADR-014。away 段与真实段同构：稳定 Id + 快照生长。
        private bool _isAway;
        private Guid _awayId;
        private DateTimeOffset _awayStart;

        // AwayProcessNames 的快照，避免热路径（高频 NAMECHANGE）每次 clone 整个配置。
        // 仅在配置变更时刷新（ConfigChanged 事件），读取无锁。
        private volatile string[] _awayProcessNames = [];

        public event Action<string?>? CurrentAppChanged;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Log.Information("应用监测服务启动");

            _awayProcessNames = [.. configManager.Current.AwayProcessNames];
            configManager.ConfigChanged += OnConfigChanged;

            windowMonitor.ForegroundWindowChanged += OnForegroundChanged;

            powerMonitor.DisplayOff += OnEnterAway;
            powerMonitor.Suspend += OnEnterAway;
            powerMonitor.DisplayOn += OnExitAway;
            powerMonitor.Resume += OnExitAway;

            var initial = windowMonitor.GetForegroundWindow();
            var initialApp = Normalize(initial.ProcessName);
            if (initialApp != null)
            {
                lock (_lock)
                {
                    StartSegment(initialApp, initial.Title, clock.UtcNow);
                    Log.Information("初始前台应用: {App}", initialApp);
                }
            }

            windowMonitor.Start();
            powerMonitor.Start();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Log.Information("应用监测服务停止");
            configManager.ConfigChanged -= OnConfigChanged;
            windowMonitor.ForegroundWindowChanged -= OnForegroundChanged;
            windowMonitor.Stop();

            powerMonitor.DisplayOff -= OnEnterAway;
            powerMonitor.Suspend -= OnEnterAway;
            powerMonitor.DisplayOn -= OnExitAway;
            powerMonitor.Resume -= OnExitAway;
            powerMonitor.Stop();
            return Task.CompletedTask;
        }

        private void OnForegroundChanged(ForegroundWindow fw)
        {
            var newApp = Normalize(fw.ProcessName);
            var newTitle = fw.Title;
            var now = clock.UtcNow;

            lock (_lock)
            {
                // away 期间（息屏/睡眠）忽略前台切换：不累加、不开段。
                // away 段在 OnExitAway 时统一以 [_awayStart, now] 发出。
                if (_isAway)
                    return;

                var appSame = string.Equals(_currentApp, newApp, StringComparison.OrdinalIgnoreCase);
                var titleSame = string.Equals(_currentTitle, newTitle, StringComparison.Ordinal);

                // 完全没变：忽略。
                if (appSame && titleSame)
                    return;

                // App 没变、仅标题变：门控（ADR-016）。
                // 只有标题变化前 1s 内有点击（含触摸板），才认为是人为切换而切段；
                // 否则视为程序自身抖动（spinner/后台刷新/自动播放），只更新当前标题、不切段。
                if (appSame && !inputActivity.ClickedWithin(TitleGateWindow))
                {
                    // 只更新比较基准，不改段标题：段身份（app+起始标题）在 Id 生命周期内不变，
                    // 抖动值不参与归因（顺带修复 ADR-017 记录的 last-title-wins 缺陷）。
                    _currentTitle = newTitle;
                    return;
                }

                CloseCurrentSegment(now);
                StartSegment(newApp, newTitle, now);

                if (newApp != null)
                    Log.Debug("应用切换: {App} / {Title}", newApp, newTitle);
            }

            CurrentAppChanged?.Invoke(newApp);
        }

        /// <summary>进入 away（息屏 / 睡眠）。首个触发生效，期间重复信号忽略。</summary>
        private void OnEnterAway()
        {
            var now = clock.UtcNow;
            lock (_lock)
            {
                if (_isAway) return;

                // 用信号到达时刻封口当前真实应用段（Suspend 在挂起前执行，≈ 入睡时刻）。
                CloseCurrentSegment(now);

                _isAway = true;
                _awayId = Guid.CreateVersion7();
                _awayStart = now;
                _currentApp = null;
                _currentTitle = null;
                _segmentTitle = null;
                _currentStart = default;
                Log.Information("进入 away（息屏/睡眠），封口当前应用段");
            }

            CurrentAppChanged?.Invoke(null);
        }

        /// <summary>退出 away（亮屏 / 唤醒）。发出 away 段并以当前前台重开。</summary>
        private void OnExitAway()
        {
            var now = clock.UtcNow;
            string? resumedApp;
            lock (_lock)
            {
                if (!_isAway) return;

                // 发出 away 终态快照 [_awayStart, now]（与中途 flush 快照同 Id，服务端收敛为一行）。
                AppendSegment(_awayId, SyntheticApps.Away, null, _awayStart, now);

                _isAway = false;

                // 以当前真实前台开新段（可能仍是睡前应用，也可能变了）。
                var resumed = windowMonitor.GetForegroundWindow();
                resumedApp = Normalize(resumed.ProcessName);
                StartSegment(resumedApp, resumed.Title, now);
                Log.Information("退出 away（亮屏/唤醒），恢复前台: {App}", resumedApp ?? "(无)");
            }

            CurrentAppChanged?.Invoke(resumedApp);
        }

        public string? GetCurrentApp()
        {
            lock (_lock)
            {
                return _isAway ? null : _currentApp;
            }
        }

        public List<AppUsageItem> GetAndClearUsages()
        {
            var now = clock.UtcNow;

            lock (_lock)
            {
                // flush 发进行中段的快照，不封口不重开：Id/StartTime 保持，服务端按 Id 生长（ADR-018）。
                // away 进行中同样快照，长息屏在服务端可见地生长。
                if (!_isAway && _currentApp != null && _currentStart != default)
                {
                    AppendSegment(_currentId, _currentApp, _segmentTitle, _currentStart, now);
                }
                else if (_isAway)
                {
                    AppendSegment(_awayId, SyntheticApps.Away, null, _awayStart, now);
                }

                var copy = new List<AppUsageItem>(_usages);
                _usages.Clear();

                Log.Information("收集到 {Count} 条使用记录，准备上传", copy.Count);
                foreach (var item in copy)
                {
                    Log.Debug("  {App}: {Start:HH:mm:ss} - {End:HH:mm:ss} ({Duration:F1}s)",
                        item.AppName, item.StartTime.LocalDateTime, item.EndTime.LocalDateTime,
                        (item.EndTime - item.StartTime).TotalSeconds);
                }

                return copy;
            }
        }

        /// <summary>开一个新活动段：新 Id、标题定格。调用方必须持有 _lock。</summary>
        private void StartSegment(string? app, string? title, DateTimeOffset now)
        {
            _currentId = Guid.CreateVersion7();
            _currentApp = app;
            _currentTitle = title;
            _segmentTitle = title;
            _currentStart = now;
        }

        /// <summary>封口当前真实应用段（仅 ≥1s 记录）。调用方必须持有 _lock。</summary>
        private void CloseCurrentSegment(DateTimeOffset now)
        {
            if (_currentApp != null && _currentStart != default)
                AppendSegment(_currentId, _currentApp, _segmentTitle, _currentStart, now);
        }

        /// <summary>追加一个 ≥1s 的段快照。调用方必须持有 _lock。</summary>
        private void AppendSegment(Guid id, string appName, string? title, DateTimeOffset start, DateTimeOffset end)
        {
            if (start == default) return;
            var duration = end - start;
            if (duration.TotalSeconds < 1) return;

            _usages.Add(new AppUsageItem
            {
                // Id 即活动身份（ADR-018）：同段跨 flush 复用，服务端按 Id upsert 收敛。
                Id = id,
                AppName = appName,
                Title = title,
                StartTime = start,
                EndTime = end
            });
            Log.Debug("段快照: {App} / {Title}，累计 {Duration:F1}s", appName, title, duration.TotalSeconds);
        }

        private void OnConfigChanged(AgentConfig config)
        {
            _awayProcessNames = [.. config.AwayProcessNames ?? []];
        }

        /// <summary>命中 AwayProcessNames 的前台进程名归一化为 away 段名（仅改名，不驱动状态机）。</summary>
        private string? Normalize(string? app)
        {
            if (string.IsNullOrEmpty(app)) return app;

            var awayNames = _awayProcessNames;
            foreach (var name in awayNames)
            {
                if (string.Equals(app, name, StringComparison.OrdinalIgnoreCase))
                    return SyntheticApps.Away;
            }
            return app;
        }

        public void Dispose()
        {
            configManager.ConfigChanged -= OnConfigChanged;
            windowMonitor.ForegroundWindowChanged -= OnForegroundChanged;
            windowMonitor.Stop();

            powerMonitor.DisplayOff -= OnEnterAway;
            powerMonitor.Suspend -= OnEnterAway;
            powerMonitor.DisplayOn -= OnExitAway;
            powerMonitor.Resume -= OnExitAway;
            powerMonitor.Stop();
            GC.SuppressFinalize(this);
        }
    }
}
