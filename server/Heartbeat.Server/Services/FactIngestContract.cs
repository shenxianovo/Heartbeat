using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Heartbeat.Core.DTOs.Facts;
using Json.Schema;
using Heartbeat.Core.Facts;

namespace Heartbeat.Server.Services;

public sealed class FactIngestException(string message, bool conflict = false) : ArgumentException(message)
{
    public bool IsConflict { get; } = conflict;
}

internal sealed record ValidatedFactSchema(FactSchemaContract Definition, JsonSchema PayloadValidator)
{
    public string EvolutionMode => Definition.EvolutionMode;
    public bool AllowRetraction => Definition.AllowRetraction;
}

internal static class FactIngestContract
{
    internal static string Hash(string text) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    internal static string Canonical(JsonElement value)
    {
        if (Heartbeat.Core.Facts.FactJson.Validate(value) is { } error)
            throw new FactIngestException(error);
        return Heartbeat.Core.Facts.FactJson.Canonicalize(value);
    }

    internal static string SnapshotHash(FactSnapshot fact) => Hash(Canonical(JsonSerializer.SerializeToElement(new
    {
        fact.SchemaRevision, fact.RecordState, fact.Start, fact.End, fact.OccurredAt, fact.IsFinal, fact.Payload
    })));

    internal static ValidatedFactSchema Schema(FactStreamDefinition stream, FactSchemaDefinition schema)
    {
        try
        {
            if (schema.Revision <= 0 || string.IsNullOrWhiteSpace(schema.DocumentJson) || Hash(schema.DocumentJson) != schema.ContentHash)
                throw new FactIngestException("Fact Schema document hash does not match its content.");
            var definition = FactSchemaContract.Parse(Encoding.UTF8.GetBytes(schema.DocumentJson),
                stream.SchemaId, stream.SchemaMajor, schema.Revision, stream.FactKind);
            return new(definition, JsonSchema.FromText(definition.PayloadSchema.GetRawText(),
                new BuildOptions { Dialect = Dialect.Draft202012 }));
        }
        catch (FactIngestException) { throw; }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or JsonSchemaException or ArgumentException)
        { throw new FactIngestException("Invalid Fact Schema document: " + ex.Message); }
    }

    internal static void Snapshot(FactSnapshot fact, string kind, ValidatedFactSchema schema, DateTimeOffset now)
    {
        if (fact.StreamId == Guid.Empty || fact.FactId == Guid.Empty || fact.FactId.Version != 7 || fact.Revision is <= 0 or > 9_007_199_254_740_991 || fact.SchemaRevision <= 0 ||
            fact.RecordState is not ("present" or "retracted"))
            throw new FactIngestException("Invalid Fact envelope.");
        if (fact.ObservedAt is { } observed && (observed.Offset != TimeSpan.Zero || observed > now.AddMinutes(5)))
            throw new FactIngestException("Invalid Fact observedAt.");
        if (kind == "segment")
        {
            if (fact.Start is not { } start || fact.End is not { } end || fact.IsFinal is null || fact.OccurredAt is not null ||
                start.Offset != TimeSpan.Zero || end.Offset != TimeSpan.Zero || start > end || end > now.AddMinutes(5))
                throw new FactIngestException("Segment requires a valid UTC start/end/isFinal interval.");
        }
        else if (fact.OccurredAt is not { } at || fact.Start is not null || fact.End is not null || fact.IsFinal is not null ||
            at.Offset != TimeSpan.Zero || at > now.AddMinutes(5))
            throw new FactIngestException("Event requires only a valid UTC occurredAt time.");
        if (fact.RecordState == "retracted")
        {
            if (!schema.AllowRetraction || fact.Revision <= 1 || fact.Payload is not null)
                throw new FactIngestException("Fact Schema does not allow this retraction.");
        }
        else
        {
            if (fact.Payload is not { } payload) throw new FactIngestException("Present Fact requires payload.");
            _ = Canonical(payload);
            try
            {
                if (!schema.PayloadValidator.Evaluate(payload).IsValid)
                    throw new FactIngestException("Fact payload does not satisfy its schema.");
            }
            catch (Exception ex) when (ex is RefResolutionException or JsonSchemaException or InvalidOperationException or NotSupportedException)
            { throw new FactIngestException("Fact payload validation failed: " + ex.Message); }
        }
    }

    internal static bool HasOnlyMutableChanges(string before, string after, ValidatedFactSchema schema)
    {
        var left = JsonNode.Parse(before)!;
        var right = JsonNode.Parse(after)!;
        foreach (var path in schema.Definition.MutablePayloadPaths)
        {
            Remove(left, path);
            Remove(right, path);
        }
        return JsonNode.DeepEquals(left, right);
    }

    private static void Remove(JsonNode root, string pointer)
    {
        var parts = pointer.Split('/').Skip(1).Select(p => p.Replace("~1", "/").Replace("~0", "~")).ToArray();
        JsonNode? parent = root;
        foreach (var part in parts.SkipLast(1))
        {
            parent = parent is JsonObject obj ? obj[part] : parent is JsonArray array && int.TryParse(part, out var index) && index >= 0 && index < array.Count ? array[index] : null;
            if (parent is null) return;
        }
        if (parent is JsonObject objParent) objParent.Remove(parts[^1]);
        else if (parent is JsonArray arrayParent && int.TryParse(parts[^1], out var index) && index >= 0 && index < arrayParent.Count) arrayParent[index] = null;
    }

    internal static Guid LegacyId(string value) => Guid.ParseExact(Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(value))), "N");

    internal static Guid ProjectedSegmentId(Guid streamId, Guid factId)
    {
        var identity = Encoding.ASCII.GetBytes($"{streamId:D}/{factId:D}");
        var value = (factId.ToString("N")[..12] + Convert.ToHexStringLower(SHA256.HashData(identity).AsSpan(0, 10))).ToCharArray();
        value[12] = '7';
        value[16] = "89ab"[Convert.ToInt32(value[16].ToString(), 16) & 3];
        return Guid.ParseExact(new string(value), "N");
    }
}
