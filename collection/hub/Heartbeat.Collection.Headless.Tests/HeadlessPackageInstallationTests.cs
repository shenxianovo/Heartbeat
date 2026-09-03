using System.Diagnostics;
using System.Text.Json;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Core.DTOs.Segments;
using Microsoft.Extensions.Hosting;

namespace Heartbeat.Collection.Headless.Tests;

public sealed class HeadlessPackageInstallationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"heartbeat-headless-runtime-owner-{Guid.NewGuid():N}");

    [Fact]
    public async Task Restart_UsesRuntimeInstanceAndExactInstallationWithoutPackageSourceOrWeb()
    {
        var source = await CreateSourcePackageAsync();
        var data = Path.Combine(_root, "data");
        var installation = new CollectorPackageInstallations(Path.Combine(data, "collector-packages"))
            .Install(source);
        Guid instanceId;
        using (var runtime = CollectorRuntime.Open(
                   Path.Combine(data, "collector-runtime.json"),
                   new NullSegmentSink()))
        {
            instanceId = runtime.CreateInstance(
                installation.Package,
                new SubjectReference(Guid.CreateVersion7(), SubjectKind.Account),
                new CollectorInstanceSpec(1, 1, JsonSerializer.SerializeToElement(new { })),
                "default").CollectorInstanceId;
        }
        Directory.Delete(source, recursive: true);

        using var manager = new HeadlessFleetManager(Fleet(data));
        await ((IHostedService)manager).StartAsync(CancellationToken.None);
        await manager.Initialized.WaitAsync(TimeSpan.FromSeconds(60));
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (true)
            {
                var status = Assert.Single(manager.InstalledSnapshot());
                if (status.Phase == CollectorRuntimePhase.Ready.ToString())
                {
                    Assert.True(status.IsInstalled);
                    Assert.Equal("heartbeat.collector.reference-managed", status.PackageId);
                    Assert.Equal("Reference Managed Collector", status.DisplayName);
                    break;
                }
                Assert.NotEqual(CollectorRuntimePhase.Failed.ToString(), status.Phase);
                await Task.Delay(20, timeout.Token);
            }
            Assert.False(File.Exists(Path.Combine(data, "headless-instance-map.json")));
            using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "collector-runtime.json")));
            Assert.Equal(instanceId, state.RootElement.GetProperty("instances")[0]
                .GetProperty("collectorInstanceId").GetGuid());
        }
        finally
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await manager.StopAsync(timeout.Token);
        }
    }

    [Fact]
    public async Task InstallAndUninstall_GenericMarketplaceOwnsTheCompleteDefaultInstanceLifecycle()
    {
        var source = await CreateSourcePackageAsync();
        var data = Path.Combine(_root, "lifecycle-data");
        var sourcePackage = LocalCollectorPackage.Load(source);
        var manager = new HeadlessFleetManager(Fleet(data))
        {
            MarketplaceFactory = installations => new LocalMarketplace(installations, source)
        };
        await ((IHostedService)manager).StartAsync(CancellationToken.None);
        await manager.Initialized.WaitAsync(TimeSpan.FromSeconds(60));
        try
        {
            var installed = await manager.InstallAsync(sourcePackage.Manifest.PackageId);
            Assert.True(installed.IsInstalled);
            Assert.NotNull(installed.CollectorInstanceId);
            Assert.Single(manager.InstalledSnapshot());
            Assert.Single(new CollectorPackageInstallations(
                Path.Combine(data, "collector-packages")).List());

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await manager.UninstallAsync(sourcePackage.Manifest.PackageId, timeout.Token);

            Assert.Empty(manager.InstalledSnapshot());
            Assert.Empty(new CollectorPackageInstallations(
                Path.Combine(data, "collector-packages")).List());
            Assert.False(Directory.Exists(Path.Combine(
                data, "instances", installed.CollectorInstanceId!.Value.ToString("D"))));
        }
        finally
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await manager.StopAsync(timeout.Token);
            manager.Dispose();
        }

        using var runtime = CollectorRuntime.Open(
            Path.Combine(data, "collector-runtime.json"),
            new NullSegmentSink());
        Assert.Empty(runtime.ListInstances());
    }

    private HeadlessFleetOptions Fleet(string dataDirectory) => new()
    {
        ApiKey = "test-key",
        DataDirectory = dataDirectory,
        UploadIntervalSeconds = 3600,
        CollectorRegistryUrl = "https://registry.example.invalid/v1/",
        CollectorDrainGraceSeconds = 2,
        Management = new HeadlessManagementOptions
        {
            OwnerSubject = "owner-1",
            Authority = "https://auth.example.test",
            Issuer = "https://auth.example.test/",
            ClientId = "heartbeat-web"
        }
    };

    private async Task<string> CreateSourcePackageAsync()
    {
        var packageDirectory = Path.Combine(_root, "source");
        Directory.CreateDirectory(packageDirectory);
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ManagedReferenceCollector");
        var executable = Path.Combine(
            fixture,
            OperatingSystem.IsWindows()
                ? "Heartbeat.Collector.Reference.ManagedProcess.exe"
                : "Heartbeat.Collector.Reference.ManagedProcess");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(executable, File.GetUnixFileMode(executable) | UnixFileMode.UserExecute);
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            ArgumentList = { "--create-package", packageDirectory }
        }) ?? throw new InvalidOperationException("Failed to start reference Package builder.");
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        return packageDirectory;
    }

    private sealed class NullSegmentSink : ISegmentSink
    {
        public void Push(List<ActivitySegmentItem> snapshots) { }
    }

    private sealed class LocalMarketplace(
        CollectorPackageInstallations installations,
        string source) : ICollectorPackageMarketplace
    {
        private readonly LocalCollectorPackage _package = LocalCollectorPackage.Load(source);

        public ValueTask<IReadOnlyList<CollectorCatalogItem>> BrowseAsync(
            CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<CollectorCatalogItem>>([
                new CollectorCatalogItem(
                    _package.Manifest.PackageId,
                    _package.Manifest.Presentation!.DisplayName,
                    _package.Manifest.Presentation.Summary,
                    _package.Manifest.Version,
                    new CollectorMarketplaceTarget("test", "test"))
            ]);

        public ValueTask<CollectorPackageInstallation> InstallLatestAsync(
            string packageId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(installations.Install(source));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
