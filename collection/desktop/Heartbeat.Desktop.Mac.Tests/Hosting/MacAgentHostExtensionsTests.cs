using Heartbeat.Desktop.Mac.Configuration;
using Heartbeat.Desktop.Mac.Collectors;
using Heartbeat.Desktop.Mac.Hosting;
using Heartbeat.Desktop.Mac.Identity;
using Heartbeat.Desktop.Mac.Observations;
using Heartbeat.Desktop.Mac.Native;
using Heartbeat.Desktop.Mac.Input;
using Heartbeat.Collector.System.Collection;
using Heartbeat.Collector.System.Observations;
using Heartbeat.Collection.Hub.Configuration;
using Heartbeat.Collection.Hub.Ingest;
using Heartbeat.Collection.Hub.Presence;
using Heartbeat.Collection.Hub.Runtime;
using Heartbeat.Collection.Hub.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Heartbeat.Desktop.Mac.Tests.Hosting;

public sealed class MacAgentHostExtensionsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"heartbeat-mac-{Guid.NewGuid()}");

    [Fact]
    public void Composition_UsesMacAdapters_AndStopsMonitorBeforeUploadWorker()
    {
        var services = BuildServices();
        using var provider = services.BuildServiceProvider();

        Assert.IsType<MacCollectorAppHintResolver>(
            provider.GetRequiredService<ICollectorAppHintResolver>());
        Assert.IsType<MacDesktopObservationSource>(
            provider.GetRequiredService<IDesktopObservationSource>());

        var hosted = provider.GetServices<IHostedService>().ToList();
        var monitorIndex = hosted.FindIndex(item => item is SystemCollectorHostedService);
        var uploadIndex = hosted.FindIndex(item => item is UploadWorker);
        var inputCollector = provider.GetRequiredService<MacInputEventCollector>();
        var inputIndex = hosted.FindIndex(item => ReferenceEquals(item, inputCollector));
        Assert.Equal(hosted.Count - 1, monitorIndex);
        Assert.True(uploadIndex >= 0 && uploadIndex < monitorIndex);
        Assert.True(inputIndex >= 0 && inputIndex < monitorIndex);
        Assert.DoesNotContain(hosted, service => service is AppMonitorService);
        Assert.Same(
            provider.GetRequiredService<SystemCollectorProtocolAdapter>(),
            provider.GetRequiredService<ISystemSegmentPublisher>());
        Assert.Same(inputCollector, provider.GetRequiredService<IMacInputMonitoringEvents>());
        Assert.Same(inputCollector, provider.GetRequiredService<IInputEventRecordingPolicy>());
    }

    [Fact]
    public void DeviceIdentity_UsesIOPlatformUuid_AndHostnameOnlyAsDefaultName()
    {
        var services = BuildServices();
        using var provider = services.BuildServiceProvider();
        var identity = provider.GetRequiredService<IDeviceIdentity>();

        Assert.Equal("AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE", identity.HardwareId);
        Assert.Equal(Environment.MachineName, identity.DeviceName);

        provider.GetRequiredService<MacConfigManager>().Update(config =>
            config.DeviceName = "Studio Mac");

        Assert.Equal("Studio Mac", identity.DeviceName);
    }

    [Fact]
    public async Task Composition_SystemBindingActivatesAndPublishesThroughProtocol()
    {
        var services = BuildServices();
        using var provider = services.BuildServiceProvider();
        var binding = Assert.Single(
            provider.GetServices<IHostedService>().OfType<SystemCollectorHostedService>());

        await binding.StartAsync(CancellationToken.None);
        provider.GetRequiredService<FakeClock>().Advance(TimeSpan.FromSeconds(2));
        provider.GetRequiredService<AppMonitorService>().PushCurrentSnapshot();

        Assert.True(File.Exists(Path.Combine(_root, "collector-runtime.json")));
        Assert.NotNull(provider.GetRequiredService<ICollectionStatus>().CurrentActivity);
        Assert.Contains("system", provider.GetRequiredService<ICollectionStatus>().SourceLastSeen.Keys);
        await binding.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void FirstLaunch_DisablesInputRecording_AndUsesVersionedPlatformCaches()
    {
        var services = BuildServices();
        using var provider = services.BuildServiceProvider();
        var config = provider.GetRequiredService<MacConfigManager>();

        Assert.False(config.Current.InputEventRecordingEnabled);
        Assert.True(File.Exists(Path.Combine(_root, "config.json")));

        _ = provider.GetRequiredService<Heartbeat.Collection.Hub.Storage.ICache<Heartbeat.Core.DTOs.Segments.ActivitySegmentItem>>();
        _ = provider.GetRequiredService<Heartbeat.Collection.Hub.Storage.ICache<Heartbeat.Core.DTOs.Input.InputEventItem>>();
        Assert.Equal(_root, provider.GetRequiredService<MacAgentPaths>().DataDirectory);
    }

    private ServiceCollection BuildServices()
    {
        Directory.CreateDirectory(_root);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<FakeClock>();
        services.AddSingleton<IClock>(provider => provider.GetRequiredService<FakeClock>());
        services.AddSingleton<IMacWorkspaceNative, FakeWorkspace>();
        services.AddSingleton<IMacAccessibilityNative, FakeAccessibilityNative>();
        services.AddSingleton<IMacInputMonitoringNative, FakeInputMonitoringNative>();
        services.AddSingleton<IMacPlatformUuid>(new StubPlatformUuid(
            "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE"));
        services.AddHeartbeatMacAgent(new MacAgentPaths(_root));
        return services;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeWorkspace : IMacWorkspaceNative
    {
        public event Action<string>? Notification { add { } remove { } }
        public MacApplication? FrontmostApplication =>
            new("com.apple.Terminal", "/System/Applications/Utilities/Terminal.app/Contents/MacOS/Terminal", "Terminal");
        public void Start(IReadOnlyCollection<string> notificationNames) { }
        public void Stop() { }
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UnixEpoch;
        public void Advance(TimeSpan duration) => UtcNow += duration;
    }

    private sealed class StubPlatformUuid(string value) : IMacPlatformUuid
    {
        public string? Read() => value;
    }

    private sealed class FakeAccessibilityNative : IMacAccessibilityNative
    {
        public event Action<MacAccessibilityObservation>? Observation { add { } remove { } }
        public bool IsAvailable => true;
        public bool IsProcessTrusted => false;
        public void RequestProcessTrust() { }
        public string? ReadFocusedWindowTitle(int processIdentifier) => null;
        public void ObserveApplication(int processIdentifier) { }
        public void StopObserving() { }
    }

    private sealed class FakeInputMonitoringNative : IMacInputMonitoringNative
    {
        public event Action<MacInputObservation>? Observation { add { } remove { } }
        public bool IsAvailable => true;
        public bool IsAuthorized => false;
        public void RequestAuthorization() { }
        public void StartListening() { }
        public void StopListening() { }
    }
}
