using Heartbeat.Hub.Core.Configuration;
using Serilog;
using System.Net.Http.Headers;

namespace Heartbeat.Hub.Core.Auth
{
    /// <summary>
    /// 自动为每个请求注入 Bearer JWT、X-Hardware-Id 和 X-Device-Name 头。
    /// 通过 TokenManager 获取/缓存 access token。
    /// </summary>
    public class BearerTokenHandler(
        IAccessTokenProvider tokenProvider,
        IDeviceIdentity deviceIdentity) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Inject Bearer token
            var token = await tokenProvider.GetAccessTokenAsync(cancellationToken);
            if (!string.IsNullOrEmpty(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            else
            {
                Log.Warning("No access token available; request will be sent without authorization.");
            }

            // Inject X-Hardware-Id header
            request.Headers.TryAddWithoutValidation("X-Hardware-Id", deviceIdentity.HardwareId);

            // Inject X-Device-Name header (URL-encoded to support non-ASCII chars)
            request.Headers.TryAddWithoutValidation("X-Device-Name", Uri.EscapeDataString(deviceIdentity.DeviceName));

            var response = await base.SendAsync(request, cancellationToken);

            // On 401, invalidate cached token so next request will re-exchange
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                Log.Warning("Received 401 Unauthorized; invalidating cached token.");
                tokenProvider.InvalidateToken();
            }

            return response;
        }
    }
}
