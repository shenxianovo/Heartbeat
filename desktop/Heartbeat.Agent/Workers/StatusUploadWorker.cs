using Heartbeat.Agent.Configuration;
using Heartbeat.Agent.Services;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Heartbeat.Agent.Workers
{
    public class StatusUploadWorker(
        AppMonitorService monitor,
        StatusUploadService statusService,
        ConfigManager configManager) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Log.Information("状态上传服务启动");

            // 立即上传一次状态
            await UploadStatusAsync();

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // 每次循环读取最新配置
                    var interval = TimeSpan.FromSeconds(configManager.Current.StatusUploadIntervalSeconds);

                    await Task.Delay(interval, stoppingToken);
                    await UploadStatusAsync();
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "状态上传异常");
                }
            }
        }

        private async Task UploadStatusAsync()
        {
            var currentApp = monitor.GetCurrentApp();
            await statusService.UploadAsync(currentApp);
        }
    }
}
