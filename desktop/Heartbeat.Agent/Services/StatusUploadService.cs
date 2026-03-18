using Heartbeat.Agent.Configuration;
using Heartbeat.Core.DTOs.Devices;
using Serilog;
using System.Net.Http.Json;

namespace Heartbeat.Agent.Services
{
    public class StatusUploadService(ConfigManager configManager, IHttpClientFactory httpClientFactory)
    {
        public async Task UploadAsync(string? currentApp)
        {
            var config = configManager.Current;
            var statusUrl = $"{config.ApiBaseUrl}/devices/heartbeat";

            var dto = new DeviceStatusRequest
            {
                CurrentApp = currentApp ?? string.Empty
            };

            try
            {
                var client = httpClientFactory.CreateClient("HeartbeatApi");
                var res = await client.PostAsJsonAsync(statusUrl, dto);
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
