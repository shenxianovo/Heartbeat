using Heartbeat.Collector.VRChat;

namespace Heartbeat.Testing;

internal sealed class IdleVRChatConnection : IVRChatEventConnection
{
    public async Task<VRChatPresenceUpdate?> ReadAsync(CancellationToken token)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        return null;
    }
    public void Dispose() { }
}
