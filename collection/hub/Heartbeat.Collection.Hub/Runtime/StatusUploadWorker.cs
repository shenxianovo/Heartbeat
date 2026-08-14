using Heartbeat.Core.DTOs.Devices;
using Heartbeat.Collection.Hub.Http;
using Heartbeat.Collection.Hub.Presence;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Heartbeat.Collection.Hub.Runtime
{
    /// <summary>
    /// presence 心跳（ADR-021）：周期＝活性，事件＝新鲜度。
    /// 周期 keepalive 供服务端在线判定（窗口 ≥ 2× 间隔，服务端取 90s = 3×）；
    /// 订阅 hub 读模型变更，Current Activity 变化时立刻补推一次。
    /// presence 是易逝信息：无缓存无重试是设计（下一个心跳自然覆盖），不入上传流（ADR-020）。
    /// away 原样上报（__away__）。
    /// </summary>
    public class StatusUploadWorker(
        ICollectionStatus status,
        HeartbeatApiClient apiClient,
        ClientCompatibilityStatus? compatibilityStatus = null) : BackgroundService
    {
        /// <summary>keepalive 节律：代码常量而非配置（ADR-021——没有任何用户决策需要调它）。</summary>
        private static readonly TimeSpan KeepaliveInterval = TimeSpan.FromSeconds(30);

        // 变更信号：容量 1，变更风暴合并为一次唤醒（下一次上传读到的已是最新值）。
        private readonly SemaphoreSlim _changed = new(0, 1);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Log.Information("状态上传服务启动");
            status.CurrentActivityChanged += OnCurrentActivityChanged;
            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        await UploadStatusAsync();
                        await _changed.WaitAsync(KeepaliveInterval, stoppingToken);
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
            finally
            {
                status.CurrentActivityChanged -= OnCurrentActivityChanged;
            }
        }

        private void OnCurrentActivityChanged(CurrentActivity? _)
        {
            try { _changed.Release(); }
            catch (SemaphoreFullException) { /* 已有待处理唤醒，合并 */ }
        }

        private async Task UploadStatusAsync()
        {
            if (compatibilityStatus?.Current.UpdateRequired == true) return;

            var currentActivity = status.CurrentActivity;
            var dto = new DeviceStatusRequest
            {
                CurrentAppIdentityKey = currentActivity?.AppIdentityKey,
                CurrentAppDisplayName = currentActivity?.AppDisplayName
            };

            var result = await apiClient.SendHeartbeatAsync(dto);
            if (result.StatusCode == 426)
                compatibilityStatus?.RequireUpdate(result.ResponseBody);
            if (result.Success)
                Log.Debug("状态上传成功: {App}", currentActivity?.AppIdentityKey ?? "(无)");
        }
    }
}
