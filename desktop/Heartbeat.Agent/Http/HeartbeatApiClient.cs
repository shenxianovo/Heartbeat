using Heartbeat.Agent.Configuration;
using Heartbeat.Core.DTOs.Apps;
using Heartbeat.Core.DTOs.Devices;
using Heartbeat.Core.DTOs.Input;
using Heartbeat.Core.DTOs.Segments;
using System.Net.Http.Json;

namespace Heartbeat.Agent.Http
{
    public class HeartbeatApiClient(HttpClient http)
    {
        private static string Url(string path)
            => $"{Endpoints.ApiBaseUrl}/api/v1/{path}";

        public async Task<ApiResult> UploadSegmentsAsync(SegmentUploadRequest dto, CancellationToken ct = default)
            => await PostAsync(Url("segments"), dto, "段上传", ct);

        public async Task<ApiResult> UploadInputEventsAsync(InputEventUploadRequest dto, CancellationToken ct = default)
            => await PostAsync(Url("input-events"), dto, "输入事件上传", ct);

        public async Task<ApiResult> SendHeartbeatAsync(DeviceStatusRequest dto, CancellationToken ct = default)
            => await PostAsync(Url("devices/heartbeat"), dto, "状态上传", ct);

        public async Task<ApiResult> UploadAppIconAsync(IconUploadRequest dto, CancellationToken ct = default)
            => await PostAsync(Url("apps/icon"), dto, "图标上传", ct);

        /// <summary>采集器声明批量上行（ADR-030 §3）：body = 声明 JSON 数组（hub 不解析语义，原文转发）。</summary>
        public async Task<ApiResult> UploadCollectorDeclarationsAsync(string declarationsJsonArray, CancellationToken ct = default)
        {
            try
            {
                using var content = new StringContent(declarationsJsonArray, System.Text.Encoding.UTF8, "application/json");
                var res = await http.PostAsync(Url("collectors/declarations"), content, ct);
                return res.IsSuccessStatusCode ? ApiResult.Ok : ApiResult.Fail(res, "采集器声明上行");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return ApiResult.Error(ex, "采集器声明上行");
            }
        }

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
