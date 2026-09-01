using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        Assert.Empty(snapshot.Apps);
        Assert.Empty(_runtime.FindInstances(BrowserCollectorRuntime.BrowserPackageId, MachineSubject));
        Assert.False(snapshot.ReloadRequired);
        Assert.True(File.Exists(Path.Combine(snapshot.SideloadDirectory!, "manifest.json")));
        Assert.DoesNotContain(BrowserPackagePath, snapshot.SideloadDirectory!, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureBundledPackageInstalled_PublishesTheSharedInstallationOfThisDataDirectory()
    {
        var runtime = CreateRuntime();

        var snapshot = runtime.EnsureBundledPackageInstalled();

        var installations = new CollectorPackageInstallations(Path.Combine(_root, "collector-packages"));
        var installation = installations.Open(new CollectorPackageReference(
            BrowserCollectorRuntime.BrowserPackageId,
            snapshot.PackageVersion!,
            snapshot.PackageContentHash!));

        Assert.Equal(snapshot.InstallDirectory, installation.Directory);
        Assert.Equal(BrowserCollectorRuntime.BrowserPackageId, installation.Package.Manifest.PackageId);
        Assert.Equal(snapshot.PackageContentHash, installation.Package.PackageContentHash);
        Assert.Equal(
            snapshot.InstallDirectory,
            Assert.Single(installations.List(BrowserCollectorRuntime.BrowserPackageId)).Directory);
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
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        manifest["artifacts"]![0]!["selector"]!["os"] = new JsonArray("unsupported");
        File.WriteAllText(manifestPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        var runtime = CreateRuntime();

        var error = Assert.Throws<PackageValidationException>(() => runtime.Import(package));

        Assert.Contains("current platform", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(runtime.Current.IsInstalled);
    }

    [Fact]
    public void Current_WhenInstalledPayloadChanges_ReportsDegradedInsteadOfCrashingTheHost()
    {
        var runtime = CreateRuntime();
        var installed = runtime.Import(BrowserPackagePath);
        File.AppendAllText(Path.Combine(installed.SideloadDirectory!, "background.js"), " ");

        var snapshot = runtime.Current;

        Assert.True(snapshot.IsInstalled);
        Assert.Equal(BrowserCollectorRuntimeStatus.Degraded, snapshot.RuntimeStatus);
        Assert.Contains("content", snapshot.RuntimeStatusDetail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Startup_WhenInstalledPayloadChanged_ReportsDegradedInsteadOfCrashingTheHost()
    {
        var runtime = CreateRuntime();
        var installed = runtime.Import(BrowserPackagePath);
        File.AppendAllText(Path.Combine(installed.SideloadDirectory!, "background.js"), " ");

        var reloaded = CreateRuntime();
        var snapshot = reloaded.Current;

        Assert.Equal(BrowserCollectorRuntimeStatus.Degraded, snapshot.RuntimeStatus);
        Assert.Contains("content", snapshot.RuntimeStatusDetail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Startup_LegacyPackageStateWithoutSchemaVersion_RemainsReadable()
    {
        var runtime = CreateRuntime();
        var installed = runtime.Import(BrowserPackagePath);
        var statePath = Path.Combine(_root, "browser-package-state.json");
        var legacyJson = File.ReadAllText(statePath)
            .Replace("  \"schemaVersion\": 1,\n", string.Empty, StringComparison.Ordinal);
        File.WriteAllText(statePath, legacyJson);

        var reloaded = CreateRuntime();

        Assert.Equal(installed.PackageContentHash, reloaded.Current.PackageContentHash);
    }

    [Fact]
    public void Current_IgnoresFinderMetadataCreatedInsideTheInstalledPackage()
    {
        var runtime = CreateRuntime();
        var installed = runtime.Import(BrowserPackagePath);
        File.WriteAllBytes(Path.Combine(installed.InstallDirectory!, ".DS_Store"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(installed.SideloadDirectory!, ".DS_Store"), [4, 5, 6]);

        var snapshot = runtime.Current;

        Assert.Equal(BrowserCollectorRuntimeStatus.Waiting, snapshot.RuntimeStatus);
    }

    [Fact]
    public void DesiredState_IsIndependentForEachDiscoveredBrowserApp()
    {
        var browserRuntime = CreateRuntime();
        var installed = browserRuntime.Import(BrowserPackagePath);
        var package = browserRuntime.ResolvePackage("browser.extension", BrowserArtifactHash);
        var chrome = browserRuntime.GetOrCreateAppInstance("chrome", package);
        var edge = browserRuntime.GetOrCreateAppInstance("edge", package);

        browserRuntime.SetAppDesiredEnabled("chrome", false);

        var snapshot = browserRuntime.Current;
        Assert.Equal(2, snapshot.Apps.Count);
        Assert.False(snapshot.DesiredEnabled);
        Assert.True(snapshot.IsInstalled);
        Assert.Equal(installed.PackageContentHash, snapshot.PackageContentHash);
        Assert.False(_runtime.GetInstance(chrome.CollectorInstanceId).Spec.Config.GetProperty("enabled").GetBoolean());
        Assert.True(_runtime.GetInstance(edge.CollectorInstanceId).Spec.Config.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void Update_StagesNewVersionAndRetainsPreviousKnownGoodUntilReload()
    {
        var runtime = CreateRuntime();
        var first = runtime.Import(BrowserPackagePath);
        var package = runtime.ResolvePackage("browser.extension", BrowserArtifactHash);
        _ = runtime.GetOrCreateAppInstance("chrome", package);
        runtime.MarkReady("chrome", first.PackageContentHash!);
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

    [Fact]
    public void Update_SameVersionWithDifferentContent_StagesNewCandidate()
    {
        var runtime = CreateRuntime();
        var first = runtime.Import(BrowserPackagePath);
        var package = runtime.ResolvePackage("browser.extension", BrowserArtifactHash);
        _ = runtime.GetOrCreateAppInstance("chrome", package);
        runtime.MarkReady("chrome", first.PackageContentHash!);
        var update = CopyPackage("same-version-update");
        var manifestPath = Path.Combine(update, "collector-manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        File.WriteAllText(manifestPath, manifest.ToJsonString());

        var second = runtime.Import(update);

        Assert.Equal(first.PackageVersion, second.PackageVersion);
        Assert.NotEqual(first.PackageContentHash, second.PackageContentHash);
        Assert.True(second.ReloadRequired);
        Assert.True(Directory.Exists(first.InstallDirectory));
        Assert.True(Directory.Exists(second.InstallDirectory));
    }

    [Fact]
    public void Startup_BundledContentChangedAtSameVersion_StagesNewCandidate()
    {
        var previousBundle = CopyPackage("previous-bundle");
        var previousManifestPath = Path.Combine(previousBundle, "collector-manifest.json");
        var previousManifest = JsonNode.Parse(File.ReadAllText(previousManifestPath))!;
        File.WriteAllText(previousManifestPath, previousManifest.ToJsonString());
        var previousRuntime = CreateRuntime(previousBundle);
        var previous = previousRuntime.Import(previousBundle);
        var previousPackage = previousRuntime.ResolvePackage("browser.extension", BrowserArtifactHash);
        _ = previousRuntime.GetOrCreateAppInstance("chrome", previousPackage);
        previousRuntime.MarkReady("chrome", previous.PackageContentHash!);

        var reloaded = CreateRuntime();
        var current = reloaded.EnsureBundledPackageInstalled();

        Assert.Equal(previous.PackageVersion, current.PackageVersion);
        Assert.NotEqual(previous.PackageContentHash, current.PackageContentHash);
        Assert.True(current.ReloadRequired);
        Assert.True(Directory.Exists(previous.InstallDirectory));
        Assert.True(Directory.Exists(current.InstallDirectory));
    }

    private BrowserCollectorRuntime CreateRuntime(string? packageDirectory = null) => new(
        _runtime,
        new Device(),
        new BrowserExternalHostBindingOptions(
            packageDirectory ?? BrowserPackagePath,
            TimeSpan.FromSeconds(10))
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
        var declarationPath = Path.Combine(package, "observation-depth.declaration.json");
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

    private static string BrowserArtifactHash => JsonNode.Parse(File.ReadAllText(Path.Combine(
        BrowserPackagePath,
        "browser-extension",
        "collector-artifact-ref.json")))!["artifactHash"]!.GetValue<string>();

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

    private sealed class RecordingSegmentSink : ISegmentSink
    {
        public void Push(List<ActivitySegmentItem> snapshots) { }
    }
}
