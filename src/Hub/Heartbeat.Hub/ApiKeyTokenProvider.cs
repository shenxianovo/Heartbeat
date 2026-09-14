using System.Net.Http.Json;
using System.Text.Json;

namespace Heartbeat.Hub;

public interface IBackendTokenProvider
{
    ValueTask<BackendAccessToken?> GetTokenAsync(CancellationToken cancellationToken = default);

    void Invalidate();
}

public sealed record BackendAccessToken(string Value, Guid OwnerId, DateTimeOffset ExpiresAt);

public sealed class ApiKeyTokenProvider : IBackendTokenProvider, IDisposable
{
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);
    private readonly HttpClient _httpClient;
    private readonly Uri _exchangeUrl;
    private readonly string _apiKey;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _exchangeLock = new(1, 1);
    private volatile BackendAccessToken? _cached;

    public ApiKeyTokenProvider(HttpClient httpClient, Uri authUrl, string apiKey, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(authUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        if (!authUrl.IsAbsoluteUri || authUrl.Scheme is not ("http" or "https") ||
            authUrl.AbsolutePath != "/" || authUrl.Query.Length != 0 ||
            authUrl.Fragment.Length != 0 || authUrl.UserInfo.Length != 0)
        {
            throw new ArgumentException("An Auth HTTP(S) origin is required.", nameof(authUrl));
        }

        _httpClient = httpClient;
        _exchangeUrl = new Uri(authUrl, "api/v1/apikeys/exchange");
        _apiKey = apiKey;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<BackendAccessToken?> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        var cached = _cached;
        if (IsReusable(cached))
        {
            return cached;
        }

        await _exchangeLock.WaitAsync(cancellationToken);
        try
        {
            cached = _cached;
            if (IsReusable(cached))
            {
                return cached;
            }

            return await ExchangeAsync(cancellationToken);
        }
        finally
        {
            _exchangeLock.Release();
        }
    }

    public void Invalidate() => _cached = null;

    public void Dispose() => _exchangeLock.Dispose();

    private bool IsReusable(BackendAccessToken? token) =>
        token is not null && _timeProvider.GetUtcNow() < token.ExpiresAt - RefreshMargin;

    private async Task<BackendAccessToken?> ExchangeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                _exchangeUrl, new ExchangeRequest(_apiKey), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var exchange = await response.Content.ReadFromJsonAsync<ExchangeResponse>(cancellationToken);
            if (exchange is null || string.IsNullOrWhiteSpace(exchange.AccessToken) || exchange.ExpiresIn <= 0)
            {
                return null;
            }

            var parsed = Parse(exchange.AccessToken);
            var now = _timeProvider.GetUtcNow();
            var responseExpiry = now.AddSeconds(exchange.ExpiresIn);
            var expiresAt = parsed.ExpiresAt < responseExpiry ? parsed.ExpiresAt : responseExpiry;
            if (expiresAt <= now)
            {
                return null;
            }

            _cached = new BackendAccessToken(exchange.AccessToken, parsed.OwnerId, expiresAt);
            return _cached;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested &&
                                          exception is HttpRequestException or OperationCanceledException or JsonException or
                                              InvalidDataException or FormatException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static (Guid OwnerId, DateTimeOffset ExpiresAt) Parse(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            throw new FormatException("The exchanged access token is not a JWT.");
        }

        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
        using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
        var root = document.RootElement;
        if (!root.TryGetProperty("sub", out var subject) || subject.ValueKind != JsonValueKind.String ||
            !Guid.TryParse(subject.GetString(), out var ownerId) || ownerId == Guid.Empty ||
            !root.TryGetProperty("exp", out var expiry) || !expiry.TryGetInt64(out var expirySeconds))
        {
            throw new FormatException("The exchanged access token is missing a valid sub or exp.");
        }

        return (ownerId, DateTimeOffset.FromUnixTimeSeconds(expirySeconds));
    }

    private sealed record ExchangeRequest(string ApiKey);

    private sealed record ExchangeResponse(string? AccessToken, long ExpiresIn);
}
