using Heartbeat.Collection.Hub.Http;
using Heartbeat.Collection.Hub.Presence;
using Heartbeat.Collection.Hub.Upload;
using Heartbeat.Desktop.UI.Presentation;
using Heartbeat.Desktop.Windows.Configuration;
using Heartbeat.Desktop.Windows.Services;

namespace Heartbeat.Desktop.Windows.Tests;

public sealed class WindowsDesktopStateTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"heartbeat-windows-state-{Guid.NewGuid()}");

    [Fact]
    public void OptionalSystemCapabilities_AreEnabledByDefaultAndPersistIndependentChoices()
    {
        Directory.CreateDirectory(_root);
        var config = new ConfigManager(Path.Combine(_root, "config.json"));
        using var state = new WindowsDesktopState(
            config,
            new FakeCollectionStatus(),
            new FakeAutoStart(),
            new ClientCompatibilityStatus(),
            new UploadStatusRegistry());

        Assert.True(state.Current.Capabilities.Get(SystemCapability.WindowActivity).RequestedEnabled);
        Assert.True(state.Current.Capabilities.Get(SystemCapability.InteractionSignal).RequestedEnabled);

        state.SetSystemCapabilityEnabled(SystemCapability.WindowActivity, false);
        state.SetSystemCapabilityEnabled(SystemCapability.InteractionSignal, false);

        Assert.False(state.Current.Capabilities.Get(SystemCapability.WindowActivity).RequestedEnabled);
        Assert.False(state.Current.Capabilities.Get(SystemCapability.InteractionSignal).RequestedEnabled);
        Assert.False(config.Current.WindowActivityCollectionEnabled);
        Assert.False(config.Current.InteractionSignalEnabled);
    }

    [Fact]
    public void ExistingLoginStartRegistration_IsRewrittenToCurrentExecutableOnStartup()
    {
        Directory.CreateDirectory(_root);
        var config = new ConfigManager(Path.Combine(_root, "config.json"));
        var login = new FakeAutoStart(isEnabled: true);

        using var state = new WindowsDesktopState(
            config,
            new FakeCollectionStatus(),
            login,
            new ClientCompatibilityStatus(),
            new UploadStatusRegistry());

        Assert.Equal(Environment.ProcessPath, login.EnabledExecutable);
        Assert.Equal(1, login.EnableCount);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeCollectionStatus : ICollectionStatus
    {
        public CurrentActivity? CurrentActivity => null;
        public IReadOnlyDictionary<string, DateTimeOffset> SourceLastSeen { get; } =
            new Dictionary<string, DateTimeOffset>();
        public event Action<CurrentActivity?>? CurrentActivityChanged { add { } remove { } }
    }

    private sealed class FakeAutoStart : IAutoStartService
    {
        public FakeAutoStart(bool isEnabled = false) => IsEnabled = isEnabled;

        public bool IsEnabled { get; private set; }
        public string? EnabledExecutable { get; private set; }
        public int EnableCount { get; private set; }
        public void Enable(string executablePath)
        {
            IsEnabled = true;
            EnabledExecutable = executablePath;
            EnableCount++;
        }
        public void Disable() => IsEnabled = false;
    }
}
