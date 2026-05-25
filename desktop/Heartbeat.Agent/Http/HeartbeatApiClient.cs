using Heartbeat.Agent.Configuration;
using Heartbeat.Core.DTOs.Apps;
using Heartbeat.Core.DTOs.Devices;
using Heartbeat.Core.DTOs.Usage;
using System.Net.Http.Json;

namespace Heartbeat.Agent.Http
{
    /// <summary>
    /// Typed HttpClient for the Heartbeat backend API.
    /// URL 在每次调用时根据当前 ConfigManager.Current.ApiBaseUrl 重新拼接，
    /// 因此用户在运行时修改配置无需重启即可生效。
    /// </summary>
    public class HeartbeatApiClient(HttpClient http, ConfigManager configManager)
    {
        private string Url(string path)
            => $"{configManager.Current.ApiBaseUrl.TrimEnd('/')}/api/v1/{path}";

        public Task<HttpResponseMessage> UploadUsageAsync(UsageUploadRequest dto, CancellationToken ct = default)
            => http.PostAsJsonAsync(Url("usage"), dto, ct);

        public Task<HttpResponseMessage> SendHeartbeatAsync(DeviceStatusRequest dto, CancellationToken ct = default)
            => http.PostAsJsonAsync(Url("devices/heartbeat"), dto, ct);

        public Task<HttpResponseMessage> UploadAppIconAsync(IconUploadRequest dto, CancellationToken ct = default)
            => http.PostAsJsonAsync(Url("apps/icon"), dto, ct);
    }
}
