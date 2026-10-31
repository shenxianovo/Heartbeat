using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Orisumi.Observer.Runtime.Forwarding;

internal sealed class WriteQueue : IAsyncDisposable
{
    internal const int PostLimit = 10 * 1024 * 1024;
    internal const int PutLimit = 8 * 1024 * 1024;
    private readonly string _root;
    private readonly HttpClient _http;
    private readonly Func<bool> _registered;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly ConcurrentQueue<string> _pending = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _sending;
    private long _sequence;
    private string PendingDirectory => Path.Combine(_root, "pending");
    private string RejectedDirectory => Path.Combine(_root, "rejected");
    internal int Pending => _pending.Count;
    internal int Rejected => Directory.Exists(RejectedDirectory) ? Directory.GetFiles(RejectedDirectory, "*.json").Length : 0;
    internal string? LastRejection
    {
        get
        {
            var path = Directory.Exists(RejectedDirectory) ? Directory.GetFiles(RejectedDirectory, "*.json").Order(StringComparer.Ordinal).LastOrDefault() : null;
            if (path is null) return null;
            using var json = JsonDocument.Parse(File.ReadAllBytes(path));
            var detail = json.RootElement.GetProperty("error").GetProperty("detail").GetString();
            return detail is { Length: > 2048 } ? detail[..2048] : detail;
        }
    }
    public WriteQueue(string instanceDirectory, HttpClient http, Func<bool> registered)
    {
        _root = Path.Combine(instanceDirectory, "forwarding"); _http = http; _registered = registered;
        Directory.CreateDirectory(PendingDirectory); Directory.CreateDirectory(RejectedDirectory);
        var sequencePath = Path.Combine(_root, "sequence");
        _sequence = File.Exists(sequencePath) ? long.Parse(File.ReadAllText(sequencePath), CultureInfo.InvariantCulture) : 0;
        foreach (var path in Directory.GetFiles(PendingDirectory).Where(p => !p.EndsWith(".tmp", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            _pending.Enqueue(path);
            _sequence = Math.Max(_sequence, Sequence(path));
        }
        _sending = Task.Run(() => SendLoopAsync(_stop.Token));
    }
    internal async Task EnqueueAsync(byte[] bytes, string? digest, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { Enqueue(bytes, digest); }
        finally { _gate.Release(); }
    }
    private void Enqueue(byte[] bytes, string? digest)
    {
        var name = (++_sequence).ToString("D20", CultureInfo.InvariantCulture) + (digest is null ? ".post.json" : "." + digest[7..] + ".put");
        var path = Path.Combine(PendingDirectory, name);
        Persist(path, bytes);
        _pending.Enqueue(path);
    }
    private static long Sequence(string path) => long.Parse(Path.GetFileName(path).AsSpan(0, 20), CultureInfo.InvariantCulture);
    private static void Persist(string path, byte[] bytes)
    {
        var temporary = path + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { file.Write(bytes); file.Flush(true); }
        File.Move(temporary, path, true);
    }
    private async Task SendLoopAsync(CancellationToken token)
    {
        var delay = 1;
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (!_pending.TryPeek(out var file) || !_registered()) { await Task.Delay(200, token); continue; }
                try
                {
                    if (!await SendAsync(file, token))
                    { await Task.Delay(TimeSpan.FromSeconds(delay), token); delay = Math.Min(delay * 2, 60); continue; }
                    delay = 1;
                }
                catch (Exception error) when (error is HttpRequestException or OperationCanceledException && !token.IsCancellationRequested)
                { await Task.Delay(TimeSpan.FromSeconds(delay), token); delay = Math.Min(delay * 2, 60); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private async Task<bool> SendAsync(string path, CancellationToken token)
    {
        var bytes = await File.ReadAllBytesAsync(path, token);
        var put = path.EndsWith(".put", StringComparison.Ordinal);
        var sequence = Sequence(path);
        var digest = put ? "sha256:" + Path.GetFileName(path)[21..^4] : null;
        JsonObject? original = null;
        using var request = new HttpRequestMessage(put ? HttpMethod.Put : HttpMethod.Post, put ? "contents/" + digest : "writes");
        if (put) request.Content = new ByteArrayContent(bytes);
        else
        {
            try { original = JsonNode.Parse(bytes) as JsonObject ?? throw new JsonException(); }
            catch (Exception error) when (error is JsonException or InvalidOperationException)
            {
                RejectAll(sequence, bytes, null, new { status = 400, detail = "The request must be a JSON object." });
                Complete(path, sequence); return true;
            }
            var body = original.DeepClone().AsObject(); body["sequence"] = sequence;
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }
        if (put) request.Content.Headers.ContentType = new("application/octet-stream");
        using var response = await _http.SendAsync(request, token);
        if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.Unauthorized) return false;
        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(token);
            object error;
            try
            {
                using var json = JsonDocument.Parse(text);
                error = json.RootElement.TryGetProperty("detail", out _) ? json.RootElement.Clone() : new { status = (int)response.StatusCode, detail = text };
            }
            catch (JsonException) { error = new { status = (int)response.StatusCode, detail = text }; }
            RejectAll(sequence, bytes, digest, error);
        }
        else if (!put)
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var results = json.RootElement.GetProperty("results");
            var index = 0;
            foreach (var result in results.EnumerateArray())
            {
                if (result.TryGetProperty("error", out var error))
                    Persist(Path.Combine(RejectedDirectory, $"{sequence:D20}-{index:D6}.json"), JsonSerializer.SerializeToUtf8Bytes(new
                    { observer = original!["observer"], write = original["writes"]![index], error }));
                index++;
            }
        }
        Complete(path, sequence);
        return true;
    }
    private void RejectAll(long sequence, byte[] bytes, string? digest, object error) =>
        Persist(Path.Combine(RejectedDirectory, $"{sequence:D20}-all.json"), JsonSerializer.SerializeToUtf8Bytes(new
        { request = Convert.ToBase64String(bytes), digest, error }));
    private void Complete(string path, long sequence)
    {
        Persist(Path.Combine(_root, "sequence"), Encoding.UTF8.GetBytes(sequence.ToString(CultureInfo.InvariantCulture)));
        File.Delete(path);
        _pending.TryDequeue(out _);
    }
    internal async Task<int> ResendRejectedAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var count = 0;
            foreach (var path in Directory.GetFiles(RejectedDirectory, "*.json").Order(StringComparer.Ordinal))
            {
                using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(path, token));
                var item = json.RootElement;
                if (item.TryGetProperty("write", out var write))
                    Enqueue(JsonSerializer.SerializeToUtf8Bytes(new { observer = item.GetProperty("observer"), writes = new[] { write } }), null);
                else Enqueue(Convert.FromBase64String(item.GetProperty("request").GetString()!), item.GetProperty("digest").GetString());
                File.Delete(path); count++;
            }
            return count;
        }
        finally { _gate.Release(); }
    }
    internal async Task DeleteAsync()
    {
        await _stop.CancelAsync(); await _sending;
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
    public async ValueTask DisposeAsync()
    { await _stop.CancelAsync(); await _sending; _stop.Dispose(); _gate.Dispose(); }
}
