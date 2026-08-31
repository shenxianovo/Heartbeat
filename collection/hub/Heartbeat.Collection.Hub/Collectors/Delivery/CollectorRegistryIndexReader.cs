using System.Text.Json;
using System.Text.RegularExpressions;
using Heartbeat.Collection.Hub.Collectors.Packages;

namespace Heartbeat.Collection.Hub.Collectors.Delivery;

/// <summary>
/// Reads a per-Package <c>current.json</c> into a verified <see cref="CollectorRegistryIndex" />.
/// Every rejection is a <see cref="CollectorRegistryFailureReason" />; nothing throws at callers.
/// </summary>
public static class CollectorRegistryIndexReader
{
    /// <summary>The only schema version this Runtime slice implements.</summary>
    public const int SupportedSchemaVersion = 1;

    public const string IndexFileName = "current.json";

    private static readonly Regex Sha256Pattern = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    private static readonly string[] RootFields = ["schemaVersion", "packageId", "version", "artifact"];
    private static readonly string[] ArtifactFields = ["url", "length", "sha256"];

    /// <summary>
    /// Parses <paramref name="json" /> as the index for <paramref name="requestedPackageId" /> hosted
    /// under <paramref name="registryBaseUri" />.
    /// </summary>
    public static CollectorRegistryResult<CollectorRegistryIndex> Read(
        ReadOnlySpan<byte> json,
        Uri registryBaseUri,
        string requestedPackageId)
    {
        if (!CollectorRegistryBoundary.TryNormalizeBase(registryBaseUri, out var baseUri, out var baseDetail))
            return Fail(CollectorRegistryFailureReason.InvalidRegistryBaseUri, baseDetail);
        if (string.IsNullOrWhiteSpace(requestedPackageId) ||
            !LocalCollectorPackage.IsValidPackageId(requestedPackageId))
            return Fail(
                CollectorRegistryFailureReason.InvalidPackageId,
                $"Requested PackageId '{requestedPackageId}' is not a well-formed Collector PackageId.");

        JsonDocument document;
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
            document = JsonDocument.ParseValue(ref reader);
            if (reader.Read())
                return Fail(CollectorRegistryFailureReason.MalformedJson, "Registry index has trailing content.");
        }
        catch (JsonException exception)
        {
            return Fail(CollectorRegistryFailureReason.MalformedJson, exception.Message);
        }

        using (document)
        {
            var root = document.RootElement;
            if (RequireObject(root, "Registry index", RootFields) is { } rootFailure)
                return rootFailure;

            var schemaVersion = root.GetProperty("schemaVersion");
            if (schemaVersion.ValueKind != JsonValueKind.Number ||
                !schemaVersion.TryGetInt32(out var declaredSchemaVersion) ||
                declaredSchemaVersion != SupportedSchemaVersion)
                return Fail(
                    CollectorRegistryFailureReason.UnsupportedSchemaVersion,
                    $"Registry index schemaVersion must be {SupportedSchemaVersion}, got '{schemaVersion}'.");

            var packageIdElement = root.GetProperty("packageId");
            if (packageIdElement.ValueKind != JsonValueKind.String ||
                packageIdElement.GetString() is not { } packageId ||
                !LocalCollectorPackage.IsValidPackageId(packageId))
                return Fail(
                    CollectorRegistryFailureReason.InvalidPackageId,
                    $"Registry index packageId '{packageIdElement}' is not a well-formed Collector PackageId.");
            if (!string.Equals(packageId, requestedPackageId, StringComparison.Ordinal))
                return Fail(
                    CollectorRegistryFailureReason.PackageIdMismatch,
                    $"Registry index declares packageId '{packageId}' but '{requestedPackageId}' was requested.");

            var versionElement = root.GetProperty("version");
            if (versionElement.ValueKind != JsonValueKind.String ||
                versionElement.GetString() is not { } version ||
                !LocalCollectorPackage.IsValidSemVer(version))
                return Fail(
                    CollectorRegistryFailureReason.InvalidVersion,
                    $"Registry index version '{versionElement}' is not a valid Collector Package SemVer.");

            var artifact = root.GetProperty("artifact");
            if (RequireObject(artifact, "Registry index artifact", ArtifactFields) is { } artifactFailure)
                return artifactFailure;

            var urlElement = artifact.GetProperty("url");
            if (urlElement.ValueKind != JsonValueKind.String ||
                urlElement.GetString() is not { } url ||
                !CollectorRegistryBoundary.TryParseCanonicalAbsolute(url, out var artifactUrl))
                return Fail(
                    CollectorRegistryFailureReason.InvalidArtifactUrl,
                    $"Registry index artifact.url '{urlElement}' is not a canonical absolute URI.");

            var versionDirectory = CollectorRegistryBoundary.VersionDirectory(baseUri, packageId, version);
            if (!CollectorRegistryBoundary.IsFileWithin(versionDirectory, artifactUrl))
                return Fail(
                    CollectorRegistryFailureReason.ArtifactUrlOutsideRegistry,
                    $"Registry index artifact.url '{artifactUrl}' is not a file inside '{versionDirectory}'.");

            var lengthElement = artifact.GetProperty("length");
            if (lengthElement.ValueKind != JsonValueKind.Number ||
                !lengthElement.TryGetInt64(out var length) ||
                length <= 0)
                return Fail(
                    CollectorRegistryFailureReason.InvalidArtifactLength,
                    $"Registry index artifact.length '{lengthElement}' must be a positive integer.");

            var shaElement = artifact.GetProperty("sha256");
            if (shaElement.ValueKind != JsonValueKind.String ||
                shaElement.GetString() is not { } sha256 ||
                !Sha256Pattern.IsMatch(sha256))
                return Fail(
                    CollectorRegistryFailureReason.InvalidArtifactSha256,
                    $"Registry index artifact.sha256 '{shaElement}' must be 64 lowercase hex characters.");

            return CollectorRegistryResult<CollectorRegistryIndex>.Success(new CollectorRegistryIndex(
                declaredSchemaVersion,
                packageId,
                version,
                new CollectorRegistryArtifact(artifactUrl, length, sha256)));
        }
    }

    private static CollectorRegistryResult<CollectorRegistryIndex>? RequireObject(
        JsonElement element,
        string context,
        IReadOnlyList<string> fields)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return Fail(CollectorRegistryFailureReason.MissingField, $"{context} must be an object.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                return Fail(
                    CollectorRegistryFailureReason.DuplicateJsonProperty,
                    $"{context} repeats property '{property.Name}'.");
            if (!fields.Contains(property.Name, StringComparer.Ordinal))
                return Fail(
                    CollectorRegistryFailureReason.UnknownField,
                    $"{context} carries unknown property '{property.Name}'; extensions require a new schemaVersion.");
        }
        foreach (var field in fields)
        {
            if (!seen.Contains(field))
                return Fail(CollectorRegistryFailureReason.MissingField, $"{context} is missing '{field}'.");
        }
        return null;
    }

    private static CollectorRegistryResult<CollectorRegistryIndex> Fail(
        CollectorRegistryFailureReason reason,
        string detail) =>
        CollectorRegistryResult<CollectorRegistryIndex>.Failure(reason, detail);
}
