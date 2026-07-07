using Heartbeat.Agent.Configuration;
using Heartbeat.Agent.Models;
using Heartbeat.Agent.Utils;
using Heartbeat.Core;
using Heartbeat.Core.DTOs.Segments;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Heartbeat.Agent.Services
{
    /// <summary>
    /// 内置 system 采集器（ADR-020）：折叠前台/标题/电源事件为 ActivitySegment 快照，
    /// 经 ISegmentSink 推进 hub 缓冲——闭合即推，进行中段按 SnapshotInterval 周期推快照，
    /// 与插件采集器的"观测 → 折叠 → 推送"同模式。
    /// </summary>
    public class AppMonitorService(
        IClock clock,
        IWindowEventMonitor windowMonitor,
        IPowerMonitor powerMonitor,
        IInputActivitySignal inputActivity,
        ISegmentSink sink,
        ConfigManager configManager) : IHostedService, IDisposable
    {
        // 标题变化门控窗口：标题变化前此时段内有点击才切段（ADR-016）。
        private static readonly TimeSpan TitleGateWindow = TimeSpan.FromSeconds(1);

        // 进行中段快照节律（ADR-020）：EndTime 尾部新鲜度上界即此值，下个快照追平，统计无损。
        private static readonly TimeSpan SnapshotInterval = TimeSpan.FromSeconds(30);

        private readonly object _lock = new();
        private string? _currentApp;
        private string? _currentTitle;   // 最新观测标题：仅用于变化比较（门控），不写入段
        private string? _segmentTitle;   // 段标题：切段时刻定格，非门控抖动不改写（同 Id 快照身份不变，ADR-018）
        private Guid _currentId;         // 活动身份：段开始时生成，跨快照复用（ADR-018）
        private DateTimeOffset _currentStart;

        // away 状态（息屏 / 睡眠）。详见 ADR-014。away 段与真实段同构：稳定 Id + 快照生长。
        private bool _isAway;
        private Guid _awayId;
        private DateTimeOffset _awayStart;

        // AwayProcessNames 的快照，避免热路径（高频 NAMECHANGE）每次 clone 整个配置。
        // 仅在配置变更时刷新（ConfigChanged 事件），读取无锁。
        private volatile string[] _awayProcessNames = [];

        private CancellationTokenSource? _snapshotCts;
        private Task? _snapshotLoop;

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

            _snapshotCts = new CancellationTokenSource();
            _snapshotLoop = Task.Run(() => SnapshotLoopAsync(_snapshotCts.Token), CancellationToken.None);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Log.Information("应用监测服务停止");
            _snapshotCts?.Cancel();

            // 终态快照：注册顺序保证本服务先于 UsageUploadWorker 停止（ADR-020），
            // 快照进入 hub 后由 worker 的最终 drain 带走。
            PushCurrentSnapshot();

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

        private async Task SnapshotLoopAsync(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(SnapshotInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(ct))
                    PushCurrentSnapshot();
            }
            catch (OperationCanceledException)
            {
                // 正常停止
            }
        }

        /// <summary>
        /// 推送进行中段（真实或 away）的当前快照：不封口不重开，Id/StartTime 保持，
        /// 服务端按 Id 生长（ADR-018）。定时循环与 StopAsync 调用；测试直接调用模拟 flush。
        /// </summary>
        public void PushCurrentSnapshot()
        {
            var now = clock.UtcNow;
            ActivitySegmentItem? snapshot;
            lock (_lock)
            {
                snapshot = _isAway
                    ? BuildSegment(_awayId, SyntheticApps.Away, null, _awayStart, now)
                    : BuildSegment(_currentId, _currentApp, _segmentTitle, _currentStart, now);
            }
            if (snapshot != null)
                sink.Push([snapshot]);
        }

        private void OnForegroundChanged(ForegroundWindow fw)
        {
            var newApp = Normalize(fw.ProcessName);
            var newTitle = fw.Title;
            var now = clock.UtcNow;
            ActivitySegmentItem? closed;

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

                closed = CloseCurrentSegment(now);
                StartSegment(newApp, newTitle, now);

                if (newApp != null)
                    Log.Debug("应用切换: {App} / {Title}", newApp, newTitle);
            }

            if (closed != null)
                sink.Push([closed]);
            CurrentAppChanged?.Invoke(newApp);
        }

        /// <summary>进入 away（息屏 / 睡眠）。首个触发生效，期间重复信号忽略。</summary>
        private void OnEnterAway()
        {
            var now = clock.UtcNow;
            ActivitySegmentItem? closed;
            lock (_lock)
            {
                if (_isAway) return;

                // 用信号到达时刻封口当前真实应用段（Suspend 在挂起前执行，≈ 入睡时刻）。
                closed = CloseCurrentSegment(now);

                _isAway = true;
                _awayId = Guid.CreateVersion7();
                _awayStart = now;
                _currentApp = null;
                _currentTitle = null;
                _segmentTitle = null;
                _currentStart = default;
                Log.Information("进入 away（息屏/睡眠），封口当前应用段");
            }

            if (closed != null)
                sink.Push([closed]);
            CurrentAppChanged?.Invoke(null);
        }

        /// <summary>退出 away（亮屏 / 唤醒）。发出 away 段并以当前前台重开。</summary>
        private void OnExitAway()
        {
            var now = clock.UtcNow;
            string? resumedApp;
            ActivitySegmentItem? awayFinal;
            lock (_lock)
            {
                if (!_isAway) return;

                // away 终态快照 [_awayStart, now]（与中途周期快照同 Id，服务端收敛为一行）。
                awayFinal = BuildSegment(_awayId, SyntheticApps.Away, null, _awayStart, now);

                _isAway = false;

                // 以当前真实前台开新段（可能仍是睡前应用，也可能变了）。
                var resumed = windowMonitor.GetForegroundWindow();
                resumedApp = Normalize(resumed.ProcessName);
                StartSegment(resumedApp, resumed.Title, now);
                Log.Information("退出 away（亮屏/唤醒），恢复前台: {App}", resumedApp ?? "(无)");
            }

            if (awayFinal != null)
                sink.Push([awayFinal]);
            CurrentAppChanged?.Invoke(resumedApp);
        }

        public string? GetCurrentApp()
        {
            lock (_lock)
            {
                return _isAway ? null : _currentApp;
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

        /// <summary>封口当前真实应用段的终态快照（仅 ≥1s）。调用方必须持有 _lock。</summary>
        private ActivitySegmentItem? CloseCurrentSegment(DateTimeOffset now)
            => BuildSegment(_currentId, _currentApp, _segmentTitle, _currentStart, now);

        /// <summary>
        /// 构造一个 ≥1s 的 system 段快照（不足 1s 返回 null，不产噪声段）。
        /// IdentityKey 由客户端计算（ADR-020）——与插件采集器"自己声明身份判据"对齐。
        /// </summary>
        private static ActivitySegmentItem? BuildSegment(Guid id, string? appName, string? title, DateTimeOffset start, DateTimeOffset end)
        {
            if (appName == null || start == default) return null;
            var duration = end - start;
            if (duration.TotalSeconds < 1) return null;

            Log.Debug("段快照: {App} / {Title}，累计 {Duration:F1}s", appName, title, duration.TotalSeconds);
            return new ActivitySegmentItem
            {
                // Id 即活动身份（ADR-018）：同段跨快照复用，服务端按 Id upsert 收敛。
                Id = id,
                Source = ActivitySources.System,
                IdentityKey = SystemIdentity.Key(appName, title),
                AppName = appName,
                Title = title,
                StartTime = start,
                EndTime = end
            };
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
            _snapshotCts?.Cancel();
            _snapshotCts?.Dispose();
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
