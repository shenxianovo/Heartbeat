using System.Security.Cryptography;
using System.Text.Json;

namespace Orisumi.Observer.Sdk;

/// <summary>Builds a binary descriptor and its commit payload from the same bytes.</summary>
public sealed class BinaryContent
{
    private readonly byte[] _data;
    public string Digest { get; }
    public JsonElement Descriptor { get; }
    public JsonElement Payload => JsonLines.Json(new { digest = Digest, data = _data });

    public BinaryContent(ReadOnlySpan<byte> data, string mediaType)
    {
        _data = data.ToArray();
        Digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(_data));
        Descriptor = JsonLines.Json(new { digest = Digest, mediaType, size = _data.LongLength });
    }
}
