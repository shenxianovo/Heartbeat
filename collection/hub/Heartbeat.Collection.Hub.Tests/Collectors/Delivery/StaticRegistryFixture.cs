using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Heartbeat.Collection.CollectorRelease;
using Heartbeat.Collection.Hub.Collectors.Delivery;

namespace Heartbeat.Collection.Hub.Tests.Collectors.Delivery;

/// <summary>
/// A real on-disk Registry tree — <c>packages/{packageId}/current.json</c> plus
/// <c>packages/{packageId}/versions/{version}/{artifact}</c> — holding a genuine VRChat Collector
/// Package. It is the single sample generator: the reader tests, the download tests and the release
/// tooling tests all consume this tree instead of each embedding their own <c>current.json</c> text.
///
/// The mutators below produce the damaged samples the acceptance criteria call for: wrong length,
/// wrong hash, a corrupt Package whose index is internally consistent, missing fields, an unknown
/// schema version and an out-of-boundary artifact URL.
/// </summary>
internal sealed class StaticRegistryFixture : IDisposable
{
    private static readonly Lazy<byte[]> SampleArchive = new(BuildArchive, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly bool _ownsRoot;

    private StaticRegistryFixture(
        string rootDirectory,
        Uri registryBaseUri,
        string packageId,
        string version,
        string artifactFileName,
        bool ownsRoot)
    {
        RootDirectory = rootDirectory;
        RegistryBaseUri = registryBaseUri;
        PackageId = packageId;
        Version = version;
        ArtifactFileName = artifactFileName;
        _ownsRoot = ownsRoot;
    }

    public string RootDirectory { get; }
    public Uri RegistryBaseUri { get; }
    public string PackageId { get; }
    public string Version { get; }
    public string ArtifactFileName { get; }

    public string PackageDirectory => Path.Combine(RootDirectory, "packages", PackageId);
    public string IndexPath => Path.Combine(PackageDirectory, "current.json");
    public string VersionDirectory => Path.Combine(PackageDirectory, "versions", Version);
    public string ArtifactPath => Path.Combine(VersionDirectory, ArtifactFileName);

    public Uri ArtifactUrl => new(
        RegistryBaseUri,
        $"packages/{PackageId}/versions/{Version}/{ArtifactFileName}");

    /// <summary>Publishes the VRChat sample under <paramref name="registryBaseUri" />.</summary>
    public static StaticRegistryFixture PublishVRChat(Uri registryBaseUri, string? rootDirectory = null)
    {
        var ownsRoot = rootDirectory is null;
        var root = rootDirectory ?? Path.Combine(
            Path.GetTempPath(),
            $"heartbeat-registry-fixture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        var (packageId, version) = VRChatSamplePackage.ReadIdentity(VRChatSamplePackage.PackageDirectory);
        var fixture = new StaticRegistryFixture(root, registryBaseUri, packageId, version, "vrchat.zip", ownsRoot);

        Directory.CreateDirectory(fixture.VersionDirectory);
        File.WriteAllBytes(fixture.ArtifactPath, SampleArchive.Value);
        fixture.RepublishIndex();
        return fixture;
    }

    /// <summary>Rewrites <c>current.json</c> so length and SHA-256 describe the artifact on disk.</summary>
    public void RepublishIndex()
    {
        var content = File.ReadAllBytes(ArtifactPath);
        var index = new CollectorRegistryIndex(
            CollectorRegistryIndexReader.SupportedSchemaVersion,
            PackageId,
            Version,
            new CollectorRegistryArtifact(
                ArtifactUrl,
                content.LongLength,
                Convert.ToHexStringLower(SHA256.HashData(content))));
        File.WriteAllBytes(IndexPath, CollectorRegistryIndexWriter.Write(index));
    }

    public string ReadIndexText() => File.ReadAllText(IndexPath, Encoding.UTF8);

    public void WriteIndexText(string json) => File.WriteAllText(IndexPath, json, new UTF8Encoding(false));

    /// <summary>Edits the published index as JSON, for example to drop or retype a single field.</summary>
    public void MutateIndex(Action<JsonObject> mutate)
    {
        var index = JsonNode.Parse(ReadIndexText())!.AsObject();
        mutate(index);
        WriteIndexText(index.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    /// <summary>Same bytes, one flipped: the download hashes to something other than the index says.</summary>
    public void FlipArtifactByte()
    {
        var content = File.ReadAllBytes(ArtifactPath);
        content[content.Length / 2] ^= 0xFF;
        File.WriteAllBytes(ArtifactPath, content);
    }

    /// <summary>Publishes fewer bytes than the index declares.</summary>
    public void TruncateArtifact(int bytes = 64)
    {
        var content = File.ReadAllBytes(ArtifactPath);
        File.WriteAllBytes(ArtifactPath, content[..(content.Length - bytes)]);
    }

    /// <summary>
    /// Publishes bytes that are not a loadable Collector Package while keeping the index honest
    /// about them. Length and SHA-256 still pass, so only the Package loader can reject this.
    /// </summary>
    public void PublishCorruptPackage() =>
        PublishArtifact(Encoding.ASCII.GetBytes("PK\u0003\u0004 this is not a real archive"));

    /// <summary>
    /// Publishes exactly <paramref name="content" /> as the artifact and republishes an index that
    /// is honest about its length and SHA-256, so integrity checks pass and only what the bytes
    /// contain can still be rejected.
    /// </summary>
    public void PublishArtifact(byte[] content)
    {
        File.WriteAllBytes(ArtifactPath, content);
        RepublishIndex();
    }

    /// <summary>
    /// The published artifact is packed by the release tooling itself, so the fixture cannot drift
    /// into producing a shape the real pipeline never emits.
    /// </summary>
    private static byte[] BuildArchive() =>
        CollectorPackageArchive.Pack(VRChatSamplePackage.PackageDirectory);

    public void Dispose()
    {
        if (_ownsRoot && Directory.Exists(RootDirectory))
            Directory.Delete(RootDirectory, recursive: true);
    }
}
