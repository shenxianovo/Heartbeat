using Heartbeat.Agent.Configuration;
using Heartbeat.Agent.Services;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Heartbeat.Agent.Workers
{
    public class UsageUploadWorker(
        AppMonitorService monitor,
        UsageUploadService usageService,
        IconUploadService iconService,
        ConfigManager configManager) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Log.Information("使用记录上传服务启动");

            // 启动时尝试上传缓存
            await usageService.UploadCachedAsync();

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // 每次循环读取最新配置
                    var interval = TimeSpan.FromMinutes(configManager.Current.UploadIntervalMinutes);
                    Log.Debug("使用记录上传间隔: {Interval}", interval);

                    await Task.Delay(interval, stoppingToken);
                    await UploadUsagesAsync();
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "使用记录上传异常");
                }
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            Log.Information("使用记录上传服务正在停止，上传剩余数据...");

            try
            {
                await UploadUsagesAsync();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "停止时上传剩余数据失败");
            }

            await base.StopAsync(cancellationToken);
        }

        private async Task UploadUsagesAsync()
        {
            var usages = monitor.GetAndClearUsages();
            if (usages.Count == 0) return;

            await usageService.UploadAsync(usages);

            var appNames = usages.Select(u => u.AppName).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var appName in appNames)
            {
                _ = iconService.EnsureIconUploadedAsync(appName);
            }
        }
    }
}
