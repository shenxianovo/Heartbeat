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
        Assert.Equal("heartbeat.collector.reference.config", package.Manifest.Config.Schema.Id);
        Assert.Equal(1, package.Manifest.Config.Schema.Version);
        Assert.Equal([1], package.Manifest.Config.AcceptedSchemaVersions);
        Assert.Equal("reference.inprocess", Assert.Single(package.Artifacts).ArtifactId);
        var schema = Assert.Single(package.FactSchemas);
        Assert.Equal("heartbeat.reference.segment", schema.SchemaId);
        Assert.Equal(FactKind.Segment, schema.FactKind);
    }

    [Fact]
    public void Load_FactPayloadSchemaIsInvalid_RejectsPackage()
    {
        using var packageCopy = ReferenceCollectorPackageCopy.Create(ReferencePackagePath);
        var schemaPath = Path.Combine(
            packageCopy.Path,
            "schemas",
            "reference-segment.schema.json");
        var schema = JsonNode.Parse(File.ReadAllText(schemaPath))!.AsObject();
        schema["payloadSchema"]!["type"] = "not-a-json-schema-type";
        File.WriteAllText(schemaPath, schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        packageCopy.UpdateSchemaHash(schemaPath);

        var error = Assert.Throws<PackageValidationException>(() =>
            LocalCollectorPackage.Load(packageCopy.Path));

        Assert.Contains("payloadSchema", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_ArtifactBytesChangedWithoutManifestChange_RejectsContentHashMismatch()
    {
        using var packageCopy = ReferenceCollectorPackageCopy.Create(ReferencePackagePath);
        var artifactPath = Path.Combine(
            packageCopy.Path,
            "artifacts",
            "reference-collector.json");
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
    public void Load_ManifestDoesNotDeclareConfigSchema_RejectsPackage()
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
    public void Load_SchemaPathEscapesPackageRoot_RejectsBeforeReading()
    {
        using var packageCopy = ReferenceCollectorPackageCopy.Create(ReferencePackagePath);
        var manifest = packageCopy.ReadManifest();
        manifest["outputs"]![0]!["schema"]!["document"] = "../outside.schema.json";
        packageCopy.WriteManifest(manifest);

        var error = Assert.Throws<PackageValidationException>(() =>
            LocalCollectorPackage.Load(packageCopy.Path));

        Assert.Contains("escapes", error.Message, StringComparison.OrdinalIgnoreCase);
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
            "reference-collector.json");
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
    public void Load_FactSchemaWhitespaceChangesExactHashInput_RejectsPackage()
    {
        using var packageCopy = ReferenceCollectorPackageCopy.Create(ReferencePackagePath);
        var schemaPath = Path.Combine(
            packageCopy.Path,
            "schemas",
            "reference-segment.schema.json");
        File.AppendAllText(schemaPath, "\n");

        var error = Assert.Throws<PackageValidationException>(() =>
            LocalCollectorPackage.Load(packageCopy.Path));

        Assert.Contains("content hash", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_FactKindEvolutionModeDoesNotMatch_RejectsPackage()
    {
        using var packageCopy = ReferenceCollectorPackageCopy.Create(ReferencePackagePath);
        var schemaPath = Path.Combine(
            packageCopy.Path,
            "schemas",
            "reference-segment.schema.json");
        var schema = JsonNode.Parse(File.ReadAllText(schemaPath))!.AsObject();
        schema["evolution"]!["mode"] = "immutableEvent";
        File.WriteAllText(schemaPath, schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        packageCopy.UpdateSchemaHash(schemaPath);

        var error = Assert.Throws<PackageValidationException>(() =>
            LocalCollectorPackage.Load(packageCopy.Path));

        Assert.Contains("evolution mode", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_SameSchemaIdentityReferencesDifferentDocuments_RejectsAmbiguity()
    {
        using var packageCopy = ReferenceCollectorPackageCopy.Create(ReferencePackagePath);
        var originalSchemaPath = Path.Combine(
            packageCopy.Path,
            "schemas",
            "reference-segment.schema.json");
        var secondSchemaPath = Path.Combine(
            packageCopy.Path,
            "schemas",
            "reference-segment-copy.schema.json");
        File.Copy(originalSchemaPath, secondSchemaPath);
        File.AppendAllText(secondSchemaPath, "\n");

        var manifest = packageCopy.ReadManifest();
        var duplicateOutput = manifest["outputs"]![0]!.DeepClone().AsObject();
        duplicateOutput["outputId"] = "activity-copy";
        duplicateOutput["schema"]!["document"] = "schemas/reference-segment-copy.schema.json";
        duplicateOutput["schema"]!["hash"] =
            "sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(secondSchemaPath)));
        manifest["outputs"]!.AsArray().Add(duplicateOutput);
        packageCopy.WriteManifest(manifest);

        var error = Assert.Throws<PackageValidationException>(() =>
            LocalCollectorPackage.Load(packageCopy.Path));

        Assert.Contains("schema identity", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_PayloadSchemaHasExternalDynamicReference_RejectsPackage()
    {
        using var packageCopy = ReferenceCollectorPackageCopy.Create(ReferencePackagePath);
        var schemaPath = Path.Combine(
            packageCopy.Path,
            "schemas",
            "reference-segment.schema.json");
        var schema = JsonNode.Parse(File.ReadAllText(schemaPath))!.AsObject();
        schema["payloadSchema"]!["$dynamicRef"] = "https://schemas.example.invalid/base";
        File.WriteAllText(schemaPath, schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        packageCopy.UpdateSchemaHash(schemaPath);

        var error = Assert.Throws<PackageValidationException>(() =>
            LocalCollectorPackage.Load(packageCopy.Path));

        Assert.Contains("external", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_PayloadSchemaHasUnresolvedLocalReference_RejectsPackage()
    {
        using var packageCopy = ReferenceCollectorPackageCopy.Create(ReferencePackagePath);
        var schemaPath = Path.Combine(
            packageCopy.Path,
            "schemas",
            "reference-segment.schema.json");
        var schema = JsonNode.Parse(File.ReadAllText(schemaPath))!.AsObject();
        schema["payloadSchema"] = new JsonObject
        {
            ["$ref"] = "#/$defs/missing"
        };
        File.WriteAllText(schemaPath, schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        packageCopy.UpdateSchemaHash(schemaPath);

        var error = Assert.Throws<PackageValidationException>(() =>
            LocalCollectorPackage.Load(packageCopy.Path));

        Assert.Contains("local reference", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_SelfContainedReferenceInsideEmbeddedResource_IsAcceptedAndExecutable()
    {
        using var packageCopy = ReferenceCollectorPackageCopy.Create(ReferencePackagePath);
        var schemaPath = Path.Combine(
            packageCopy.Path,
            "schemas",
            "reference-segment.schema.json");
        var schema = JsonNode.Parse(File.ReadAllText(schemaPath))!.AsObject();
        schema["payloadSchema"] = new JsonObject
        {
            ["$defs"] = new JsonObject
            {
                ["scoped"] = new JsonObject
                {
                    ["$id"] = "urn:heartbeat:test:scoped-payload",
                    ["$defs"] = new JsonObject
                    {
                        ["payload"] = new JsonObject { ["type"] = "object" }
                    },
                    ["$ref"] = "#/$defs/payload"
                }
            },
            ["$ref"] = "#/$defs/scoped"
        };
        File.WriteAllText(schemaPath, schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        packageCopy.UpdateSchemaHash(schemaPath);

        var package = LocalCollectorPackage.Load(packageCopy.Path);
        using var payload = JsonDocument.Parse("{}");

        Assert.True(Assert.Single(package.FactSchemas).IsPayloadValid(payload.RootElement));
    }

    [Fact]
    public void Load_AnchorInsideDifferentEmbeddedResource_DoesNotResolveFromRoot()
    {
        using var packageCopy = ReferenceCollectorPackageCopy.Create(ReferencePackagePath);
        var schemaPath = Path.Combine(
            packageCopy.Path,
            "schemas",
            "reference-segment.schema.json");
        var schema = JsonNode.Parse(File.ReadAllText(schemaPath))!.AsObject();
        schema["payloadSchema"] = new JsonObject
        {
            ["$defs"] = new JsonObject
            {
                ["child"] = new JsonObject
                {
                    ["$id"] = "child",
                    ["$anchor"] = "nestedOnly",
                    ["type"] = "object"
                }
            },
            ["$ref"] = "#nestedOnly"
        };
        File.WriteAllText(schemaPath, schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        packageCopy.UpdateSchemaHash(schemaPath);

        var error = Assert.Throws<PackageValidationException>(() =>
            LocalCollectorPackage.Load(packageCopy.Path));

        Assert.Contains("local reference", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_MeasurementWithoutDescriptor_IsRejectedAsUnsupportedByRuntimeSlice()
    {
        using var packageCopy = ReferenceCollectorPackageCopy.Create(ReferencePackagePath);
        var schemaPath = Path.Combine(
            packageCopy.Path,
            "schemas",
            "reference-segment.schema.json");
        var schema = JsonNode.Parse(File.ReadAllText(schemaPath))!.AsObject();
        schema["factKind"] = "measurement";
        schema["evolution"]!["mode"] = "measurementCorrection";
        schema["evolution"]!["allowRetraction"] = false;
        File.WriteAllText(schemaPath, schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        packageCopy.UpdateSchemaHash(schemaPath);
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
