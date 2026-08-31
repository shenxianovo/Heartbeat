using System.Buffers;
using System.Text.Json;

namespace Heartbeat.Collection.Hub.Collectors.Delivery;

/// <summary>
/// The completion marker written as the last step of an installation. Its presence is the only
/// admission signal for a Collector Installation, so it is deliberately written after the content
/// has been unpacked and already re-verified through the Collector Package loader.
///
/// It names the candidate it completes — PackageId, Version, artifact SHA-256 — plus the Package
/// content hash the loader computed, so a directory cannot be reused for a different candidate by
/// renaming it, and content that no longer matches the marker stops being an Installation.
/// </summary>
public sealed record CollectorInstallationMarker(
    int SchemaVersion,
    string PackageId,
    string Version,
    string ArtifactSha256,
    string PackageContentHash)
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>The marker file name, inside the installation directory itself.</summary>
    public const string FileName = "collector-installation.json";

    private static readonly string[] Fields =
        ["schemaVersion", "packageId", "version", "artifactSha256", "packageContentHash"];

    internal static byte[] Write(CollectorInstallationMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", marker.SchemaVersion);
            writer.WriteString("packageId", marker.PackageId);
            writer.WriteString("version", marker.Version);
            writer.WriteString("artifactSha256", marker.ArtifactSha256);
            writer.WriteString("packageContentHash", marker.PackageContentHash);
            writer.WriteEndObject();
        }
        return [.. buffer.WrittenSpan, .. "\n"u8];
    }

    /// <summary>
    /// Returns <c>null</c> for anything this reader cannot accept as a marker — malformed JSON, a
    /// repeated or unknown property, a missing field, a wrong type. An unreadable marker is treated
    /// exactly like a marker that names another candidate: the directory is not an Installation.
    /// </summary>
    internal static CollectorInstallationMarker? Read(ReadOnlySpan<byte> json)
    {
        JsonDocument document;
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8
            });
            document = JsonDocument.ParseValue(ref reader);
            if (reader.Read())
                return null;
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!seen.Add(property.Name) || !Fields.Contains(property.Name, StringComparer.Ordinal))
                    return null;
            }
            if (Fields.Any(field => !seen.Contains(field)))
                return null;

            var schemaVersion = root.GetProperty("schemaVersion");
            if (schemaVersion.ValueKind != JsonValueKind.Number ||
                !schemaVersion.TryGetInt32(out var declaredSchemaVersion))
                return null;
            if (Text(root, "packageId") is not { } packageId ||
                Text(root, "version") is not { } version ||
                Text(root, "artifactSha256") is not { } artifactSha256 ||
                Text(root, "packageContentHash") is not { } packageContentHash)
                return null;

            return new CollectorInstallationMarker(
                declaredSchemaVersion,
                packageId,
                version,
                artifactSha256,
                packageContentHash);
        }
    }

    private static string? Text(JsonElement root, string name)
    {
        var element = root.GetProperty(name);
        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }

    /// <summary>True when this marker completes exactly <paramref name="reference" />.</summary>
    internal bool Completes(CollectorPackageReference reference) =>
        SchemaVersion == CurrentSchemaVersion &&
        string.Equals(PackageId, reference.PackageId, StringComparison.Ordinal) &&
        string.Equals(Version, reference.Version, StringComparison.Ordinal) &&
        string.Equals(ArtifactSha256, reference.ArtifactSha256, StringComparison.Ordinal);
}
