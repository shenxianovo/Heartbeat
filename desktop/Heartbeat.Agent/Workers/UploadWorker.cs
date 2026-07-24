using Heartbeat.Agent.Configuration;
using Heartbeat.Agent.Services;
using Heartbeat.Core.DTOs.Input;
using Heartbeat.Core.DTOs.Segments;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Heartbeat.Agent.Workers
{
    /// <summary>
    /// 出网调度（ADR-020/022）：周期性驱动各上传流 drain 一轮。
    /// 退回重注入由流自持（ADR-022），本类只负责节律与图标挂点。
    /// </summary>
    public class UploadWorker(
        IIconUploadService iconService,
        UploadStream<ActivitySegmentItem> segmentStream,
        UploadStream<InputEventItem> inputStream,
        ConfigManager configManager,
        DeclarationUplinkService declarationUplink) : BackgroundService
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
                    await DrainOnceAsync();
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
                await DrainOnceAsync();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "停止时上传剩余数据失败");
            }

            await base.StopAsync(cancellationToken);
        }

        /// <summary>
        /// 驱动两条流各 drain 一轮；段批次顺带触发图标挂点（ADR-020 §6）与声明上行
        /// （ADR-030 §3，未确认的才发，失败不阻塞下一轮）。
        /// 周期循环与 StopAsync 终态 drain 共用此入口；测试直接调用模拟一轮调度。
        /// </summary>
        public async Task DrainOnceAsync()
        {
            try
            {
                await declarationUplink.PushOnceAsync();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "采集器声明上行异常");
            }

            await inputStream.DrainAsync();
            var segments = await segmentStream.DrainAsync();

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
