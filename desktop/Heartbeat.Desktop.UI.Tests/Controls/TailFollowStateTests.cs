using Heartbeat.Desktop.UI.Controls;

namespace Heartbeat.Desktop.UI.Tests.Controls;

public sealed class TailFollowStateTests
{
    [Fact]
    public void NewContent_DoesNotResumeFollowingAfterTheUserScrollsUp()
    {
        var state = new TailFollowState();

        state.ObserveOffset(offset: 120, extent: 500, viewport: 200);

        Assert.False(state.IsFollowingLatest);
    }

    [Fact]
    public void ReachingTheBottom_ResumesFollowingNewContent()
    {
        var state = new TailFollowState();
        state.ObserveOffset(offset: 120, extent: 500, viewport: 200);

        state.ObserveOffset(offset: 500, extent: 700, viewport: 200);

        Assert.True(state.IsFollowingLatest);
    }
}
