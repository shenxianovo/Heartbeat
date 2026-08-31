using System.Collections.Concurrent;
using Heartbeat.Collector.System.Collection;

namespace Heartbeat.Collector.System.Tests.Collection;

public sealed class OrderedDeferredHandoffTests
{
    [Fact]
    public async Task CompleteKeepsTheGateClosedUntilOlderDeferredItemsAreReplayed()
    {
        var handoff = new OrderedDeferredHandoff<string>();
        var observed = new ConcurrentQueue<string>();
        using var olderReplayEntered = new ManualResetEventSlim();
        using var releaseOlderReplay = new ManualResetEventSlim();
        Assert.True(handoff.TryBegin());
        Assert.True(handoff.TryDefer(() => "B@t1"));

        var completion = Task.Run(() => handoff.Complete(item =>
        {
            if (item == "B@t1")
            {
                olderReplayEntered.Set();
                releaseOlderReplay.Wait(TimeSpan.FromSeconds(5));
            }
            observed.Enqueue(item);
        }));
        Assert.True(olderReplayEntered.Wait(TimeSpan.FromSeconds(2)));

        if (!handoff.TryDefer(() => "C@t2"))
            observed.Enqueue("C@t2");
        releaseOlderReplay.Set();
        await completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(["B@t1", "C@t2"], observed);
    }
}
