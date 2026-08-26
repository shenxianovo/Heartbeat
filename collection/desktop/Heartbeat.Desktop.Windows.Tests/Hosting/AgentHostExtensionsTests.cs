using Heartbeat.Desktop.Windows.Configuration;
using Heartbeat.Desktop.Windows.Collectors;
using Heartbeat.Desktop.Windows.Hosting;
using Heartbeat.Collector.System.Collection;
using Heartbeat.Collector.System.Input;
using Heartbeat.Collector.System.Observations;
using Heartbeat.Collection.Hub.Configuration;
using Heartbeat.Collection.Hub.Presence;
using Heartbeat.Collection.Hub.Runtime;
using Heartbeat.Collection.Hub.Ingest;
using Heartbeat.Collection.Hub.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Heartbeat.Desktop.Windows.Tests.Hosting;

/// <summary>
/// 组合根的注册顺序契约（ADR-020 §6）：托管服务停止顺序为注册逆序，
/// 输入 hook 最后注册（最先停止），system InProcess Binding 紧随其前（随后停止，终态快照入 hub），
/// UploadWorker 在其之前注册（之后停止，终态 drain 带走快照）。
/// 此前该不变量只有注释钉住——重排两行注册就会让每次关机丢掉最后一段。
/// </summary>
public class AgentHostExtensionsTests : IDisposable
{
    private readonly string _tempConfig = Path.Combine(Path.GetTempPath(), $"heartbeat-cfg-{Guid.NewGuid()}.json");
    private readonly string _tempRuntime = Path.Combine(Path.GetTempPath(), $"heartbeat-runtime-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (File.Exists(_tempConfig)) File.Delete(_tempConfig);
        if (Directory.Exists(_tempRuntime)) Directory.Delete(_tempRuntime, recursive: true);
    }

    [Fact]
    public void HostedServices_MonitorRegisteredLast_AfterUploadWorker()
    {
        var services = new ServiceCollection();
        services.AddHeartbeatAgent(new ConfigManager(_tempConfig));
        services.AddSingleton(new SystemCollectorBindingOptions(_tempRuntime));

        // 不 Dispose provider：托管服务未 Start，实例化即足以断言顺序，
        // 避免触发未启动组件的 Stop/Dispose 路径。
        var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().ToList();

        Assert.IsType<WindowsCollectorAppHintResolver>(
            provider.GetRequiredService<ICollectorAppHintResolver>());

        var monitorIndex = hosted.FindIndex(h => h is SystemCollectorHostedService);
        var inputIndex = hosted.FindIndex(h => h is Heartbeat.Desktop.Windows.Services.InputEventCollector);
        var workerIndex = hosted.FindIndex(h => h is UploadWorker);

        Assert.True(monitorIndex >= 0 && inputIndex >= 0 && workerIndex >= 0);
        Assert.Equal(hosted.Count - 1, inputIndex);   // input hook 最先停止，不再向 Event Stream 写入
        Assert.Equal(inputIndex - 1, monitorIndex);   // system Activation 随后 drain
        Assert.True(workerIndex < monitorIndex);      // worker 在 monitor 之后停止，终态 drain 兜底
        Assert.DoesNotContain(hosted, service => service is AppMonitorService);
        Assert.Same(
            provider.GetRequiredService<SystemCollectorProtocolAdapter>(),
            provider.GetRequiredService<ISystemSegmentPublisher>());
    }

    [Fact]
    public async Task Composition_SystemBindingActivatesAndPublishesThroughProtocol()
    {
        var services = new ServiceCollection();
        services.AddHeartbeatAgent(new ConfigManager(_tempConfig));
        var clock = new FakeClock();
        services.AddSingleton<IClock>(clock);
        services.AddSingleton<IDeviceIdentity>(new FakeDeviceIdentity());
        services.AddSingleton<IDesktopObservationSource>(new FakeObservations());
        services.AddSingleton<IInputActivitySignal>(new FakeInputActivitySignal());
        services.AddSingleton(new SystemCollectorBindingOptions(_tempRuntime));
        using var provider = services.BuildServiceProvider();
        var binding = Assert.Single(
            provider.GetServices<IHostedService>().OfType<SystemCollectorHostedService>());

        await binding.StartAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(2));
        provider.GetRequiredService<AppMonitorService>().PushCurrentSnapshot();

        Assert.True(File.Exists(Path.Combine(_tempRuntime, "collector-runtime.json")));
        Assert.Equal("win:code", provider.GetRequiredService<ICollectionStatus>()
            .CurrentActivity!.AppIdentityKey);
        Assert.Contains("system", provider.GetRequiredService<ICollectionStatus>().SourceLastSeen.Keys);
        await binding.StopAsync(CancellationToken.None);
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UnixEpoch;
        public void Advance(TimeSpan duration) => UtcNow += duration;
    }

    private sealed class FakeDeviceIdentity : IDeviceIdentity
    {
        public string HardwareId => "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE";
        public string DeviceName => "Test desktop";
    }

    private sealed class FakeObservations : IDesktopObservationSource
    {
        public event Action<DesktopObservation>? Observation { add { } remove { } }
        public DesktopActivity CurrentActivity => new("win:code", "Code", "main.cs");
        public void Start() { }
        public void Stop() { }
    }

    private sealed class FakeInputActivitySignal : IInputActivitySignal
    {
        public void MarkClick() { }
        public bool ClickedWithin(TimeSpan window) => false;
    }
}
