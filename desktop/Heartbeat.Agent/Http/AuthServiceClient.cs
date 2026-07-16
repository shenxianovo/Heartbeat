using Heartbeat.Agent.Configuration;
using System.Net.Http.Json;

namespace Heartbeat.Agent.Http
{
    /// <summary>
    /// Typed HttpClient for the AuthService (API key → JWT exchange).
    /// Base URL 为编译期常量（<see cref="Endpoints.AuthServiceBaseUrl"/>）。
    /// </summary>
    public class AuthServiceClient(HttpClient http)
    {
        private static string Url(string path)
            => $"{Endpoints.AuthServiceBaseUrl}/api/v1/{path}";

        public Task<HttpResponseMessage> ExchangeApiKeyAsync(object payload, CancellationToken ct = default)
            => http.PostAsJsonAsync(Url("apikeys/exchange"), payload, ct);
    }
}
