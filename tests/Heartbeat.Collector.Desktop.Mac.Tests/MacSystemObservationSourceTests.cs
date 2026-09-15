using Heartbeat.Collector.Desktop.Mac.Native;

namespace Heartbeat.Collector.Desktop.Mac.Tests;

public sealed class MacSystemObservationSourceTests
{
    [Fact]
    public void PermissionsDegradeIndependentlyAndRecoverWithoutRestart()
    {
        var workspace = new FakeWorkspace
        {
            FrontmostApplication = new("com.example.App", "/Applications/App", "App", 42),
        };
        var accessibility = new FakeAccessibility { IsAvailable = true, IsProcessTrusted = false };
        var input = new FakeInput { IsAvailable = true, IsAuthorized = false };
        using var source = new MacSystemObservationSource(workspace, accessibility, input);
        var observed = new List<MacSystemObservation>();
        source.Observation += observed.Add;

        source.StartObserving();
        var degraded = source.Capture();

        Assert.Contains(degraded.Capabilities, status =>
            status is { Capability: ObservationCapability.WindowTitle, State: ObservationState.PermissionRequired });
        Assert.Contains(degraded.Capabilities, status =>
            status is { Capability: ObservationCapability.Input, State: ObservationState.PermissionRequired });
        Assert.Equal("com.example.App", degraded.Activity?.Application.Id);

        accessibility.IsProcessTrusted = true;
        accessibility.Title = "Recovered window";
        input.IsAuthorized = true;
        source.RefreshCapabilities();

        Assert.Equal(42, accessibility.ObservedProcessIdentifier);
        Assert.Equal(1, input.StartCount);
        Assert.Contains(observed, item => item is MacSystemObservation.Capability
        {
            Value.Capability: ObservationCapability.WindowTitle,
            Value.State: ObservationState.Available,
        });
        Assert.Equal("Recovered window", source.Capture().Activity?.WindowTitle);
    }

    [Fact]
    public void WorkspaceAwaySignalsAndNativeActivityEventsAreForwarded()
    {
        var workspace = new FakeWorkspace
        {
            FrontmostApplication = new("com.example.App", null, "App", 42),
        };
        var accessibility = new FakeAccessibility
        {
            IsAvailable = true,
            IsProcessTrusted = true,
            Title = "First",
        };
        var input = new FakeInput { IsAvailable = true, IsAuthorized = true };
        using var source = new MacSystemObservationSource(workspace, accessibility, input);
        var observed = new List<MacSystemObservation>();
        source.Observation += observed.Add;
        source.StartObserving();
        observed.Clear();

        workspace.Emit(MacWorkspaceNotification.ScreenLocked);
        workspace.Emit(MacWorkspaceNotification.SystemSleep);
        workspace.Emit(MacWorkspaceNotification.ScreenUnlocked);
        accessibility.Emit(new(
            MacAccessibilityObservationKind.TitleChanged, "Renamed", 42));

        Assert.Contains(observed, item => item is MacSystemObservation.AwayEntered
            { Reason: MacAwayReason.ScreenLocked });
        Assert.Contains(observed, item => item is MacSystemObservation.AwayEntered
            { Reason: MacAwayReason.SystemSleep });
        Assert.Contains(observed, item => item is MacSystemObservation.AwayExited
            { Reason: MacAwayReason.ScreenLocked });
        Assert.Contains(observed, item => item is MacSystemObservation.Activity
            { Kind: ActivityChangeKind.TitleChanged, Sample.WindowTitle: "Renamed" });
    }

    [Fact]
    public void ObserverFailurePublishesUnavailableWithoutStoppingFromFailureCallback()
    {
        var workspace = new FakeWorkspace
        {
            FrontmostApplication = new("com.example.App", null, "App", 42),
        };
        var accessibility = new FakeAccessibility { IsAvailable = true, IsProcessTrusted = true };
        var input = new FakeInput { IsAvailable = true, IsAuthorized = true };
        using var source = new MacSystemObservationSource(workspace, accessibility, input);
        var observed = new List<MacSystemObservation>();
        source.Observation += observed.Add;
        source.StartObserving();

        accessibility.Fail(new InvalidOperationException("observer stopped"));
        input.Fail(new InvalidOperationException("tap stopped"));

        Assert.Equal(0, accessibility.StopCount);
        Assert.Equal(0, input.StopCount);
        Assert.Contains(observed, item => item is MacSystemObservation.Capability
            { Value.State: ObservationState.Unavailable });
    }

    [Fact]
    public void SubscriptionRefreshDoesNotPublishRecoveryWhileTitleReadsStillFail()
    {
        var workspace = new FakeWorkspace
        {
            FrontmostApplication = new("com.example.App", null, "App", 42),
        };
        var accessibility = new FakeAccessibility { IsAvailable = true, IsProcessTrusted = true };
        using var source = new MacSystemObservationSource(workspace, accessibility, new FakeInput());
        var observed = new List<MacSystemObservation>();
        source.Observation += observed.Add;
        source.StartObserving();
        accessibility.ReadFailure = new InvalidOperationException("AX CannotComplete: -25204");
        source.Capture();
        observed.Clear();

        source.RefreshCapabilities();
        var snapshot = source.Capture();

        Assert.DoesNotContain(observed, item => item is MacSystemObservation.Capability
            { Value.Capability: ObservationCapability.WindowTitle, Value.State: ObservationState.Available });
        Assert.Contains(snapshot.Capabilities, status => status is
            { Capability: ObservationCapability.WindowTitle, State: ObservationState.Unavailable });

        accessibility.ReadFailure = null;
        accessibility.Title = "Recovered";
        source.RefreshCapabilities();
        Assert.Equal("Recovered", source.Capture().Activity?.WindowTitle);
        Assert.Contains(observed, item => item is MacSystemObservation.Capability
            { Value.Capability: ObservationCapability.WindowTitle, Value.State: ObservationState.Available });
    }

    [Fact]
    public void SuccessfulTitleReadCannotClaimAnUnattachedObserverIsAvailable()
    {
        var workspace = new FakeWorkspace
        {
            FrontmostApplication = new("com.example.App", null, "App", 42),
        };
        var accessibility = new FakeAccessibility
        {
            IsAvailable = true,
            IsProcessTrusted = true,
            ObservationReady = false,
            Title = "Readable even while subscribing",
        };
        using var source = new MacSystemObservationSource(workspace, accessibility, new FakeInput());
        source.StartObserving();
        var starting = source.Capture();
        Assert.Contains(starting.Capabilities, state => state is
            { Capability: ObservationCapability.WindowTitle, State: ObservationState.Unavailable });

        accessibility.ObservationReady = true;
        var recovered = source.Capture();
        Assert.Contains(recovered.Capabilities, state => state is
            { Capability: ObservationCapability.WindowTitle, State: ObservationState.Available });
    }

    private sealed class FakeWorkspace : IMacWorkspaceNative
    {
        public event Action<string>? Notification;
        public MacApplication? FrontmostApplication { get; set; }
        public void Emit(string name) => Notification?.Invoke(name);
        public void Start(IReadOnlyCollection<string> notificationNames) { }
        public void StopNotifications() { }
        public void Dispose() { }
    }

    private sealed class FakeAccessibility : IMacAccessibilityNative
    {
        public event Action<Exception>? Failed;
        public event Action<MacAccessibilityObservation>? Observation;
        public bool IsAvailable { get; set; }
        public bool IsProcessTrusted { get; set; }
        public string? Title { get; set; }
        public Exception? ReadFailure { get; set; }
        public bool ObservationReady { get; set; } = true;
        public int ObservedProcessIdentifier { get; private set; }
        public int StopCount { get; private set; }
        public void Emit(MacAccessibilityObservation observation) => Observation?.Invoke(observation);
        public void Fail(Exception exception) => Failed?.Invoke(exception);
        public void RequestProcessTrust() { }
        public string? ReadFocusedWindowTitle(int processIdentifier) => ReadFailure is { } failure ? throw failure : Title;
        public void ObserveApplication(int processIdentifier) => ObservedProcessIdentifier = processIdentifier;
        public bool IsObservingApplication(int processIdentifier) => ObservationReady && ObservedProcessIdentifier == processIdentifier;
        public void StopObserving() => StopCount++;
        public void Dispose() { }
    }

    private sealed class FakeInput : IMacInputMonitoringNative
    {
        public event Action<Exception>? Failed;
        public event Action<MacInputObservation>? Observation { add { } remove { } }
        public bool IsAvailable { get; set; }
        public bool IsAuthorized { get; set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public void Fail(Exception exception) => Failed?.Invoke(exception);
        public void RequestAuthorization() { }
        public void StartListening() => StartCount++;
        public void StopListening() => StopCount++;
        public void Dispose() { }
    }
}
