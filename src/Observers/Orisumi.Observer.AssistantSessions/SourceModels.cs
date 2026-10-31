using System.Globalization;
using System.Text.Json;
using Orisumi.Observer.Sdk;

namespace Orisumi.Observers.AssistantSessions;

// Runtime pipe limits for one batch, and the longest source line the reader keeps in memory. Tests inject smaller ones.
internal sealed record AssistantLimits(int BatchBytes, int BatchOperations, int LineBytes)
{
    internal static readonly AssistantLimits Default = new(JsonLines.MaximumBytes - 1024, 512, SourceFiles.MaximumLineBytes);
}

internal sealed record SessionInfo(string Product, string SourceSessionId, string? Title = null, string? Client = null,
    string? ParentId = null, string? AgentName = null, string? SpawnCallId = null);

internal sealed record ContentPart(string Type, string? Text, int EntryIndex, string Pointer,
    string? MediaType = null, BinaryContent? File = null);

internal sealed record Interaction(string Kind, string? Actor, string? SourceRole, string SourceKind, string SourceId,
    string? SourceVersion, string? SourceTurnId, string? SourceParentId, string? SourceMessageId,
    string? CallId, string? ToolName, string? ToolServer, bool? IsError, int? ExitCode, string? Phase,
    string? Model, string? WorkingDirectory, DateTimeOffset? StartAt, DateTimeOffset? EndAt, string? TimeZone,
    IReadOnlyList<ContentPart> Parts, IReadOnlyList<byte[]> SourceEntries);

// Persisted with the file checkpoint: source context must survive incremental reads and restart.
internal sealed class SourceContext
{
    public SessionInfo? Session { get; set; }
    public string? Version { get; set; }
    public string? TurnId { get; set; }
    public string? Model { get; set; }
    public string? WorkingDirectory { get; set; }
    public string? TimeZone { get; set; }
    public Dictionary<string, PendingReasoning> Reasoning { get; set; } = new(StringComparer.Ordinal);
}

internal sealed record PendingReasoning(byte[] Entry, string? TimeZone, string? Model, string? WorkingDirectory,
    string? Version, string? TurnId);

internal static class SourceJson
{
    internal static JsonElement At(this JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var found) ? found : default;
    internal static string? Text(this JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    internal static string? Text(this JsonElement value, string name) => value.At(name).Text();
    internal static bool? Boolean(this JsonElement value, string name) => value.At(name).ValueKind switch
    { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
    internal static int? Integer(this JsonElement value, string name) => value.At(name) is { ValueKind: JsonValueKind.Number } number
        && number.TryGetInt32(out var result) ? result : null;
    internal static IEnumerable<JsonElement> Elements(this JsonElement value) =>
        value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];
    internal static DateTimeOffset? Timestamp(this JsonElement value) => DateTimeOffset.TryParse(value.Text("timestamp"),
        CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time) ? time : null;
    internal static DateTimeOffset? Milliseconds(this JsonElement value, string name)
    {
        var field = value.At(name);
        if (field.ValueKind != JsonValueKind.Number || !field.TryGetInt64(out var milliseconds)) return null;
        try { return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
    internal static string Required(this JsonElement value, string name) =>
        value.Text(name) is { Length: > 0 } text ? text : throw new InvalidDataException($"Missing source {name}.");
    internal static string RequiredText(this JsonElement value, string name) =>
        value.Text(name) ?? throw new InvalidDataException($"Missing source {name}.");
}
