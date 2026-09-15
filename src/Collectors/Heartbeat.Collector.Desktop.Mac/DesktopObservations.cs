namespace Heartbeat.Collector.Desktop.Mac;

public enum ActivityChangeKind
{
    Confirmation,
    ApplicationActivated,
    FocusedWindowChanged,
    TitleChanged,
    Recovery,
}

public sealed record DesktopActivitySample(ForegroundApplication Application, string? WindowTitle);

public enum MacAwayReason
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

public abstract record MacSystemObservation
{
    public sealed record Activity(DesktopActivitySample? Sample, ActivityChangeKind Kind) : MacSystemObservation;
    public sealed record AwayEntered(MacAwayReason Reason) : MacSystemObservation;
    public sealed record AwayExited(MacAwayReason Reason, DesktopActivitySample? CurrentActivity) : MacSystemObservation;
    public sealed record Input(DesktopInputObservation Value) : MacSystemObservation;
    public sealed record Capability(CapabilityObservation Value) : MacSystemObservation;
}

public sealed record MacSystemSnapshot(
    DesktopActivitySample? Activity,
    IReadOnlyList<CapabilityObservation> Capabilities);

public interface IMacSystemObservationSource : IDisposable
{
    event Action<MacSystemObservation>? Observation;

    MacSystemSnapshot Capture();
    void RefreshCapabilities();
    void StartObserving();
    void StopObserving();
}
