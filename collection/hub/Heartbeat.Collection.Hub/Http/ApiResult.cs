using Serilog;

namespace Heartbeat.Collection.Hub.Http
{
    public readonly record struct ApiResult(
        bool Success,
        int? StatusCode = null,
        string? ResponseBody = null)
    {
        public static ApiResult Ok => new(true);

        public static ApiResult Fail(HttpResponseMessage response, string context)
        {
            var statusCode = (int)response.StatusCode;
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Log.Warning("{Context}失败 [{StatusCode}]: {Body}", context, statusCode, body);
            return new ApiResult(false, statusCode, body);
        }

        public static ApiResult Error(Exception ex, string context)
        {
            Log.Warning(ex, "{Context}失败（网络异常）", context);
            return new ApiResult(false);
        }
    }
}
