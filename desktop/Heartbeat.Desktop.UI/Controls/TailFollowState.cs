namespace Heartbeat.Desktop.UI.Controls;

internal sealed class TailFollowState
{
    private const double BottomTolerance = 1;

    public bool IsFollowingLatest { get; private set; } = true;

    public void ObserveOffset(double offset, double extent, double viewport)
    {
        var maximumOffset = Math.Max(0, extent - viewport);
        IsFollowingLatest = offset >= maximumOffset - BottomTolerance;
    }

    public void Resume() => IsFollowingLatest = true;
}
