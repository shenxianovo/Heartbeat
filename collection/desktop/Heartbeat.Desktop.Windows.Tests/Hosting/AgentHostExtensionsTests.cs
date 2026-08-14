using Heartbeat.Desktop.Windows.Configuration;
using Heartbeat.Desktop.Windows.Collectors;
using Heartbeat.Desktop.Windows.Hosting;
using Heartbeat.Collector.System.Collection;
using Heartbeat.Collection.Hub.Runtime;
using Heartbeat.Collection.Hub.Ingest;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Heartbeat.Desktop.Windows.Tests.Hosting;

/// <summary>
/// 组合根的注册顺序契约（ADR-020 §6）：托管服务停止顺序为注册逆序，
/// AppMonitorService 必须最后注册（最先停止，终态快照入 hub），
/// UploadWorker 在其之前注册（之后停止，终态 drain 带走快照）。
/// 此前该不变量只有注释钉住——重排两行注册就会让每次关机丢掉最后一段。
/// </summary>
public class AgentHostExtensionsTests : IDisposable
{
    private readonly string _tempConfig = Path.Combine(Path.GetTempPath(), $"heartbeat-cfg-{Guid.NewGuid()}.json");

    public void Dispose()
    {
        if (File.Exists(_tempConfig)) File.Delete(_tempConfig);
    }

    [Fact]
    public void HostedServices_MonitorRegisteredLast_AfterUploadWorker()
    {
        var services = new ServiceCollection();
        services.AddHeartbeatAgent(new ConfigManager(_tempConfig));

        // 不 Dispose provider：托管服务未 Start，实例化即足以断言顺序，
        // 避免触发未启动组件的 Stop/Dispose 路径。
        var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().ToList();

        Assert.IsType<WindowsCollectorAppHintResolver>(
            provider.GetRequiredService<ICollectorAppHintResolver>());

        var monitorIndex = hosted.FindIndex(h => h is AppMonitorService);
        var workerIndex = hosted.FindIndex(h => h is UploadWorker);

        Assert.True(monitorIndex >= 0 && workerIndex >= 0);
        Assert.Equal(hosted.Count - 1, monitorIndex); // monitor 最后注册 → 最先停止
        Assert.True(workerIndex < monitorIndex);      // worker 在 monitor 之后停止，终态 drain 兜底
    }
}
