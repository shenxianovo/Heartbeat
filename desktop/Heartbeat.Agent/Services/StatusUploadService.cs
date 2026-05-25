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

            try
            {
                var res = await apiClient.SendHeartbeatAsync(dto);
                if (!res.IsSuccessStatusCode)
                {
                    var body = await res.Content.ReadAsStringAsync();
                    Log.Warning("状态上传失败 [{StatusCode}]: {Body}", (int)res.StatusCode, body);
                    return;
                }
                Log.Debug("状态上传成功: {App}", currentApp ?? "(无)");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "状态上传失败（网络异常）");
            }
        }
    }
}
