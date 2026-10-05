using System.Globalization;
using VRChat.API.Client;

namespace Heartbeat.Collector.VRChat;

// Shared by the server's VRChat accounts, so enrichment cannot bypass a cooldown.
internal sealed class VRChatRequestGate : IDisposable
{
    private readonly SemaphoreSlim _mutex = new(1);
    private DateTimeOffset _next;

    public async Task<T> RunAsync<T>(Func<Task<T>> request, CancellationToken token, string operation = "REST 请求")
    {
        await _mutex.WaitAsync(token);
        try
        {
            var delay = _next - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
            try { return await request(); }
            catch (ApiException exception) when (exception.ErrorCode is 401 or 403)
            { throw new VRChatUnauthorizedException($"VRChat {operation} HTTP {exception.ErrorCode}"); }
            catch (ApiException exception)
            {
                var retry = RetryDelay(exception.Headers?.FirstOrDefault(pair =>
                    pair.Key.Equals("Retry-After", StringComparison.OrdinalIgnoreCase)).Value?.FirstOrDefault());
                if (exception.ErrorCode == 429) _next = DateTimeOffset.UtcNow + (retry ?? TimeSpan.FromMinutes(2));
                throw new VRChatTransientException($"VRChat {operation} HTTP {exception.ErrorCode}", retryAfter: retry);
            }
            catch (HttpRequestException) { throw new VRChatTransientException("VRChat 网络请求失败"); }
            catch (TaskCanceledException) when (!token.IsCancellationRequested)
            { throw new VRChatTransientException("VRChat 请求超时"); }
        }
        finally
        {
            var spaced = DateTimeOffset.UtcNow.AddSeconds(1);
            if (_next < spaced) _next = spaced;
            _mutex.Release();
        }
    }

    public void Dispose() => _mutex.Dispose();

    internal static TimeSpan? RetryDelay(string? value)
    {
        if (double.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) &&
            double.IsFinite(seconds) && seconds >= 0)
            return TimeSpan.FromSeconds(Math.Min(seconds, TimeSpan.FromDays(7).TotalSeconds));
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
            return date > DateTimeOffset.UtcNow ? date - DateTimeOffset.UtcNow : TimeSpan.Zero;
        return null;
    }
}
