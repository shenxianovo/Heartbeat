using Heartbeat.Core.DTOs.Apps;
using Heartbeat.Core.DTOs.Devices;
using Heartbeat.Core.DTOs.Input;
using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Core.DTOs.Facts;
using Heartbeat.Core;
using Heartbeat.Collection.Hub.Http;
using System.Net.Http.Json;

namespace Heartbeat.Collection.Hub.Http
{
    public class HeartbeatApiClient(HttpClient http)
    {
        private static string Url(string path)
            => $"{Endpoints.ApiBaseUrl}/api/v1/{path}";

        public async Task<ApiResult> UploadSegmentsAsync(SegmentUploadRequest dto, CancellationToken ct = default)
            => await PostAsync(Url("segments"), dto, "段上传", ct);

        public async Task<ApiResult> UploadInputEventsAsync(InputEventUploadRequest dto, CancellationToken ct = default)
            => await PostAsync(Url("input-events"), dto, "输入事件上传", ct);

        public async Task<ApiResult> UploadFactsAsync(FactUploadRequest dto, CancellationToken ct = default)
            => await PostAsync(Url("facts"), dto, "事实上传", ct);

        public async Task<ApiResult> UploadObservationsAsync(ObservationUploadRequest dto, CancellationToken ct = default)
            => await PostAsync(Url("observations"), dto, "观测上传", ct);

        public async Task<ApiResult> UploadFactsAsync(IReadOnlyList<Heartbeat.Collection.Hub.Upload.FactUploadItem> items, CancellationToken ct = default)
        {
            var native = items.Where(item => item.Observation is not null).ToList();
            if (native.Count > 0)
            {
                var result = await UploadObservationsAsync(Heartbeat.Collection.Hub.Upload.FactUploadItem.ObservationRequest(native), ct);
                if (!result.Success) return result;
            }
            var legacy = items.Where(item => item.Observation is null).ToList();
            return legacy.Count == 0 ? ApiResult.Ok : await UploadFactsAsync(Heartbeat.Collection.Hub.Upload.FactUploadItem.Request(legacy), ct);
        }

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
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = JsonContent.Create(dto)
                };
                request.Headers.TryAddWithoutValidation(
                    HeartbeatProtocol.VersionHeader,
                    HeartbeatProtocol.RequiredVersion);
                var res = await http.SendAsync(request, ct);
                return res.IsSuccessStatusCode ? ApiResult.Ok : ApiResult.Fail(res, context);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return ApiResult.Error(ex, context);
            }
        }
    }
}
