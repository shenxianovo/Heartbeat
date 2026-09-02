using System.Diagnostics;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Microsoft.Extensions.Hosting;

namespace Heartbeat.Collection.Headless.Tests;

/// <summary>
/// Headless Hub 是独立发布单元：它必须能在零 Collector Instance 下启动；某个配置项的 Package 缺失、损坏
/// 或它的 projection pipeline 恢复失败时，只有那一个 Instance 失效，其余 Instance、管理面与 Hub 进程都继续
/// 跑（ADR-048/ADR-049）。管理面只报告真实事实：没建起 Instance 就没有 Package 版本，Activation 失败的原因
/// 归 CollectorRuntime 所有。
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

        await StartAsync(manager);

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

    [Fact]
    public async Task Restart_WithOneUnrestorableInstancePipeline_KeepsTheOtherInstanceRunning()
    {
        var first = await CreateSourcePackageAsync("first-source");
        var second = await CreateSourcePackageAsync("second-source");
        var data = Path.Combine(_root, "data");
        var instances = new[]
        {
            Instance("first", first, "0198d5eb-fc31-7d7b-8bf0-c2d009ec8125"),
            Instance("second", second, "0198d5eb-fc31-7d7b-8bf0-c2d009ec8126")
        };

        // 先跑一轮，让两条 Instance 都有 mapping 与 Installation：第二轮走的才是「恢复既有 mapping」这条路径。
        Guid brokenInstanceId;
        using (var initial = new HeadlessFleetManager(Fleet(data, instances)))
        {
            await StartAsync(initial);
            await WaitForReadyAsync(initial, "Managed first");
            brokenInstanceId = (await WaitForReadyAsync(initial, "Managed second")).CollectorInstanceId!.Value;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await initial.StopAsync(timeout.Token);
        }

        // 让第二条 Instance 的 projection pipeline 恢复失败：它的目录位置被一个文件占住。
        var pipelineDirectory = Path.Combine(data, "instances", brokenInstanceId.ToString("D"));
        Directory.Delete(pipelineDirectory, recursive: true);
        await File.WriteAllTextAsync(pipelineDirectory, "not a directory");

        using var restarted = new HeadlessFleetManager(Fleet(data, instances));
        await StartAsync(restarted);
        try
        {
            var healthy = await WaitForReadyAsync(restarted, "Managed first");
            Assert.NotNull(healthy.CollectorInstanceId);

            var broken = Assert.Single(
                restarted.Snapshot(),
                status => status.SubjectName == "Managed second");
            Assert.Equal(CollectorRuntimePhase.Failed.ToString(), broken.Phase);
            Assert.Null(broken.CollectorInstanceId);
            Assert.False(string.IsNullOrWhiteSpace(broken.StatusDetail));
        }
        finally
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await restarted.StopAsync(timeout.Token);
        }
    }

    [Fact]
    public async Task Restart_WhenTheInstalledCollectorCannotBeExecuted_ReportsTheRuntimeFailure()
    {
        // 去掉可执行位是 POSIX 语义，Windows 上没有等价的最小改动。
        if (OperatingSystem.IsWindows())
            return;

        var source = await CreateSourcePackageAsync("exec-source");
        var data = Path.Combine(_root, "data");
        var instances = new[] { Instance("only", source, "0198d5eb-fc31-7d7b-8bf0-c2d009ec8127") };

        using (var initial = new HeadlessFleetManager(Fleet(data, instances)))
        {
            await StartAsync(initial);
            await WaitForReadyAsync(initial, "Managed only");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await initial.StopAsync(timeout.Token);
        }

        // Installation 的 tree hash 只覆盖内容、不覆盖 unix mode：去掉可执行位不会让 Package 校验失败，
        // 但 Activation 会真的起不来，于是管理面必须透出 Runtime 自己的结构化 Failure。
        var executable = Directory
            .EnumerateFiles(
                Path.Combine(data, "collector-packages"),
                "Heartbeat.Collector.Reference.ManagedProcess",
                SearchOption.AllDirectories)
            .Single();
        File.SetUnixFileMode(
            executable,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        using var restarted = new HeadlessFleetManager(Fleet(data, instances));
        await StartAsync(restarted);
        try
        {
            var failed = await WaitForStatusAsync(
                restarted,
                "Managed only",
                status => !string.IsNullOrWhiteSpace(status.StatusDetail));

            // Instance 身份建起来了，所以这条原因只可能来自 CollectorRuntime 的 Failure，
            // 不是宿主为「没建起来」编的话术。
            Assert.NotNull(failed.CollectorInstanceId);
            Assert.NotNull(failed.PackageVersion);
            Assert.NotNull(failed.PackageContentHash);
            Assert.Contains(": ", failed.StatusDetail!, StringComparison.Ordinal);
        }
        finally
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await restarted.StopAsync(timeout.Token);
        }
    }

    private async Task AssertOnlyTheBrokenInstanceFailsAsync(string healthySource, string brokenSource)
    {
        using var manager = new HeadlessFleetManager(Fleet(
            Path.Combine(_root, "data"),
            Instance("healthy", healthySource, "0198d5eb-fc31-7d7b-8bf0-c2d009ec8123"),
            Instance("broken", brokenSource, "0198d5eb-fc31-7d7b-8bf0-c2d009ec8124")));

        await StartAsync(manager);
        try
        {
            var ready = await WaitForReadyAsync(manager, "Managed healthy");
            Assert.NotNull(ready.CollectorInstanceId);
            Assert.NotNull(ready.PackageVersion);
            Assert.Null(ready.StatusDetail);

            // 坏掉的配置项仍出现在管理面上，带原因，且没有占用任何 Instance 身份。
            var broken = Assert.Single(
                manager.Snapshot(),
                status => status.SubjectName == "Managed broken");
            Assert.Equal(CollectorRuntimePhase.Failed.ToString(), broken.Phase);
            Assert.Null(broken.CollectorInstanceId);
            Assert.False(string.IsNullOrWhiteSpace(broken.StatusDetail));
            // 没建起 Instance 就是"不存在"，不是空字符串。
            Assert.Null(broken.PackageVersion);
            Assert.Null(broken.PackageContentHash);
        }
        finally
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await manager.StopAsync(timeout.Token);
        }
    }

    /// <summary>
    /// BackgroundService 不保证 ExecuteAsync（其中包含 Initialize）在 StartAsync 返回前跑完，
    /// 所以等 Hub 自己的 readiness signal，而不是睡固定时长。
    /// </summary>
    private static async Task StartAsync(HeadlessFleetManager manager)
    {
        await ((IHostedService)manager).StartAsync(CancellationToken.None);
        await manager.Initialized.WaitAsync(TimeSpan.FromSeconds(60));
    }

    private static async Task<HeadlessSubjectStatusResponse> WaitForReadyAsync(
        HeadlessFleetManager manager,
        string subjectName) =>
        await WaitForStatusAsync(
            manager,
            subjectName,
            status =>
            {
                Assert.NotEqual(CollectorRuntimePhase.Failed.ToString(), status.Phase);
                return status.Phase == CollectorRuntimePhase.Ready.ToString();
            });

    private static async Task<HeadlessSubjectStatusResponse> WaitForStatusAsync(
        HeadlessFleetManager manager,
        string subjectName,
        Func<HeadlessSubjectStatusResponse, bool> satisfied)
    {
        // Activation 本身是异步的：readiness signal 只说明 Initialize 跑完了。
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (true)
        {
            if (manager.ExecuteTask is { IsFaulted: true } faulted)
                await faulted;
            var status = manager.Snapshot().FirstOrDefault(item => item.SubjectName == subjectName);
            if (status is not null && satisfied(status))
                return status;
            await Task.Delay(20, timeout.Token);
        }
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
