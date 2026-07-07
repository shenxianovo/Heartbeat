using Heartbeat.Agent.Configuration;
using Heartbeat.Agent.Services;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Heartbeat.Agent.Workers
{
    /// <summary>
    /// 出网调度（ADR-020 后）：周期性 drain hub 缓冲与输入事件缓冲，cached 先于 fresh。
    /// system 段与插件段同缓冲同路径；旧 usage 管道已退役。
    /// </summary>
    public class UsageUploadWorker(
        IconUploadService iconService,
        InputEventCollector inputCollector,
        InputEventUploadService inputUploadService,
        SegmentIngestService segmentIngest,
        SegmentUploadService segmentUploadService,
        ConfigManager configManager) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Log.Information("上传调度服务启动");
            LogLegacyUsageCacheIfPresent();

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // 每次循环读取最新配置
                    var interval = TimeSpan.FromMinutes(configManager.Current.UploadIntervalMinutes);
                    Log.Debug("上传间隔: {Interval}", interval);

                    await Task.Delay(interval, stoppingToken);

                    // 先尝试上传缓存的离线记录
                    await inputUploadService.UploadCachedAsync();
                    await segmentUploadService.UploadCachedAsync();

                    await UploadInputEventsAsync();
                    await UploadSegmentsAsync();
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "上传调度异常");
                }
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            Log.Information("上传调度服务正在停止，上传剩余数据...");

            // AppMonitorService 已先停止（注册逆序，ADR-020），其终态快照已在 hub 缓冲中。
            try
            {
                await UploadInputEventsAsync();
                await UploadSegmentsAsync();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "停止时上传剩余数据失败");
            }

            await base.StopAsync(cancellationToken);
        }

        private async Task UploadInputEventsAsync()
        {
            var events = inputCollector.GetAndClearEvents();
            if (events.Count == 0) return;

            await inputUploadService.UploadAsync(events);
        }

        private async Task UploadSegmentsAsync()
        {
            var segments = segmentIngest.GetAndClearSegments();
            if (segments.Count == 0) return;

            await segmentUploadService.UploadAsync(segments);

            // 图标挂点（ADR-020）：从段批次的 AppName 关联提示触发，行为与旧 usage 批次一致。
            var appNames = segments
                .Where(s => !string.IsNullOrEmpty(s.AppName))
                .Select(s => s.AppName!)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var appName in appNames)
            {
                try
                {
                    await iconService.EnsureIconUploadedAsync(appName);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "图标上传失败: {App}", appName);
                }
            }
        }

        /// <summary>旧 usage 离线缓存孤儿化留痕（ADR-020）：文件保留但不再读取，不写迁移代码。</summary>
        private static void LogLegacyUsageCacheIfPresent()
        {
            var legacyPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Heartbeat", "cache.json");
            if (File.Exists(legacyPath))
                Log.Information("检测到已退役的 usage 离线缓存 {Path}（ADR-020），文件保留但不再读取", legacyPath);
        }
    }
}
