using Heartbeat.Agent.Configuration;
using Heartbeat.Core.DTOs.Apps;
using Heartbeat.Core.DTOs.Devices;
using Heartbeat.Core.DTOs.Input;
using Heartbeat.Core.DTOs.Segments;
using System.Net.Http.Json;

namespace Heartbeat.Agent.Http
{
    public class HeartbeatApiClient(HttpClient http, ConfigManager configManager)
    {
        private string Url(string path)
            => $"{configManager.Current.ApiBaseUrl.TrimEnd('/')}/api/v1/{path}";

        public async Task<ApiResult> UploadSegmentsAsync(SegmentUploadRequest dto, CancellationToken ct = default)
            => await PostAsync(Url("segments"), dto, "段上传", ct);

        public async Task<ApiResult> UploadInputEventsAsync(InputEventUploadRequest dto, CancellationToken ct = default)
            => await PostAsync(Url("input-events"), dto, "输入事件上传", ct);

        public async Task<ApiResult> SendHeartbeatAsync(DeviceStatusRequest dto, CancellationToken ct = default)
            => await PostAsync(Url("devices/heartbeat"), dto, "状态上传", ct);

        public async Task<ApiResult> UploadAppIconAsync(IconUploadRequest dto, CancellationToken ct = default)
            => await PostAsync(Url("apps/icon"), dto, "图标上传", ct);

        private async Task<ApiResult> PostAsync<T>(string url, T dto, string context, CancellationToken ct)
        {
            try
            {
                var res = await http.PostAsJsonAsync(url, dto, ct);
                return res.IsSuccessStatusCode ? ApiResult.Ok : ApiResult.Fail(res, context);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return ApiResult.Error(ex, context);
            }
        }
    }
}
