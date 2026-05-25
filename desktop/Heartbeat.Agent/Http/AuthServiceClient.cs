using Heartbeat.Agent.Configuration;
using System.Net.Http.Json;

namespace Heartbeat.Agent.Http
{
    /// <summary>
    /// Typed HttpClient for the AuthService (API key → JWT exchange).
    /// URL 在每次调用时根据当前 ConfigManager.Current.AuthServiceBaseUrl 重新拼接，
    /// 因此用户在运行时修改配置无需重启即可生效。
    /// </summary>
    public class AuthServiceClient(HttpClient http, ConfigManager configManager)
    {
        private string Url(string path)
            => $"{configManager.Current.AuthServiceBaseUrl.TrimEnd('/')}/api/v1/{path}";

        public Task<HttpResponseMessage> ExchangeApiKeyAsync(object payload, CancellationToken ct = default)
            => http.PostAsJsonAsync(Url("apikeys/exchange"), payload, ct);
    }
}
