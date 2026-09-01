using System.Diagnostics;
using System.Security.Cryptography;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Microsoft.Extensions.Hosting;

namespace Heartbeat.Collection.Headless.Tests;

/// <summary>
/// Headless Hub 从宿主挂载的只读 Package 来源安装出运行用 Installation，并在其上建立稳定 Instance。
/// 全程只用 <see cref="HeadlessFleetManager"/> 的公开接口驱动。
/// </summary>
public sealed class HeadlessPackageInstallationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"heartbeat-headless-installation-{Guid.NewGuid():N}");

    [Fact]
    public async Task Start_InstallsPackageSourceIntoDataDirectoryAndBuildsInstanceFromTheInstalledCopy()
    {
        var source = await CreateSourcePackageAsync("package-source");
        var dataDirectory = Path.Combine(_root, "data");
        var sourceContentBefore = Fingerprint(source);
        var sourcePackage = LocalCollectorPackage.Load(source);

        using var manager = new HeadlessFleetManager(Fleet(dataDirectory, source));
        try
        {
            var status = await StartAndWaitForInstanceAsync(manager);
            Assert.NotNull(status.CollectorInstanceId);
            Assert.NotEqual(Guid.Empty, status.CollectorInstanceId!.Value);
            Assert.True(File.Exists(Path.Combine(
                dataDirectory,
                "collector-packages",
                sourcePackage.Manifest.PackageId,
                sourcePackage.Manifest.Version,
                sourcePackage.PackageContentHash["sha256:".Length..],
                "collector-manifest.json")));
            Assert.Equal(sourceContentBefore, Fingerprint(source));
        }
        finally
        {
            await StopAsync(manager);
        }

        Assert.Equal(sourceContentBefore, Fingerprint(source));
    }

    /// <summary>
    /// 生产上 Package 来源是只读 bind mount。Windows 没有等价的 Unix file mode 前提，直接跳过
    /// （xunit 2.9 没有动态 Skip，只能返回）。
    /// </summary>
    [Fact]
    public async Task Start_ReadOnlyUnixPackageSource_StillInstallsAndBuildsInstance()
    {
        if (OperatingSystem.IsWindows())
            return;

        var source = await CreateSourcePackageAsync("readonly-source");
        var dataDirectory = Path.Combine(_root, "data");
        var sourceContentBefore = Fingerprint(source);
        SetSourceWritable(source, writable: false);
        try
        {
            Assert.False(new DirectoryInfo(source).UnixFileMode.HasFlag(UnixFileMode.UserWrite));
            Assert.False(new FileInfo(Path.Combine(source, "collector-manifest.json"))
                .UnixFileMode.HasFlag(UnixFileMode.UserWrite));
            using var manager = new HeadlessFleetManager(Fleet(dataDirectory, source));
            try
            {
                var status = await StartAndWaitForInstanceAsync(manager);
                Assert.NotNull(status.CollectorInstanceId);
                var installations = new CollectorPackageInstallations(
                    Path.Combine(dataDirectory, "collector-packages"));
                var installation = Assert.Single(installations.List());
                Assert.Equal(LocalCollectorPackage.Load(source).PackageContentHash,
                    installation.Reference.PackageContentHash);
                Assert.Equal(sourceContentBefore, Fingerprint(source));
            }
            finally
            {
                await StopAsync(manager);
            }
        }
        finally
        {
            SetSourceWritable(source, writable: true);
        }
    }

    [Fact]
    public async Task Restart_OnTheSameDataDirectory_KeepsTheCollectorInstanceIdentity()
    {
        var source = await CreateSourcePackageAsync("restart-source");
        var dataDirectory = Path.Combine(_root, "data");

        Guid first;
        using (var manager = new HeadlessFleetManager(Fleet(dataDirectory, source)))
        {
            try
            {
                first = (await StartAndWaitForInstanceAsync(manager)).CollectorInstanceId!.Value;
            }
            finally
            {
                await StopAsync(manager);
            }
        }

        using var restarted = new HeadlessFleetManager(Fleet(dataDirectory, source));
        try
        {
            Assert.Equal(first, (await StartAndWaitForInstanceAsync(restarted)).CollectorInstanceId);
        }
        finally
        {
            await StopAsync(restarted);
        }
    }

    private HeadlessFleetOptions Fleet(string dataDirectory, string packageDirectory) => new()
    {
        ApiKey = "test-key",
        DataDirectory = dataDirectory,
        // 单个测试生命周期内不应触发周期性上传：后端不存在，上传失败只会拖慢并干扰测试。
        UploadIntervalSeconds = 3600,
        Management = new HeadlessManagementOptions
        {
            OwnerSubject = "owner-1",
            Authority = "https://auth.example.test",
            Issuer = "https://auth.example.test/",
            ClientId = "heartbeat-web"
        },
        Instances =
        [
            new HeadlessManagedInstanceOptions
            {
                InstanceKey = "managed-reference",
                PackageDirectory = packageDirectory,
                SubjectId = Guid.Parse("0198d5eb-fc31-7d7b-8bf0-c2d009ec8123"),
                SubjectKind = SubjectKind.Account,
                SubjectName = "Managed reference account",
                StartupTimeoutSeconds = 30,
                DrainGraceSeconds = 2
            }
        ]
    };

    private static async Task<HeadlessSubjectStatusResponse> StartAndWaitForInstanceAsync(
        HeadlessFleetManager manager)
    {
        await ((IHostedService)manager).StartAsync(CancellationToken.None);
        // BackgroundService 不保证 ExecuteAsync（其中包含 Initialize）在 StartAsync 返回前跑完，
        // 所以这里等 Instance 事实出现；初始化失败会以 ExecuteTask 的原样异常呈现，而不是超时。
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (true)
        {
            if (manager.ExecuteTask is { IsFaulted: true } faulted)
                await faulted;
            if (manager.Snapshot() is { Count: > 0 } snapshot)
                return Assert.Single(snapshot);
            await Task.Delay(20, timeout.Token);
        }
    }

    private static async Task StopAsync(HeadlessFleetManager manager)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await manager.StopAsync(timeout.Token);
    }

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

    private static SortedDictionary<string, string> Fingerprint(string directory)
    {
        var content = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            content[Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/')] =
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        }
        return content;
    }

    private static void SetSourceWritable(string directory, bool writable)
    {
        if (OperatingSystem.IsWindows())
            return;
        const UnixFileMode writeBits =
            UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
        var root = new DirectoryInfo(directory);
        var entries = new List<FileSystemInfo>(root.EnumerateFileSystemInfos("*", SearchOption.AllDirectories));
        if (writable)
            entries.Insert(0, root);
        else
            entries.Add(root);
        foreach (var entry in entries)
        {
            entry.UnixFileMode = writable
                ? entry.UnixFileMode | UnixFileMode.UserWrite
                : entry.UnixFileMode & ~writeBits;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
