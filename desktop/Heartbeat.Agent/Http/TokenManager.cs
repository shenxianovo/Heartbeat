using Heartbeat.Agent.Configuration;
using Serilog;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Heartbeat.Agent.Http
{
    /// <summary>
    /// Manages JWT access tokens obtained by exchanging an AuthService API Key.
    /// Caches the token and automatically refreshes it before expiry.
    /// Thread-safe.
    /// </summary>
    public class TokenManager : IAccessTokenProvider
    {
        private readonly ConfigManager _configManager;
        private readonly AuthServiceClient _authClient;
        private readonly SemaphoreSlim _semaphore = new(1, 1);

        private volatile string? _cachedToken;
        private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

        // Refresh 60 seconds before actual expiry to avoid edge-case failures
        private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);

        public TokenManager(ConfigManager configManager, AuthServiceClient authClient)
        {
            _configManager = configManager;
            _authClient = authClient;

            // Invalidate cached token when config changes (e.g. user changes API key)
            _configManager.ConfigChanged += _ =>
            {
                _cachedToken = null;
                _expiresAt = DateTimeOffset.MinValue;
            };
        }

        /// <summary>
        /// Returns a valid JWT access token, exchanging the API key if necessary.
        /// Returns null if the exchange fails (e.g. invalid key, network error).
        /// </summary>
        public async Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
        {
            // Fast path: token is still valid
            if (_cachedToken != null && DateTimeOffset.UtcNow < _expiresAt - RefreshMargin)
                return _cachedToken;

            await _semaphore.WaitAsync(ct);
            try
            {
                // Double-check after acquiring lock
                if (_cachedToken != null && DateTimeOffset.UtcNow < _expiresAt - RefreshMargin)
                    return _cachedToken;

                return await ExchangeApiKeyAsync(ct);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Force-invalidate the cached token (e.g. on 401 response).
        /// </summary>
        public void InvalidateToken()
        {
            _cachedToken = null;
            _expiresAt = DateTimeOffset.MinValue;
        }

        private async Task<string?> ExchangeApiKeyAsync(CancellationToken ct)
        {
            var config = _configManager.Current;

            if (string.IsNullOrEmpty(config.ApiKey) || string.IsNullOrEmpty(config.AuthServiceBaseUrl))
            {
                Log.Warning("AuthService base URL or API key is not configured; skipping token exchange.");
                return null;
            }

            try
            {
                var payload = new { apiKey = config.ApiKey };
                var response = await _authClient.ExchangeApiKeyAsync(payload, ct);

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(ct);
                    Log.Warning("Token exchange failed: {StatusCode} — {Body}", response.StatusCode, body);
                    return null;
                }

                var result = await response.Content.ReadFromJsonAsync<ExchangeResponse>(ct);
                if (result == null || string.IsNullOrEmpty(result.AccessToken))
                {
                    Log.Warning("Token exchange returned empty token.");
                    return null;
                }

                _cachedToken = result.AccessToken;
                _expiresAt = DateTimeOffset.UtcNow.AddSeconds(result.ExpiresIn);

                Log.Information("Token exchanged successfully, expires in {ExpiresIn}s.", result.ExpiresIn);
                return _cachedToken;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Token exchange failed due to exception.");
                return null;
            }
        }

        private sealed class ExchangeResponse
        {
            [JsonPropertyName("accessToken")]
            public string AccessToken { get; set; } = string.Empty;

            [JsonPropertyName("expiresIn")]
            public int ExpiresIn { get; set; }
        }
    }
}
