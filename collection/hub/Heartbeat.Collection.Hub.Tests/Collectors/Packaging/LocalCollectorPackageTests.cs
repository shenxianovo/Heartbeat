using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Tests.Collectors;

namespace Heartbeat.Collection.Hub.Tests.Collectors.Packages;

public class LocalCollectorPackageTests
{
    private static string ReferencePackagePath => Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "ReferenceCollectorPackage");

    [Fact]
    public void Load_ValidReferencePackage_ReturnsVerifiedImmutableSnapshot()
    {
        var package = LocalCollectorPackage.Load(ReferencePackagePath);

        Assert.Equal("heartbeat.collector.reference", package.Manifest.PackageId);
        Assert.Equal("1.0.0", package.Manifest.Version);
        Assert.Equal(1, package.Manifest.Config.Version);
        Assert.Equal([1], package.Manifest.Config.AcceptedVersions);
        Assert.Equal("Reference Collector", package.Manifest.Presentation?.DisplayName);
        Assert.Equal("machine", package.Manifest.DefaultInstance?.SubjectKind);
        Assert.Equal(1, package.Manifest.DefaultInstance?.ConfigVersion);
        Assert.Equal(JsonValueKind.Object, package.Manifest.DefaultInstance?.Config.ValueKind);
        Assert.Equal("reference.inprocess", Assert.Single(package.Artifacts).ArtifactId);
    }

    [Fact]
    public void Load_ArtifactBytesChangedWithoutManifestChange_RejectsContentHashMismatch()
    {
        using var packageCopy = ReferenceCollectorPackageCopy.Create(ReferencePackagePath);
        var artifactPath = Path.Combine(
            packageCopy.Path,
            "artifacts",
            "reference-collector.artifact.json");
        var bytes = File.ReadAllBytes(artifactPath);
        bytes[10] ^= 1;
        File.WriteAllBytes(artifactPath, bytes);

        var error = Assert.Throws<PackageValidationException>(() =>
            LocalCollectorPackage.Load(packageCopy.Path));

        Assert.Contains("content hash", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_ManifestContainsUnknownField_RejectsStrictly()
    {
        using var packageCopy = ReferenceCollectorPackageCopy.Create(ReferencePackagePath);
        var manifest = packageCopy.ReadManifest();
        manifest["packageTypo"] = true;
        packageCopy.WriteManifest(manifest);

        var error = Assert.Throws<PackageValidationException>(() =>
            LocalCollectorPackage.Load(packageCopy.Path));

        Assert.Contains("unknown field 'packageTypo'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_ManifestDoesNotDeclareConfigVersion_RejectsPackage()
    {
        using var packageCopy = ReferenceCollectorPackageCopy.Create(ReferencePackagePath);
        var manifest = packageCopy.ReadManifest();
        manifest.Remove("config");
        packageCopy.WriteManifest(manifest);

        var error = Assert.Throws<PackageValidationException>(() =>
            LocalCollectorPackage.Load(packageCopy.Path));

        Assert.Contains("config", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_ConfigDoesNotAcceptItsCurrentVersion_RejectsPackage()
    {
        using var packageCopy = ReferenceCollectorPackageCopy.Create(ReferencePackagePath);
        var manifest = packageCopy.ReadManifest();
        manifest["config"]!["accepts"] = new JsonArray(2);
        packageCopy.WriteManifest(manifest);

        var error = Assert.Throws<PackageValidationException>(() =>
            LocalCollectorPackage.Load(packageCopy.Path));

        Assert.Contains("config.accepts", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_OutputDeclaresUnknownSubjectKind_RejectsPackage()
    {
        using var packageCopy = ReferenceCollectorPackageCopy.Create(ReferencePackagePath);
        var manifest = packageCopy.ReadManifest();
        manifest["outputs"]![0]!["subjectKinds"] = new JsonArray("spaceship");
        packageCopy.WriteManifest(manifest);

        var error = Assert.Throws<PackageValidationException>(() =>
            LocalCollectorPackage.Load(packageCopy.Path));

        Assert.Contains("subjectKind", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_DefaultInstanceUsesUnproducedSubjectKind_RejectsPackage()
    {
        using var packageCopy = ReferenceCollectorPackageCopy.Create(ReferencePackagePath);
        var manifest = packageCopy.ReadManifest();
        manifest["defaultInstance"]!["subjectKind"] = "account";
        packageCopy.WriteManifest(manifest);

        var error = Assert.Throws<PackageValidationException>(() =>
            LocalCollectorPackage.Load(packageCopy.Path));

        Assert.Contains("defaultInstance.subjectKind", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_DefaultInstanceConfigVersionIsNotAccepted_RejectsPackage()
    {
        using var packageCopy = ReferenceCollectorPackageCopy.Create(ReferencePackagePath);
        var manifest = packageCopy.ReadManifest();
        manifest["defaultInstance"]!["configVersion"] = 2;
        packageCopy.WriteManifest(manifest);

        var error = Assert.Throws<PackageValidationException>(() =>
            LocalCollectorPackage.Load(packageCopy.Path));

        Assert.Contains("defaultInstance.configVersion", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_DiskChangesAfterVerification_DoNotMutatePackageSnapshot()
    {
        using var packageCopy = ReferenceCollectorPackageCopy.Create(ReferencePackagePath);
        var package = LocalCollectorPackage.Load(packageCopy.Path);
        var artifact = Assert.Single(package.Artifacts);
        var verifiedBytes = artifact.Content.ToArray();

        File.WriteAllText(
            Path.Combine(packageCopy.Path, artifact.Entrypoint),
            "changed after verification");

        Assert.Equal(verifiedBytes, artifact.Content.ToArray());
    }

    [Fact]
    public void Load_CallerMutatesExposedArtifactMemory_DoesNotMutateVerifiedSnapshot()
    {
        var package = LocalCollectorPackage.Load(ReferencePackagePath);
        var artifact = Assert.Single(package.Artifacts);
        var exposed = artifact.Content;
        Assert.True(MemoryMarshal.TryGetArray(exposed, out ArraySegment<byte> segment));
        var original = segment.Array![segment.Offset];

        segment.Array[segment.Offset] ^= 1;

        Assert.Equal(original, artifact.Content.Span[0]);
    }

    [Fact]
    public void Load_ArtifactSymlinkEscapesPackageRoot_RejectsEvenWhenBytesMatch()
    {
        using var packageCopy = ReferenceCollectorPackageCopy.Create(ReferencePackagePath);
        var artifactPath = Path.Combine(
            packageCopy.Path,
            "artifacts",
            "reference-collector.artifact.json");
        var outsidePath = Path.Combine(
            Path.GetTempPath(),
            $"heartbeat-outside-artifact-{Guid.NewGuid():N}.json");
        File.Copy(artifactPath, outsidePath);
        try
        {
            File.Delete(artifactPath);
            File.CreateSymbolicLink(artifactPath, outsidePath);

            var error = Assert.Throws<PackageValidationException>(() =>
                LocalCollectorPackage.Load(packageCopy.Path));

            Assert.Contains("symbolic link", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(outsidePath);
        }
    }

    [Fact]
    public void Load_MeasurementWithoutDescriptor_IsRejectedAsUnsupportedByRuntimeSlice()
    {
        using var packageCopy = ReferenceCollectorPackageCopy.Create(ReferencePackagePath);
        var manifest = packageCopy.ReadManifest();
        manifest["outputs"]![0]!["factKind"] = "measurement";
        manifest["supportedCapabilities"]!.AsObject().Remove("facts.segment");
        manifest["supportedCapabilities"]!["facts.measurement.gauge"] = new JsonArray(1);
        packageCopy.WriteManifest(manifest);

        var error = Assert.Throws<PackageValidationException>(() =>
            LocalCollectorPackage.Load(packageCopy.Path));

        Assert.Contains("Measurement", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_SemVerNumericPrereleaseHasLeadingZero_RejectsPackage()
    {
        using var packageCopy = ReferenceCollectorPackageCopy.Create(ReferencePackagePath);
        var manifest = packageCopy.ReadManifest();
        manifest["version"] = "1.0.0-01";
        packageCopy.WriteManifest(manifest);

        var error = Assert.Throws<PackageValidationException>(() =>
            LocalCollectorPackage.Load(packageCopy.Path));

        Assert.Contains("SemVer", error.Message, StringComparison.Ordinal);
    }

}
