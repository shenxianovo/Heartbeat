using Heartbeat.Agent.Configuration;

namespace Heartbeat.Agent.Http
{
    /// <summary>
    /// 自动为每个请求注入 ApiKey 认证头。
    /// 每次请求动态读取 ConfigManager，支持热重载。
    /// </summary>
    public class ApiKeyDelegatingHandler(ConfigManager configManager) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var apiKey = configManager.Current.ApiKey;
            if (!string.IsNullOrEmpty(apiKey))
            {
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("ApiKey", apiKey);
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
