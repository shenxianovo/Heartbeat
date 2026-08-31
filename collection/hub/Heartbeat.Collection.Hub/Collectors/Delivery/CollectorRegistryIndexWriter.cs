using System.Buffers;
using System.Text.Json;

namespace Heartbeat.Collection.Hub.Collectors.Delivery;

/// <summary>
/// Serializes a <see cref="CollectorRegistryIndex" />. The writer lives next to the reader so the
/// release tooling and the test fixtures emit the exact document shape the Runtime accepts, instead
/// of each hand-rolling the same JSON. It is a codec for the contract, not a publishing policy.
/// </summary>
public static class CollectorRegistryIndexWriter
{
    public static byte[] Write(CollectorRegistryIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", index.SchemaVersion);
            writer.WriteString("packageId", index.PackageId);
            writer.WriteString("version", index.Version);
            writer.WriteStartObject("artifact");
            writer.WriteString("url", index.Artifact.Url.AbsoluteUri);
            writer.WriteNumber("length", index.Artifact.Length);
            writer.WriteString("sha256", index.Artifact.Sha256);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return [.. buffer.WrittenSpan, .. "\n"u8];
    }
}
