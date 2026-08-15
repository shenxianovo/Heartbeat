using Heartbeat.Desktop.Mac.Configuration;
using Heartbeat.Desktop.Mac;
using Heartbeat.Desktop.UI.Presentation;
using Heartbeat.Collection.Hub.Http;
using Heartbeat.Collection.Hub.Presence;
using Heartbeat.Collection.Hub.Upload;
using Heartbeat.Desktop.Mac.Observations;

namespace Heartbeat.Desktop.Mac.Tests;

public sealed class MacDesktopStateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"heartbeat-state-{Guid.NewGuid()}");

    [Fact]
    public void Snapshot_OffersOptionalTitleDepthWithoutClaimingUnavailablePermission()
    {
        var login = new FakeLoginStart();
        var accessibility = new FakeAccessibility();
        using var state = Build(login, accessibility);

        Assert.Equal(CapabilityAvailability.Unavailable, state.Current.Capabilities.FocusedWindowObservation);
        Assert.True(state.Current.Capabilities.WindowTitleObservationConfigurable);
        Assert.False(state.Current.Settings.WindowTitleObservationEnabled);
        Assert.False(state.Current.Settings.InputEventRecordingEnabled);

        state.SetWindowTitleObservationEnabled(true);
        state.SetInputEventRecordingEnabled(true);

        Assert.True(accessibility.Enabled);
        Assert.False(state.Current.Settings.InputEventRecordingEnabled);
    }

    [Fact]
    public void PermissionRequiredSnapshot_OffersARecoveryPathAndKeepsAppObservationAvailable()
    {
        var accessibility = new FakeAccessibility
        {
            Enabled = true,
            CapabilityState = MacAccessibilityCapabilityState.PermissionRequired
        };
        using var state = Build(new FakeLoginStart(), accessibility);

        Assert.Equal(CapabilityAvailability.Available, state.Current.Capabilities.AppObservation);
        Assert.Equal(CapabilityAvailability.PermissionRequired, state.Current.Capabilities.FocusedWindowObservation);
        Assert.True(state.Current.Capabilities.WindowTitlePermissionActionAvailable);

        state.OpenWindowTitlePermissionSettings();

        Assert.Equal(1, accessibility.OpenSettingsCount);
    }

    [Fact]
    public void SettingsCollectorsAndLoginStart_UsePlatformHeadSeams()
    {
        var login = new FakeLoginStart();
        using var state = Build(login);

        state.SaveSettings(new DesktopSettingsInput(" key ", "Studio", 5));
        state.SetLoginStartEnabled(true);
        state.SetThemeMode(DesktopThemeMode.Dark);

        Assert.Equal(" key ", state.Current.Settings.ApiKey);
        Assert.Equal("Studio", state.Current.Settings.DeviceName);
        Assert.Equal(5, state.Current.Settings.UploadIntervalMinutes);
        Assert.Equal(DesktopThemeMode.Dark, state.Current.Settings.ThemeMode);
        Assert.True(state.Current.LoginStartEnabled);
        Assert.Equal(Environment.ProcessPath, login.EnabledExecutable);
    }

    private MacDesktopState Build(FakeLoginStart login, FakeAccessibility? accessibility = null)
    {
        var config = new MacConfigManager(new MacAgentPaths(_root));
        accessibility ??= new FakeAccessibility();
        accessibility.SetEnabledAction = enabled =>
            config.Update(value => value.WindowTitleObservationEnabled = enabled);
        if (accessibility.Enabled)
            config.Update(value => value.WindowTitleObservationEnabled = true);
        return new MacDesktopState(
            config,
            new FakeCollectionStatus(),
            login,
            new ClientCompatibilityStatus(),
            new UploadStatusRegistry(),
            accessibility);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeLoginStart : IMacLoginStart
    {
        public bool IsEnabled { get; private set; }
        public string? EnabledExecutable { get; private set; }
        public void Enable(string executablePath)
        {
            IsEnabled = true;
            EnabledExecutable = executablePath;
        }
        public void Disable() => IsEnabled = false;
    }

    private sealed class FakeCollectionStatus : ICollectionStatus
    {
        public CurrentActivity? CurrentActivity => null;
        public IReadOnlyDictionary<string, DateTimeOffset> SourceLastSeen { get; } =
            new Dictionary<string, DateTimeOffset>();
        public event Action<CurrentActivity?>? CurrentActivityChanged { add { } remove { } }
    }

    private sealed class FakeAccessibility : IMacAccessibilityEvents
    {
        public event Action<MacAccessibilityObservation>? Observation { add { } remove { } }
        public event Action<MacAccessibilityCapabilityState>? CapabilityChanged;
        public bool Enabled { get; set; }
        public MacAccessibilityCapabilityState CapabilityState { get; set; } =
            MacAccessibilityCapabilityState.Disabled;
        public string? CurrentTitle => null;
        public int OpenSettingsCount { get; private set; }
        public Action<bool>? SetEnabledAction { get; set; }
        public void Start() { }
        public void Stop() { }
        public void SetCurrentApplication(int processIdentifier) { }
        public void SetEnabledFromUser(bool enabled)
        {
            SetEnabledAction?.Invoke(enabled);
            Enabled = enabled;
            CapabilityState = enabled
                ? MacAccessibilityCapabilityState.PermissionRequired
                : MacAccessibilityCapabilityState.Disabled;
            CapabilityChanged?.Invoke(CapabilityState);
        }
        public void OpenPermissionSettingsFromUser() => OpenSettingsCount++;
    }
}
