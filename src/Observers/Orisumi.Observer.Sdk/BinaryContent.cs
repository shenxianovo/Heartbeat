using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orisumi.Observer.Sdk;

public sealed class BinaryContent
{
    internal byte[] Data { get; }
    public string Digest { get; }
    public JsonElement Descriptor { get; }
    public BinaryContent(ReadOnlySpan<byte> data, string mediaType)
    {
        Data = data.ToArray();
        Digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Data));
        Descriptor = JsonSerializer.SerializeToElement(new { digest = Digest, mediaType, size = Data.LongLength });
    }
}
internal sealed class BinaryConverter(Dictionary<string, BinaryContent> files) : JsonConverter<BinaryContent>
{
    public override BinaryContent Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => throw new NotSupportedException("Read binary descriptors as JSON.");
    public override void Write(Utf8JsonWriter writer, BinaryContent value, JsonSerializerOptions options)
    {
        files[value.Digest] = value;
        value.Descriptor.WriteTo(writer);
    }
}
