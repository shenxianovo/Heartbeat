using Heartbeat.Agent.Http;
using Heartbeat.Core.DTOs.Devices;
using Serilog;

namespace Heartbeat.Agent.Services
{
    public class StatusUploadService(HeartbeatApiClient apiClient)
    {
        public async Task UploadAsync(string? currentApp)
        {
            var dto = new DeviceStatusRequest
            {
                CurrentApp = currentApp ?? string.Empty
            };

            var result = await apiClient.SendHeartbeatAsync(dto);
            if (result.Success)
                Log.Debug("状态上传成功: {App}", currentApp ?? "(无)");
        }
    }
}
