using System.Text;
using Heartbeat.Collection.Hub.Collectors.Delivery;
using Heartbeat.Collection.Hub.Collectors.Packages;

namespace Heartbeat.Collection.Hub.Tests.Collectors.Delivery;

/// <summary>
/// The single admission decision: is this directory a Collector Installation for exactly this
/// candidate? Every case here plants an on-disk state by hand — because that is what a crashed
/// download, an interrupted extraction or a restarted Hub actually leaves behind — and then asks the
/// store. A directory only counts when the completion marker exists, still names the requested
/// PackageId, Version and artifact SHA-256, and the content itself still loads through the existing
/// Collector Package loader.
///
/// Nothing here re-verifies the artifact bytes: the store is deliberately not a second Package
/// identity authority, it composes the one in <see cref="LocalCollectorPackage" />.
/// </summary>
public sealed class CollectorInstallationStoreTests : IDisposable
{
    private const string OtherSha256 = "1111111111111111111111111111111111111111111111111111111111111111";

    private readonly string _state = Path.Combine(
        Path.GetTempPath(),
        $"heartbeat-installations-{Guid.NewGuid():N}");
    private readonly CollectorInstallationStore _store;
    private readonly CollectorPackageReference _reference;

    public CollectorInstallationStoreTests()
    {
        Directory.CreateDirectory(_state);
        _store = new CollectorInstallationStore(_state);
        var (packageId, version) = VRChatSamplePackage.ReadIdentity(VRChatSamplePackage.PackageDirectory);
        _reference = new CollectorPackageReference(
            packageId,
            version,
            "0000000000000000000000000000000000000000000000000000000000000000");
    }

    public void Dispose()
    {
        if (Directory.Exists(_state))
            Directory.Delete(_state, recursive: true);
    }

    /// <summary>Copies the real sample Package into the directory that owns <paramref name="reference" />.</summary>
    private string PlantContent(CollectorPackageReference? reference = null)
    {
        var directory = _store.DirectoryFor(reference ?? _reference);
        CopyDirectory(VRChatSamplePackage.PackageDirectory, directory);
        return directory;
    }

    private void PlantMarker(CollectorInstallationMarker marker, string? directory = null) =>
        File.WriteAllBytes(
            Path.Combine(directory ?? _store.DirectoryFor(_reference), CollectorInstallationMarker.FileName),
            CollectorInstallationMarker.Write(marker));

    private CollectorInstallationMarker MarkerFor(CollectorPackageReference reference, string directory) =>
        new(
            CollectorInstallationMarker.CurrentSchemaVersion,
            reference.PackageId,
            reference.Version,
            reference.ArtifactSha256,
            LocalCollectorPackage.Load(directory).PackageContentHash);

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
    }

    [Fact]
    public void DirectoryFor_ExactCandidate_IsolatedByVersionAndArtifactHash()
    {
        var sameVersionOtherContent = _reference with { ArtifactSha256 = OtherSha256 };

        var first = _store.DirectoryFor(_reference);
        var second = _store.DirectoryFor(sameVersionOtherContent);

        Assert.NotEqual(first, second);
        Assert.Equal(
            Path.Combine(_store.PackagesRoot, _reference.PackageId, _reference.Version, _reference.ArtifactSha256),
            first);
        Assert.StartsWith(_store.InstallRoot + Path.DirectorySeparatorChar, first, StringComparison.Ordinal);
        Assert.StartsWith(Path.GetFullPath(_state) + Path.DirectorySeparatorChar, _store.InstallRoot, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenInstallation_MarkerAndContentMatchTheCandidate_IsAnInstallation()
    {
        var directory = PlantContent();
        PlantMarker(MarkerFor(_reference, directory));

        var result = _store.OpenInstallation(_reference);

        Assert.True(result.IsSuccess, result.Detail);
        var installation = result.Require();
        Assert.Equal(directory, installation.Directory);
        Assert.Equal(_reference, installation.Reference);
        Assert.Equal(_reference.PackageId, installation.Package.Manifest.PackageId);
        Assert.Equal(installation.Package.PackageContentHash, installation.PackageContentHash);
    }

    [Fact]
    public void OpenInstallation_NoDirectory_FailsWithMarkerMissing()
    {
        var result = _store.OpenInstallation(_reference);

        Assert.Equal(CollectorRegistryFailureReason.InstallationMarkerMissing, result.Reason);
    }

    [Fact]
    public void OpenInstallation_ContentWithoutMarker_FailsWithMarkerMissing()
    {
        PlantContent();

        var result = _store.OpenInstallation(_reference);

        Assert.Equal(CollectorRegistryFailureReason.InstallationMarkerMissing, result.Reason);
    }

    [Fact]
    public void OpenInstallation_PartiallyExtractedContent_IsNotAnInstallationAfterARestart()
    {
        var directory = _store.DirectoryFor(_reference);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "collector-manifest.json"),
            File.ReadAllText(Path.Combine(VRChatSamplePackage.PackageDirectory, "collector-manifest.json")));

        // A fresh store is what the next process sees; there is no in-memory state to lean on.
        var reopened = new CollectorInstallationStore(_state).OpenInstallation(_reference);

        Assert.Equal(CollectorRegistryFailureReason.InstallationMarkerMissing, reopened.Reason);
    }

    [Fact]
    public void OpenInstallation_MarkerNamingAnotherVersion_FailsWithMarkerMismatch()
    {
        var directory = PlantContent();
        var marker = MarkerFor(_reference, directory) with { Version = "9.9.9" };
        PlantMarker(marker);

        var result = _store.OpenInstallation(_reference);

        Assert.Equal(CollectorRegistryFailureReason.InstallationMarkerMismatch, result.Reason);
    }

    [Fact]
    public void OpenInstallation_MarkerNamingAnotherArtifactHash_FailsWithMarkerMismatch()
    {
        var directory = PlantContent();
        PlantMarker(MarkerFor(_reference, directory) with { ArtifactSha256 = OtherSha256 });

        var result = _store.OpenInstallation(_reference);

        Assert.Equal(CollectorRegistryFailureReason.InstallationMarkerMismatch, result.Reason);
    }

    [Fact]
    public void OpenInstallation_MarkerNamingAnotherPackage_FailsWithMarkerMismatch()
    {
        var directory = PlantContent();
        PlantMarker(MarkerFor(_reference, directory) with { PackageId = "heartbeat.collector.other" });

        var result = _store.OpenInstallation(_reference);

        Assert.Equal(CollectorRegistryFailureReason.InstallationMarkerMismatch, result.Reason);
    }

    [Fact]
    public void OpenInstallation_MarkerNamingAnotherPackageContentHash_FailsWithMarkerMismatch()
    {
        var directory = PlantContent();
        PlantMarker(MarkerFor(_reference, directory) with
        {
            PackageContentHash = "sha256:" + OtherSha256
        });

        var result = _store.OpenInstallation(_reference);

        Assert.Equal(CollectorRegistryFailureReason.InstallationMarkerMismatch, result.Reason);
    }

    [Fact]
    public void OpenInstallation_MarkerWithAnUnknownSchemaVersion_FailsWithMarkerMismatch()
    {
        var directory = PlantContent();
        PlantMarker(MarkerFor(_reference, directory) with { SchemaVersion = 2 });

        var result = _store.OpenInstallation(_reference);

        Assert.Equal(CollectorRegistryFailureReason.InstallationMarkerMismatch, result.Reason);
    }

    [Fact]
    public void OpenInstallation_UnreadableMarker_FailsWithMarkerMismatch()
    {
        var directory = PlantContent();
        File.WriteAllBytes(
            Path.Combine(directory, CollectorInstallationMarker.FileName),
            Encoding.UTF8.GetBytes("{ \"packageId\": "));

        var result = _store.OpenInstallation(_reference);

        Assert.Equal(CollectorRegistryFailureReason.InstallationMarkerMismatch, result.Reason);
    }

    [Fact]
    public void OpenInstallation_MarkerWithAnUnknownField_FailsWithMarkerMismatch()
    {
        var directory = PlantContent();
        var marker = Encoding.UTF8.GetString(
            CollectorInstallationMarker.Write(MarkerFor(_reference, directory)));
        File.WriteAllText(
            Path.Combine(directory, CollectorInstallationMarker.FileName),
            marker.TrimEnd().TrimEnd('}') + ", \"approved\": true }");

        var result = _store.OpenInstallation(_reference);

        Assert.Equal(CollectorRegistryFailureReason.InstallationMarkerMismatch, result.Reason);
    }

    [Fact]
    public void OpenInstallation_MarkerWithoutTheManifest_FailsPackageValidation()
    {
        var directory = PlantContent();
        var marker = MarkerFor(_reference, directory);
        File.Delete(Path.Combine(directory, "collector-manifest.json"));
        PlantMarker(marker);

        var result = _store.OpenInstallation(_reference);

        Assert.Equal(CollectorRegistryFailureReason.PackageValidationFailed, result.Reason);
    }

    [Fact]
    public void OpenInstallation_MarkerWithAlteredArtifactContent_FailsPackageValidation()
    {
        var directory = PlantContent();
        PlantMarker(MarkerFor(_reference, directory));
        var entrypoint = Path.Combine(
            directory,
            LocalCollectorPackage.Load(directory).Artifacts[0].Entrypoint);
        var bytes = File.ReadAllBytes(entrypoint);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(entrypoint, bytes);

        var result = _store.OpenInstallation(_reference);

        Assert.Equal(CollectorRegistryFailureReason.PackageValidationFailed, result.Reason);
    }

    [Fact]
    public void OpenInstallation_ContentDeclaringAnotherVersion_FailsWithManifestMismatch()
    {
        // A directory keyed by 9.9.9 whose marker agrees but whose Package manifest still says
        // 0.1.0 must not be able to impersonate the requested candidate.
        var impostor = _reference with { Version = "9.9.9" };
        var directory = PlantContent(impostor);
        PlantMarker(
            new CollectorInstallationMarker(
                CollectorInstallationMarker.CurrentSchemaVersion,
                impostor.PackageId,
                impostor.Version,
                impostor.ArtifactSha256,
                LocalCollectorPackage.Load(directory).PackageContentHash),
            directory);

        var result = _store.OpenInstallation(impostor);

        Assert.Equal(CollectorRegistryFailureReason.PackageManifestMismatch, result.Reason);
    }

    [Fact]
    public void OpenInstallation_SameVersionOtherArtifactHash_DoesNotSeeTheInstalledCandidate()
    {
        var directory = PlantContent();
        PlantMarker(MarkerFor(_reference, directory));

        var other = _store.OpenInstallation(_reference with { ArtifactSha256 = OtherSha256 });

        Assert.Equal(CollectorRegistryFailureReason.InstallationMarkerMissing, other.Reason);
        Assert.True(_store.OpenInstallation(_reference).IsSuccess);
    }

    [Theory]
    [InlineData("../evil", "0.1.0", "0000000000000000000000000000000000000000000000000000000000000000",
        CollectorRegistryFailureReason.InvalidPackageId)]
    [InlineData("heartbeat.collector.vrchat", "0.1", "0000000000000000000000000000000000000000000000000000000000000000",
        CollectorRegistryFailureReason.InvalidVersion)]
    [InlineData("heartbeat.collector.vrchat", "../0.1.0", "0000000000000000000000000000000000000000000000000000000000000000",
        CollectorRegistryFailureReason.InvalidVersion)]
    [InlineData("heartbeat.collector.vrchat", "0.1.0", "not-a-hash", CollectorRegistryFailureReason.InvalidArtifactSha256)]
    [InlineData("heartbeat.collector.vrchat", "0.1.0", "0000000000000000000000000000000000000000000000000000000000000000/..",
        CollectorRegistryFailureReason.InvalidArtifactSha256)]
    public void OpenInstallation_MalformedCandidate_FailsBeforeTouchingDisk(
        string packageId,
        string version,
        string artifactSha256,
        CollectorRegistryFailureReason expected)
    {
        var result = _store.OpenInstallation(new CollectorPackageReference(packageId, version, artifactSha256));

        Assert.Equal(expected, result.Reason);
        Assert.False(Directory.Exists(_store.PackagesRoot));
    }
}
