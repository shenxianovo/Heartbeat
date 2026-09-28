using System.Net;
using System.Net.WebSockets;
using System.Text.Json;

namespace Heartbeat.Collector.VRChat;

internal sealed class VRChatPipeline(ClientWebSocket socket, Func<DateTimeOffset> now) : IVRChatEventConnection
{
    public static async Task<IVRChatEventConnection> ConnectAsync(string auth, string userAgent, Func<DateTimeOffset> now, CancellationToken token)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("User-Agent", userAgent);
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(30);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await socket.ConnectAsync(new Uri($"wss://pipeline.vrchat.cloud/?authToken={Uri.EscapeDataString(auth)}"), timeout.Token);
            return new VRChatPipeline(socket, now);
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            var status = socket.HttpStatusCode;
            var retry = VRChatRequestGate.RetryDelay(socket.HttpResponseHeaders?.FirstOrDefault(pair =>
                pair.Key.Equals("Retry-After", StringComparison.OrdinalIgnoreCase)).Value?.FirstOrDefault());
            socket.Dispose();
            if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new VRChatUnauthorizedException("VRChat 事件连接认证失效。");
            throw new VRChatTransientException(status == HttpStatusCode.TooManyRequests
                ? "VRChat 事件连接 HTTP 429" : "VRChat 事件连接失败", retryAfter: retry);
        }
        catch { socket.Dispose(); throw; }
    }

    public async Task<VRChatPresenceUpdate?> ReadAsync(CancellationToken token)
    {
        using var message = new MemoryStream();
        var buffer = new byte[8192];
        ValueWebSocketReceiveResult part;
        do
        {
            part = await socket.ReceiveAsync(buffer.AsMemory(), token);
            if (part.MessageType == WebSocketMessageType.Close)
                throw new VRChatTransientException("VRChat 事件连接已关闭");
            if (part.MessageType != WebSocketMessageType.Text || message.Length + part.Count > 1_048_576)
                throw new VRChatTransientException("VRChat 事件格式异常");
            message.Write(buffer, 0, part.Count);
        } while (!part.EndOfMessage);
        using var document = JsonDocument.Parse(message.ToArray());
        var root = document.RootElement;
        // Pipeline error payloads can contain authToken; never retain or expose their text.
        if (root.TryGetProperty("err", out _)) throw new VRChatTransientException("VRChat 事件服务返回错误");
        return Parse(root, now());
    }

    private static VRChatPresenceUpdate? Parse(JsonElement root, DateTimeOffset at)
    {
        var type = Text(root, "type");
        if (type is not ("user-location" or "friend-location" or "friend-online" or "friend-offline" or "friend-active" or "friend-delete"))
            return null;
        if (!root.TryGetProperty("content", out var content)) return null;
        using var decoded = content.ValueKind == JsonValueKind.String ? JsonDocument.Parse(content.GetString()!) : null;
        return ParseContent(type, decoded?.RootElement ?? content, at);
    }

    private static VRChatPresenceUpdate? ParseContent(string type, JsonElement content, DateTimeOffset at)
    {
        var id = Text(content, "userId") ?? Text(content, "userid");
        if (string.IsNullOrWhiteSpace(id)) return null;
        var user = content.TryGetProperty("user", out var nested) ? nested : default;
        var location = type is "friend-offline" or "friend-active" or "friend-delete" ? null
            : VRChatLocation.Parse(Text(content, "location") ?? Text(user, "location"));
        return new(id, Text(user, "displayName"), location, at, type);
    }

    private static string? Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    public void Dispose() => socket.Dispose();
}
