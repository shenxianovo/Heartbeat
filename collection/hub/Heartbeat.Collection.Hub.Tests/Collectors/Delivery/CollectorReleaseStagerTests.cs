using System.Text.Json;
using System.Text.Json.Nodes;
using Heartbeat.Collection.CollectorRelease;
using Heartbeat.Collection.Hub.Collectors.Delivery;
using Heartbeat.Collection.Hub.Collectors.Packages;

namespace Heartbeat.Collection.Hub.Tests.Collectors.Delivery;

/// <summary>
/// The explicit VRChat release pipeline (issue 02 / ADR-047). Staging turns a built Collector
/// Package into the static tree an operator copies to the server, and it has to be self-proving:
/// length and SHA-256 come from the bytes actually staged, the archive reloads through the Package
/// loader, and the index reads back through the Runtime's own reader.
///
/// The refusals matter more than the happy path. A tag that disagrees with the Package manifest, a
/// foreign PackageId, an unloadable Package and an attempt to republish a Version with different
/// content all have to fail closed, because none of them can be undone once a user has installed it.
/// </summary>
public sealed class CollectorReleaseStagerTests : IDisposable
{
    private static readonly Uri RegistryBaseUri = new("https://registry.example/collector-registry/v1/");

    private readonly string _output = Path.Combine(
        Path.GetTempPath(),
        $"heartbeat-collector-release-{Guid.NewGuid():N}");
    private readonly string _version;

    public CollectorReleaseStagerTests() =>
        _version = VRChatSamplePackage.ReadIdentity(VRChatSamplePackage.PackageDirectory).Version;

    public void Dispose()
    {
        if (Directory.Exists(_output))
            Directory.Delete(_output, recursive: true);
    }

    private CollectorReleaseResult Stage(
        string? tag = null,
        string? packageDirectory = null,
        Uri? registryBaseUri = null,
        string? output = null) =>
        CollectorReleaseStager.Stage(new CollectorReleaseRequest(
            tag ?? $"collector-vrchat/v{_version}",
            packageDirectory ?? VRChatSamplePackage.PackageDirectory,
            registryBaseUri ?? RegistryBaseUri,
            output ?? _output));

    private string CopySamplePackage()
    {
        var copy = Path.Combine(_output, $"package-{Guid.NewGuid():N}");
        Directory.CreateDirectory(copy);
        var source = VRChatSamplePackage.PackageDirectory;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(copy, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        return copy;
    }

    [Fact]
    public void Stage_VRChatPackage_WritesTheStaticTreeTheRuntimeReads()
    {
        var result = Stage();

        Assert.True(result.Succeeded, result.Detail);
        var index = result.Index!;
        Assert.Equal("heartbeat.collector.vrchat", index.PackageId);
        Assert.Equal(_version, index.Version);
        Assert.Equal(
            Path.Combine(_output, "packages", index.PackageId, "current.json"),
            result.IndexPath);
        Assert.Equal(
            Path.Combine(_output, "packages", index.PackageId, "versions", _version, "vrchat.zip"),
            result.ArtifactPath);

        // Length and SHA-256 describe the bytes on disk, not something a human typed.
        var artifact = File.ReadAllBytes(result.ArtifactPath!);
        Assert.Equal(artifact.LongLength, index.Artifact.Length);
        Assert.Equal(
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(artifact)),
            index.Artifact.Sha256);
        Assert.Equal(
            LocalCollectorPackage.Load(VRChatSamplePackage.PackageDirectory).PackageContentHash,
            result.PackageContentHash);
    }

    [Fact]
    public void Stage_ArtifactReloadsThroughThePackageLoader()
    {
        var result = Stage();

        var unpacked = Path.Combine(_output, "unpacked");
        CollectorPackageArchive.Unpack(File.ReadAllBytes(result.ArtifactPath!), unpacked);
        var package = LocalCollectorPackage.Load(unpacked);

        Assert.Equal("heartbeat.collector.vrchat", package.Manifest.PackageId);
        Assert.Equal(_version, package.Manifest.Version);
        Assert.Contains(package.Artifacts, artifact => artifact.ArtifactId == "vrchat.managed");
    }

    [Fact]
    public void Stage_PublishedIndex_MatchesWhatTheFixtureTreeServes()
    {
        // One sample generator: the release output and the Registry fixture must be the same bytes,
        // otherwise the reader tests would be pinning a tree nobody publishes.
        using var fixture = StaticRegistryFixture.PublishVRChat(RegistryBaseUri);
        var result = Stage();

        Assert.Equal(File.ReadAllBytes(fixture.ArtifactPath), File.ReadAllBytes(result.ArtifactPath!));
        Assert.Equal(File.ReadAllBytes(fixture.IndexPath), File.ReadAllBytes(result.IndexPath!));
    }

    [Fact]
    public void Stage_TwiceFromTheSamePackage_IsIdempotent()
    {
        var first = Stage();
        var second = Stage();

        Assert.True(second.Succeeded, second.Detail);
        Assert.Equal(first.Index!.Artifact.Sha256, second.Index!.Artifact.Sha256);
        Assert.Equal(first.Index.Artifact.Length, second.Index.Artifact.Length);
    }

    [Fact]
    public void Stage_SameVersionWithDifferentContent_RefusesToOverwrite()
    {
        var first = Stage();
        File.WriteAllBytes(first.ArtifactPath!, [.. File.ReadAllBytes(first.ArtifactPath!), 0x00]);

        var second = Stage();

        Assert.Equal(CollectorReleaseFailure.VersionAlreadyPublished, second.Failure);
        Assert.Contains("new tag", second.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Stage_TagVersionDisagreesWithThePackageManifest_Fails()
    {
        var result = Stage(tag: "collector-vrchat/v9.9.9");

        Assert.Equal(CollectorReleaseFailure.VersionMismatch, result.Failure);
        Assert.False(Directory.Exists(Path.Combine(_output, "packages")));
    }

    [Theory]
    [InlineData("v0.1.0")]
    [InlineData("collector-vrchat/0.1.0")]
    [InlineData("collector-vrchat/vnope")]
    [InlineData("collector-vrchat/v0.1")]
    [InlineData("collectorvrchat/v0.1.0")]
    public void Stage_MalformedTag_Fails(string tag) =>
        Assert.Equal(CollectorReleaseFailure.InvalidTag, Stage(tag: tag).Failure);

    [Fact]
    public void Stage_CollectorWithoutAReleaseTarget_Fails() =>
        // The System Collector and the Browser Collector do not publish through this pipeline.
        Assert.Equal(
            CollectorReleaseFailure.UnknownReleaseTarget,
            Stage(tag: "collector-system/v1.0.0").Failure);

    [Fact]
    public void Stage_PackageIdDoesNotMatchTheReleaseTarget_Fails()
    {
        using var other = ManagedReferenceCollectorPackage.Create();

        var result = Stage(tag: "collector-vrchat/v1.0.0", packageDirectory: other.Path);

        Assert.Equal(CollectorReleaseFailure.PackageIdMismatch, result.Failure);
    }

    [Fact]
    public void Stage_UnloadablePackage_Fails()
    {
        var broken = CopySamplePackage();
        File.Delete(Path.Combine(broken, "collector-manifest.json"));

        var result = Stage(packageDirectory: broken);

        Assert.Equal(CollectorReleaseFailure.PackageLoadFailed, result.Failure);
    }

    [Fact]
    public void Stage_CorruptedArtifactInsideThePackage_Fails()
    {
        var broken = CopySamplePackage();
        var entrypoint = Path.Combine(
            broken,
            OperatingSystem.IsWindows() ? "Heartbeat.Collector.VRChat.exe" : "Heartbeat.Collector.VRChat");
        var content = File.ReadAllBytes(entrypoint);
        content[content.Length / 2] ^= 0xFF;
        File.WriteAllBytes(entrypoint, content);

        var result = Stage(packageDirectory: broken);

        Assert.Equal(CollectorReleaseFailure.PackageLoadFailed, result.Failure);
    }

    [Fact]
    public void Stage_SelfContainedArtifact_Fails()
    {
        var selfContained = CopySamplePackage();
        var runtimeConfig = Path.Combine(selfContained, "Heartbeat.Collector.VRChat.runtimeconfig.json");
        var document = JsonNode.Parse(File.ReadAllText(runtimeConfig))!.AsObject();
        document["runtimeOptions"]!.AsObject()["includedFrameworks"] = new JsonArray(
            new JsonObject { ["name"] = "Microsoft.NETCore.App", ["version"] = "10.0.0" });
        File.WriteAllText(runtimeConfig, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var result = Stage(packageDirectory: selfContained);

        Assert.Equal(CollectorReleaseFailure.SelfContainedArtifact, result.Failure);
    }

    [Fact]
    public void Stage_PlainHttpRegistryOutsideLoopback_FailsBeforeWritingAnything()
    {
        var result = Stage(registryBaseUri: new Uri("http://registry.example/collector-registry/v1/"));

        Assert.Equal(CollectorReleaseFailure.IndexVerificationFailed, result.Failure);
        Assert.Equal(CollectorRegistryFailureReason.InvalidRegistryBaseUri, result.RegistryReason);
        Assert.False(Directory.Exists(Path.Combine(_output, "packages")));
    }

    [Fact]
    public async Task Stage_ThenServeStatically_IsConsumedByTheRuntimeRegistryClient()
    {
        // End to end for the frozen slice: the pipeline output is read as an index and downloaded
        // over HTTP with its declared length and SHA-256. Installation stays out of scope.
        var served = Path.Combine(_output, "served");
        Directory.CreateDirectory(served);
        using var server = StaticRegistryFixtureServer.Start(served);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = Stage(registryBaseUri: server.BaseUri, output: served);
        Assert.True(result.Succeeded, result.Detail);

        using var httpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var client = new StaticCollectorRegistryClient(httpClient, server.BaseUri);
        var index = await client.GetCurrentAsync("heartbeat.collector.vrchat", timeout.Token);
        Assert.True(index.IsSuccess, index.Detail);

        using var destination = new MemoryStream();
        var download = await client.DownloadArtifactAsync(index.Require(), destination, timeout.Token);

        Assert.True(download.IsSuccess, download.Detail);
        Assert.Equal(result.Index, index.Require());
        Assert.Equal(await File.ReadAllBytesAsync(result.ArtifactPath!, timeout.Token), destination.ToArray());
    }
}
