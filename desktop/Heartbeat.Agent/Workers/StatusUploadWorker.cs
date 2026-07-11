using Heartbeat.Agent.Configuration;
using Heartbeat.Agent.Http;
using Heartbeat.Agent.Services;
using Heartbeat.Core.DTOs.Devices;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Heartbeat.Agent.Workers
{
    /// <summary>
    /// 状态心跳上传。presence 是易逝信息：无缓存无重试是设计（下一个心跳自然覆盖），
    /// 不入上传通道（ADR-020）。数据源为 hub 集面读模型（ADR-021），不伸手进采集器；
    /// away 原样上报（__away__）。
    /// </summary>
    public class StatusUploadWorker(
        ICollectionStatus status,
        HeartbeatApiClient apiClient,
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
            var currentApp = status.CurrentApp;
            var dto = new DeviceStatusRequest { CurrentApp = currentApp ?? string.Empty };

            var result = await apiClient.SendHeartbeatAsync(dto);
            if (result.Success)
                Log.Debug("状态上传成功: {App}", currentApp ?? "(无)");
        }
    }
}
