using System.Threading.Channels;

namespace Heartbeat.Collector.VRChat;

internal sealed record VRChatFeedItem(VRChatPresenceUpdate? Event, VRChatPresenceSnapshot? Snapshot);

// Both producers preserve receipt times. One consumer owns ordering and projection.
internal static class VRChatFeed
{
    public static async Task RunAsync(IVRChatApiSession session, Func<VRChatFeedItem, Task> consume, CancellationToken token)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var connection = await session.ConnectAsync(stop.Token);
        var channel = Channel.CreateBounded<VRChatFeedItem>(1024);
        var events = ProduceAsync(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var update = await connection.ReadAsync(stop.Token);
                if (update is not null) await channel.Writer.WriteAsync(new(update, null), stop.Token);
            }
        }, channel.Writer);
        var snapshots = ProduceAsync(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await channel.Writer.WriteAsync(new(null, await session.GetSnapshotAsync(stop.Token)), stop.Token);
                await Task.Delay(TimeSpan.FromMinutes(5), stop.Token);
            }
        }, channel.Writer);
        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(token)) await consume(item);
        }
        finally
        {
            await stop.CancelAsync();
            connection.Dispose();
            await Task.WhenAll(events, snapshots);
        }
    }

    private static async Task ProduceAsync(Func<Task> run, ChannelWriter<VRChatFeedItem> writer)
    {
        try { await run(); }
        catch (Exception exception) { writer.TryComplete(exception); }
    }
}
