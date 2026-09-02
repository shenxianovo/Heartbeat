using System.Diagnostics;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Microsoft.Extensions.Hosting;

namespace Heartbeat.Collection.Headless.Tests;

/// <summary>
/// Headless Hub 是独立发布单元：它必须能在零 Collector Instance 下启动；某个配置项的 Package 缺失或
/// 损坏时，只有那一个 Instance 失效，其余 Instance、管理面与 Hub 进程都继续跑（ADR-048）。
/// </summary>
public sealed class HeadlessStartupIsolationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"heartbeat-headless-isolation-{Guid.NewGuid():N}");

    [Fact]
    public async Task Start_WithZeroConfiguredInstances_RunsTheHubWithAnEmptyManagementSnapshot()
    {
        using var manager = new HeadlessFleetManager(Fleet(Path.Combine(_root, "data")));

        await ((IHostedService)manager).StartAsync(CancellationToken.None);
        await SettleAsync(manager);

        Assert.Empty(manager.Snapshot());
        Assert.False(manager.ExecuteTask?.IsFaulted);
        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Start_WithOneMissingPackageSource_KeepsTheOtherInstanceRunning()
    {
        var healthy = await CreateSourcePackageAsync("healthy-source");
        var missing = Path.Combine(_root, "not-installed-source");

        await AssertOnlyTheBrokenInstanceFailsAsync(healthy, missing);
    }

    [Fact]
    public async Task Start_WithOneCorruptPackageSource_KeepsTheOtherInstanceRunning()
    {
        var healthy = await CreateSourcePackageAsync("healthy-source");
        var corrupt = await CreateSourcePackageAsync("corrupt-source");
        await File.WriteAllTextAsync(
            Path.Combine(corrupt, "collector-manifest.json"),
            "{ not a manifest");

        await AssertOnlyTheBrokenInstanceFailsAsync(healthy, corrupt);
    }

    private async Task AssertOnlyTheBrokenInstanceFailsAsync(string healthySource, string brokenSource)
    {
        using var manager = new HeadlessFleetManager(Fleet(
            Path.Combine(_root, "data"),
            Instance("healthy", healthySource, "0198d5eb-fc31-7d7b-8bf0-c2d009ec8123"),
            Instance("broken", brokenSource, "0198d5eb-fc31-7d7b-8bf0-c2d009ec8124")));

        await ((IHostedService)manager).StartAsync(CancellationToken.None);
        try
        {
            var ready = await WaitForReadyAsync(manager, "Managed healthy");
            Assert.NotNull(ready.CollectorInstanceId);
            Assert.Null(ready.StatusDetail);

            // 坏掉的配置项仍出现在管理面上，带原因，且没有占用任何 Instance 身份。
            var broken = Assert.Single(
                manager.Snapshot(),
                status => status.SubjectName == "Managed broken");
            Assert.Equal(CollectorRuntimePhase.Failed.ToString(), broken.Phase);
            Assert.Null(broken.CollectorInstanceId);
            Assert.False(string.IsNullOrWhiteSpace(broken.StatusDetail));
        }
        finally
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await manager.StopAsync(timeout.Token);
        }
    }

    private static async Task<HeadlessSubjectStatusResponse> WaitForReadyAsync(
        HeadlessFleetManager manager,
        string subjectName)
    {
        // BackgroundService 不保证 ExecuteAsync（其中包含 Initialize）在 StartAsync 返回前跑完。
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (true)
        {
            if (manager.ExecuteTask is { IsFaulted: true } faulted)
                await faulted;
            var status = manager.Snapshot().FirstOrDefault(item => item.SubjectName == subjectName);
            if (status is not null)
            {
                Assert.NotEqual(CollectorRuntimePhase.Failed.ToString(), status.Phase);
                if (status.Phase == CollectorRuntimePhase.Ready.ToString())
                    return status;
            }
            await Task.Delay(20, timeout.Token);
        }
    }

    /// <summary>
    /// 等到 ExecuteAsync 至少跑过 Initialize：零 Instance 的 Hub 没有任何可轮询的状态变化。
    /// </summary>
    private static async Task SettleAsync(HeadlessFleetManager manager)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!timeout.IsCancellationRequested)
        {
            if (manager.ExecuteTask is { IsFaulted: true } faulted)
                await faulted;
            if (manager.ExecuteTask is not null)
                break;
            await Task.Delay(20, timeout.Token);
        }
        await Task.Delay(100, CancellationToken.None);
        if (manager.ExecuteTask is { IsFaulted: true } settled)
            await settled;
    }

    private static HeadlessManagedInstanceOptions Instance(
        string key,
        string packageDirectory,
        string subjectId) => new()
        {
            InstanceKey = key,
            PackageDirectory = packageDirectory,
            SubjectId = Guid.Parse(subjectId),
            SubjectKind = SubjectKind.Account,
            SubjectName = $"Managed {key}",
            StartupTimeoutSeconds = 30,
            DrainGraceSeconds = 2
        };

    private static HeadlessFleetOptions Fleet(
        string dataDirectory,
        params HeadlessManagedInstanceOptions[] instances) => new()
        {
            ApiKey = "test-key",
            // 单个测试生命周期内不应触发周期性上传：后端不存在，上传失败只会拖慢并干扰测试。
            UploadIntervalSeconds = 3600,
            DataDirectory = dataDirectory,
            Management = new HeadlessManagementOptions
            {
                OwnerSubject = "owner-1",
                Authority = "https://auth.example.test",
                Issuer = "https://auth.example.test/",
                ClientId = "heartbeat-web"
            },
            Instances = instances
        };

    private async Task<string> CreateSourcePackageAsync(string name)
    {
        var packageDirectory = Path.Combine(_root, name);
        Directory.CreateDirectory(packageDirectory);
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ManagedReferenceCollector");
        var executable = Path.Combine(
            fixture,
            OperatingSystem.IsWindows()
                ? "Heartbeat.Collector.Reference.ManagedProcess.exe"
                : "Heartbeat.Collector.Reference.ManagedProcess");
        if (!File.Exists(executable))
            throw new FileNotFoundException($"Managed reference Collector build output was not copied: {executable}");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                executable,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            ArgumentList = { "--create-package", packageDirectory }
        }) ?? throw new InvalidOperationException("Failed to start the reference Package builder.");
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        return packageDirectory;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
