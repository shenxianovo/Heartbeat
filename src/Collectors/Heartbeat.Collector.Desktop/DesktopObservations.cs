namespace Heartbeat.Collector.Desktop;

/// <summary>
/// 一次前台读数：当前前台应用，以及它当前的前台窗口标题（读不到时为 null）。
/// </summary>
public sealed record DesktopActivitySample(ForegroundApplication Application, string? WindowTitle);

public enum DesktopAwayReason
{
    ScreenLocked,
    SessionInactive,
    DisplaySleep,
    SystemSleep,
}

public enum DesktopInputKind
{
    KeyDown,
    KeyUp,
    MouseButtonDown,
    Scroll,
}

public enum ScrollUnit
{
    Line,
    Point,
}

public sealed record DesktopInputObservation(
    DesktopInputKind Kind,
    int Code = 0,
    double DeltaX = 0,
    double DeltaY = 0,
    ScrollUnit? ScrollUnit = null);

public enum ObservationCapability
{
    Application,
    WindowTitle,
    Input,
}

public enum ObservationState
{
    Available,
    PermissionRequired,
    Unavailable,
}

public sealed record CapabilityObservation(
    ObservationCapability Capability,
    ObservationState State,
    string? Reason = null);

public abstract record DesktopObservation
{
    public sealed record Activity(DesktopActivitySample? Sample) : DesktopObservation;
    public sealed record AwayEntered(DesktopAwayReason Reason) : DesktopObservation;
    public sealed record AwayExited(DesktopAwayReason Reason, DesktopActivitySample? CurrentActivity) : DesktopObservation;
    public sealed record Input(DesktopInputObservation Value) : DesktopObservation;
    public sealed record Capability(CapabilityObservation Value) : DesktopObservation;
}

public sealed record DesktopSnapshot(
    DesktopActivitySample? Activity,
    IReadOnlyList<CapabilityObservation> Capabilities);

public interface IDesktopObservationSource : IDisposable
{
    event Action<DesktopObservation>? Observation;

    DesktopSnapshot Capture();
    void RefreshCapabilities();
    void StartObserving();
    void StopObserving();
}
