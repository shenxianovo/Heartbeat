using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Heartbeat.Collection.Hub.Collectors.Packages;

namespace Heartbeat.Collection.Hub.Tests.Collectors.Packages;

public sealed class CollectorPackageMarketplaceTests : IDisposable
{
    private static readonly Uri Registry = new("https://registry.example/collector-registry/v1/");
    private static string ReferencePackagePath => Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "ReferenceCollectorPackage");
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"heartbeat-marketplace-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task BrowseAndInstall_ValidGenericRelease_InstallsExactPackage()
    {
        var fixture = CreateFixture();
        var marketplace = CreateMarketplace(fixture.Responses);

        var item = Assert.Single(await marketplace.BrowseAsync());
        var installation = await marketplace.InstallLatestAsync(item.PackageId);

        Assert.Equal("Reference Collector", item.DisplayName);
        Assert.Equal(item.PackageId, installation.Reference.PackageId);
        Assert.Equal(item.Version, installation.Reference.Version);
        Assert.True(Directory.Exists(installation.Directory));
    }

    [Fact]
    public async Task InstallLatest_ArtifactHashDoesNotMatch_RejectsWithoutInstallation()
    {
        var fixture = CreateFixture(sha256: "sha256:" + new string('0', 64));
        var marketplace = CreateMarketplace(fixture.Responses);

        var error = await Assert.ThrowsAsync<PackageValidationException>(async () =>
            await marketplace.InstallLatestAsync("heartbeat.collector.reference"));

        Assert.Contains("bytes", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(new CollectorPackageInstallations(Path.Combine(_root, "installations")).List());
    }

    [Fact]
    public async Task InstallLatest_ReleaseLeavesRegistryOrigin_RejectsBeforeRequestingIt()
    {
        var fixture = CreateFixture(releaseUrl: "https://attacker.invalid/release.json");
        var handler = new StaticHttpHandler(fixture.Responses);
        var marketplace = CreateMarketplace(handler);

        await Assert.ThrowsAsync<PackageValidationException>(async () =>
            await marketplace.InstallLatestAsync("heartbeat.collector.reference"));

        Assert.DoesNotContain(handler.Requests, uri => uri.Host == "attacker.invalid");
    }

    [Fact]
    public async Task InstallLatest_PackagePresentationDiffersFromCatalog_RejectsPackage()
    {
        var fixture = CreateFixture(displayName: "Different name");
        var marketplace = CreateMarketplace(fixture.Responses);

        var error = await Assert.ThrowsAsync<PackageValidationException>(async () =>
            await marketplace.InstallLatestAsync("heartbeat.collector.reference"));

        Assert.Contains("presentation", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallLatest_DeclaredLengthDoesNotMatch_RejectsArtifact()
    {
        var fixture = CreateFixture(lengthAdjustment: 1);
        var marketplace = CreateMarketplace(fixture.Responses);

        var error = await Assert.ThrowsAsync<PackageValidationException>(async () =>
            await marketplace.InstallLatestAsync("heartbeat.collector.reference"));

        Assert.Contains("Content-Length", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallLatest_ArtifactIsNotZip_RejectsBeforeInstallation()
    {
        var fixture = CreateFixture(artifactBytes: Encoding.UTF8.GetBytes("not a zip"));
        var marketplace = CreateMarketplace(fixture.Responses);

        var error = await Assert.ThrowsAsync<PackageValidationException>(async () =>
            await marketplace.InstallLatestAsync("heartbeat.collector.reference"));

        Assert.Contains("zip", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallLatest_ArchivePackageIdentityDiffers_RejectsPackage()
    {
        var fixture = CreateFixture(archivePackageId: "heartbeat.collector.different");
        var marketplace = CreateMarketplace(fixture.Responses);

        var error = await Assert.ThrowsAsync<PackageValidationException>(async () =>
            await marketplace.InstallLatestAsync("heartbeat.collector.reference"));

        Assert.Contains("identity", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Browse_DuplicatePackageIds_RejectsCatalog()
    {
        var fixture = CreateFixture();
        var responses = new Dictionary<Uri, byte[]>(fixture.Responses);
        var original = JsonNode.Parse(responses[new Uri(Registry, "catalog.json")])!.AsObject();
        original["packages"]!.AsArray().Add(original["packages"]![0]!.DeepClone());
        responses[new Uri(Registry, "catalog.json")] = JsonSerializer.SerializeToUtf8Bytes(original);
        var marketplace = CreateMarketplace(responses);

        var error = await Assert.ThrowsAsync<PackageValidationException>(async () =>
            await marketplace.BrowseAsync());

        Assert.Contains("duplicate", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Browse_MultipleTargets_SelectsOnlyTheCurrentHostTarget()
    {
        var fixture = CreateFixture();
        var responses = new Dictionary<Uri, byte[]>(fixture.Responses);
        var catalog = JsonNode.Parse(responses[new Uri(Registry, "catalog.json")])!.AsObject();
        catalog["packages"]![0]!["latest"]!.AsArray().Add(new JsonObject
        {
            ["version"] = "2.0.0",
            ["target"] = new JsonObject { ["os"] = "macos", ["arch"] = "arm64" },
            ["releaseUrl"] =
                "https://registry.example/collector-registry/v1/packages/heartbeat.collector.reference/versions/2.0.0/macos-arm64/release.json"
        });
        responses[new Uri(Registry, "catalog.json")] = JsonSerializer.SerializeToUtf8Bytes(catalog);
        var marketplace = CreateMarketplace(responses);

        var item = Assert.Single(await marketplace.BrowseAsync());

        Assert.Equal("1.0.0", item.Version);
        Assert.Equal(new CollectorMarketplaceTarget("linux", "x64"), item.Target);
    }

    [Fact]
    public async Task Browse_DuplicateTargetLatest_RejectsCatalog()
    {
        var fixture = CreateFixture();
        var responses = new Dictionary<Uri, byte[]>(fixture.Responses);
        var catalog = JsonNode.Parse(responses[new Uri(Registry, "catalog.json")])!.AsObject();
        var latest = catalog["packages"]![0]!["latest"]!.AsArray();
        latest.Add(latest[0]!.DeepClone());
        responses[new Uri(Registry, "catalog.json")] = JsonSerializer.SerializeToUtf8Bytes(catalog);
        var marketplace = CreateMarketplace(responses);

        var error = await Assert.ThrowsAsync<PackageValidationException>(async () =>
            await marketplace.BrowseAsync());

        Assert.Contains("duplicate latest target", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private CollectorPackageMarketplace CreateMarketplace(
        IReadOnlyDictionary<Uri, byte[]> responses) =>
        CreateMarketplace(new StaticHttpHandler(responses));

    private CollectorPackageMarketplace CreateMarketplace(HttpMessageHandler handler) => new(
        new HttpClient(handler),
        Registry,
        new CollectorMarketplaceTarget("linux", "x64"),
        new CollectorPackageInstallations(Path.Combine(_root, "installations")));

    private static Fixture CreateFixture(
        string? sha256 = null,
        string? releaseUrl = null,
        string displayName = "Reference Collector",
        long lengthAdjustment = 0,
        byte[]? artifactBytes = null,
        string? archivePackageId = null)
    {
        var archive = artifactBytes ?? CreateArchive(archivePackageId);
        const string packageId = "heartbeat.collector.reference";
        const string version = "1.0.0";
        const string fileName = "heartbeat.collector.reference-1.0.0-linux-x64.zip";
        var release = new Uri(Registry, $"packages/{packageId}/versions/{version}/linux-x64/release.json");
        var artifact = new Uri(release, fileName);
        var catalog = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            packages = new[]
            {
                new
                {
                    packageId,
                    displayName,
                    summary = "A generic package used to prove host-independent Collector lifecycle behavior.",
                    latest = new[]
                    {
                        new
                        {
                            version,
                            target = new { os = "linux", arch = "x64" },
                            releaseUrl = releaseUrl ?? release.AbsoluteUri
                        }
                    }
                }
            }
        });
        var metadata = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            packageId,
            version,
            target = new { os = "linux", arch = "x64" },
            artifact = new
            {
                fileName,
                url = artifact.AbsoluteUri,
                length = archive.LongLength + lengthAdjustment,
                sha256 = sha256 ?? "sha256:" + Convert.ToHexStringLower(SHA256.HashData(archive))
            }
        });
        return new Fixture(new Dictionary<Uri, byte[]>
        {
            [new Uri(Registry, "catalog.json")] = catalog,
            [release] = metadata,
            [artifact] = archive
        });
    }

    private static byte[] CreateArchive(string? manifestPackageId)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var path in Directory.EnumerateFiles(ReferencePackagePath, "*", SearchOption.AllDirectories))
            {
                var entry = archive.CreateEntry(
                    Path.GetRelativePath(ReferencePackagePath, path).Replace(Path.DirectorySeparatorChar, '/'));
                using var destination = entry.Open();
                if (manifestPackageId is not null && Path.GetFileName(path) == "collector-manifest.json")
                {
                    var manifest = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
                    manifest["packageId"] = manifestPackageId;
                    destination.Write(JsonSerializer.SerializeToUtf8Bytes(manifest));
                }
                else
                {
                    using var source = File.OpenRead(path);
                    source.CopyTo(destination);
                }
            }
        }
        return output.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed record Fixture(IReadOnlyDictionary<Uri, byte[]> Responses);

    private sealed class StaticHttpHandler(IReadOnlyDictionary<Uri, byte[]> responses) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri);
            if (!responses.TryGetValue(uri, out var content))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
            });
        }
    }
}
