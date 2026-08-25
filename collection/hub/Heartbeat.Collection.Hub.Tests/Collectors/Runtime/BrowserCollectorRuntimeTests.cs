using System.Security.Cryptography;
using Heartbeat.Collection.Hub.Collectors;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Configuration;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Core.DTOs.Segments;

namespace Heartbeat.Collection.Hub.Tests.Collectors.Runtime;

public sealed class BrowserCollectorRuntimeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"heartbeat-browser-packages-{Guid.NewGuid():N}");
    private readonly CollectorRuntime _runtime;
    private readonly MutableRegistry _legacyRegistry = new();

    public BrowserCollectorRuntimeTests()
    {
        Directory.CreateDirectory(_root);
        _runtime = CollectorRuntime.Open(
            Path.Combine(_root, "collector-runtime.json"),
            new RecordingSegmentSink());
    }

    [Fact]
    public void Import_VerifiesAndStagesExactPackageWithoutClaimingBrowserIsLoaded()
    {
        var runtime = CreateRuntime();

        var snapshot = runtime.Import(BrowserPackagePath);

        Assert.True(snapshot.IsInstalled);
        Assert.Equal("0.1.0", snapshot.PackageVersion);
        Assert.StartsWith("sha256:", snapshot.PackageContentHash, StringComparison.Ordinal);
        Assert.Equal(BrowserCollectorRuntimeStatus.Waiting, snapshot.RuntimeStatus);
        Assert.True(snapshot.DesiredEnabled);
        Assert.False(snapshot.ReloadRequired);
        Assert.True(File.Exists(Path.Combine(snapshot.SideloadDirectory!, "manifest.json")));
        Assert.DoesNotContain(BrowserPackagePath, snapshot.SideloadDirectory!, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_RejectsUndeclaredExecutablePayloadChangeAndLeavesNoInstallationFact()
    {
        var package = CopyPackage("corrupt");
        File.AppendAllText(Path.Combine(package, "browser-extension", "background.js"), " ");
        var runtime = CreateRuntime();

        Assert.Throws<PackageValidationException>(() => runtime.Import(package));

        Assert.False(runtime.Current.IsInstalled);
    }

    [Fact]
    public void Import_RejectsPackageWithoutOneCurrentExternalHostArtifact()
    {
        var package = CopyPackage("unsupported-platform");
        var manifestPath = Path.Combine(package, "collector-manifest.json");
        var manifest = File.ReadAllText(manifestPath)
            .Replace("[\"windows\", \"macos\"]", "[\"linux\"]", StringComparison.Ordinal);
        File.WriteAllText(manifestPath, manifest);
        var runtime = CreateRuntime();

        var error = Assert.Throws<PackageValidationException>(() => runtime.Import(package));

        Assert.Contains("current platform", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(runtime.Current.IsInstalled);
    }

    [Fact]
    public void DesiredState_UpdatesStableInstanceWithoutRemovingInstallation()
    {
        var browserRuntime = CreateRuntime();
        var installed = browserRuntime.Import(BrowserPackagePath);
        var instanceId = Assert.Single(_runtime.FindInstances(
            BrowserCollectorRuntime.BrowserPackageId,
            MachineSubject)).CollectorInstanceId;

        browserRuntime.SetDesiredEnabled(false);

        var snapshot = browserRuntime.Current;
        var instance = Assert.Single(_runtime.FindInstances(
            BrowserCollectorRuntime.BrowserPackageId,
            MachineSubject));
        Assert.Equal(instanceId, instance.CollectorInstanceId);
        Assert.False(snapshot.DesiredEnabled);
        Assert.True(snapshot.IsInstalled);
        Assert.Equal(installed.PackageContentHash, snapshot.PackageContentHash);
        Assert.False(instance.Spec.Config.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void Update_StagesNewVersionAndRetainsPreviousKnownGoodUntilReload()
    {
        var runtime = CreateRuntime();
        var first = runtime.Import(BrowserPackagePath);
        runtime.MarkReady(first.PackageContentHash!);
        var update = CopyPackage("update");
        RewriteVersion(update, "0.1.0", "0.2.0");

        var second = runtime.Import(update);

        Assert.Equal("0.2.0", second.PackageVersion);
        Assert.Equal("0.1.0", second.PreviousKnownGoodVersion);
        Assert.True(second.ReloadRequired);
        Assert.Equal(BrowserCollectorRuntimeStatus.Ready, second.RuntimeStatus);
        Assert.True(Directory.Exists(first.InstallDirectory));
        Assert.True(Directory.Exists(second.InstallDirectory));
    }

    private BrowserCollectorRuntime CreateRuntime() => new(
        _runtime,
        _legacyRegistry,
        new Device(),
        new BrowserExternalHostBindingOptions(BrowserPackagePath, TimeSpan.FromSeconds(10))
        {
            DataDirectory = _root
        });

    private string CopyPackage(string name)
    {
        var destination = Path.Combine(_root, name);
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(BrowserPackagePath, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(BrowserPackagePath, directory)));
        foreach (var file in Directory.EnumerateFiles(BrowserPackagePath, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(BrowserPackagePath, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        return destination;
    }

    private static void RewriteVersion(string package, string oldVersion, string newVersion)
    {
        var declarationPath = Path.Combine(package, "observation-depth.json");
        var declaration = File.ReadAllText(declarationPath)
            .Replace($"\"collectorVersion\": \"{oldVersion}\"", $"\"collectorVersion\": \"{newVersion}\"", StringComparison.Ordinal);
        File.WriteAllText(declarationPath, declaration);
        var declarationHash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(declarationPath)));

        var manifestPath = Path.Combine(package, "collector-manifest.json");
        var manifest = File.ReadAllText(manifestPath)
            .Replace($"\"version\": \"{oldVersion}\"", $"\"version\": \"{newVersion}\"", StringComparison.Ordinal);
        var hashStart = manifest.IndexOf("\"observationDeclaration\"", StringComparison.Ordinal);
        var oldHashStart = manifest.IndexOf("sha256:", hashStart, StringComparison.Ordinal);
        manifest = string.Concat(manifest.AsSpan(0, oldHashStart), declarationHash, manifest.AsSpan(oldHashStart + 71));
        File.WriteAllText(manifestPath, manifest);
    }

    private SubjectReference MachineSubject => new(
        Guid.Parse(Device.HardwareIdValue),
        SubjectKind.Machine);

    private static string BrowserPackagePath => Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "BrowserCollectorPackage");

    public void Dispose()
    {
        _runtime.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class Device : IDeviceIdentity
    {
        public const string HardwareIdValue = "AAAAAAAA-BBBB-7CCC-8DDD-EEEEEEEEEEEE";
        public string HardwareId => HardwareIdValue;
        public string DeviceName => "test";
    }

    private sealed class MutableRegistry : ICollectorRegistry
    {
        public bool Enabled { get; private set; } = true;
        public IReadOnlyDictionary<string, CollectorRegistration> Snapshot =>
            new Dictionary<string, CollectorRegistration>
            {
                ["browser"] = new(Enabled, 30_000, null, null)
            };

        public CollectorRegistration Touch(string source, int? flushPeriodMs = null) => Snapshot["browser"];
        public void Discover(IEnumerable<string> sources) { }
        public void StoreDeclaration(string source, string declarationJson, int version) { }
    }

    private sealed class RecordingSegmentSink : ISegmentSink
    {
        public void Push(List<ActivitySegmentItem> snapshots) { }
    }
}
