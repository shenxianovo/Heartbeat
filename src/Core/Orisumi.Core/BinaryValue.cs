using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Orisumi.Core;

public sealed partial record BinaryValue(string Digest, string MediaType, long Size)
{
    public static string ComputeDigest(ReadOnlySpan<byte> data) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(data));
    public static bool ValidDigest(string? digest) => digest is not null && DigestPattern().IsMatch(digest);

    public static bool IsValid(JsonElement value) => EntitySchemaRules.UniqueProperties(value)
        && value.EnumerateObject().Count() == 3
        && value.TryGetProperty("digest", out var digest) && digest.ValueKind == JsonValueKind.String && ValidDigest(digest.GetString())
        && value.TryGetProperty("mediaType", out var mediaType) && mediaType.ValueKind == JsonValueKind.String && MediaTypePattern().IsMatch(mediaType.GetString()!)
        && value.TryGetProperty("size", out var size) && EntitySchemaRules.IsInteger(size)
        && size.TryGetDecimal(out var length) && length is >= 0 and <= long.MaxValue;

    public static BinaryValue Read(JsonElement value)
    {
        VersionRules.Require(IsValid(value), "BINARY-1", "Invalid binary content descriptor.");
        return new(value.GetProperty("digest").GetString()!, value.GetProperty("mediaType").GetString()!, (long)value.GetProperty("size").GetDecimal());
    }

    public static IReadOnlyList<BinaryValue> Collect(VersionSubmission version, EntitySchema? schema)
    {
        var result = new List<BinaryValue>();
        if (version.Content is BusinessContent business)
        {
            VersionRules.Require(schema is not null, "SCHEMA-2", "Binary enumeration needs the version's Schema.");
            CollectObject(schema!.Fields, business.Fields, result);
        }
        return result;
    }

    private static void CollectObject(JsonElement fields, JsonElement content, List<BinaryValue> result)
    {
        foreach (var field in fields.EnumerateObject())
            if (content.TryGetProperty(field.Name, out var value)) CollectValue(field.Value, value, result);
    }

    private static void CollectValue(JsonElement definition, JsonElement value, List<BinaryValue> result)
    {
        if (value.ValueKind == JsonValueKind.Null) return;
        switch (definition.GetProperty("type").GetString())
        {
            case "binary": result.Add(Read(value)); break;
            case "object": CollectObject(definition.GetProperty("fields"), value, result); break;
            case "array":
                foreach (var item in value.EnumerateArray()) CollectValue(definition.GetProperty("items"), item, result);
                break;
        }
    }

    [GeneratedRegex(@"^sha256:[0-9a-f]{64}\z")]
    private static partial Regex DigestPattern();
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9!#$&^_.+\-]{0,126}/[A-Za-z0-9][A-Za-z0-9!#$&^_.+\-]{0,126}\z")]
    private static partial Regex MediaTypePattern();
}
