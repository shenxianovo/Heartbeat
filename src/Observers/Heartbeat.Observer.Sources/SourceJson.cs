using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Heartbeat.Observers.Sources;

public static class SourceJson
{
    public const int MaximumInputBytes = 8 * 1024 * 1024;

    public static async Task<JsonDocument> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var bytes = await ReadBytesAsync(path, cancellationToken);
        var document = JsonDocument.Parse(bytes);
        try { Validate(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }

    public static async Task<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaximumInputBytes) throw new InvalidDataException("Input exceeds the 8 MiB import limit.");
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (memory.Length + count > MaximumInputBytes) throw new InvalidDataException("Input grew beyond the 8 MiB import limit.");
            memory.Write(buffer, 0, count);
        }
        return memory.ToArray();
    }

    public static void Validate(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw new InvalidDataException("Source JSON has duplicate decoded property names.");
                    Validate(property.Value);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray()) Validate(item);
                break;
            case JsonValueKind.String: _ = value.GetString(); break;
        }
    }

    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static string Canonical(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); WriteCanonical(writer, property.Value); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }
}
