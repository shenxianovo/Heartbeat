using Heartbeat.Agent.Mac.Configuration;
using Heartbeat.Desktop.Mac;
using Heartbeat.Desktop.UI.Presentation;
using Heartbeat.Hub.Core.Http;
using Heartbeat.Hub.Core.Presence;
using Heartbeat.Hub.Core.Upload;

namespace Heartbeat.Desktop.Mac.Tests;

public sealed class MacDesktopStateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"heartbeat-state-{Guid.NewGuid()}");

    [Fact]
    public void Snapshot_IsTruthfulAboutAppOnlyCapabilities()
    {
        var login = new FakeLoginStart();
        using var state = Build(login);

        Assert.Equal(DesktopCapabilitySnapshot.MacAppOnly, state.Current.Capabilities);
        Assert.False(state.Current.Settings.InputEventRecordingEnabled);

        state.SetInputEventRecordingEnabled(true);

        Assert.False(state.Current.Settings.InputEventRecordingEnabled);
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

    private MacDesktopState Build(FakeLoginStart login)
    {
        var config = new MacConfigManager(new MacAgentPaths(_root));
        return new MacDesktopState(
            config,
            new FakeCollectionStatus(),
            login,
            new ClientCompatibilityStatus(),
            new UploadStatusRegistry());
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
}
