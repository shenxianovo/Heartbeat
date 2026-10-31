using System.Text.Json;
using System.Text.RegularExpressions;
using Orisumi.Observer.Sdk;

namespace Orisumi.Observers.AssistantSessions;

internal static partial class CodexSource
{
    internal static IReadOnlyList<Interaction> Read(byte[] raw, SourceContext context, string? liveZone,
        Func<string, BinaryContent?>? readImage = null)
    {
        using var document = JsonDocument.Parse(raw);
        var row = document.RootElement;
        var type = row.Required("type");
        var payload = row.At("payload");
        if (type == "session_meta")
        {
            var spawn = payload.At("source").At("subagent").At("thread_spawn");
            context.Session = new("codex", payload.Required("id"), context.Session?.Title, payload.Text("originator"),
                spawn.Text("parent_thread_id"), payload.Text("agent_path") ?? spawn.Text("agent_path"), spawn.Text("tool_call_id"));
            context.Version = payload.Text("cli_version");
            return [Context(row, raw, context, context.Session.SourceSessionId, type, null, "/payload", liveZone)];
        }
        if (context.Session is null) throw new InvalidDataException("Codex file has no session_meta.");
        if (type == "turn_context")
        {
            context.Model = payload.Text("model"); context.WorkingDirectory = payload.Text("cwd"); context.TurnId = payload.Text("turn_id");
            return [];
        }
        if (type == "compacted") return [Context(row, raw, context, payload.Required("window_id"), type, null, "/payload", liveZone)];
        if (type == "response_item") return Response(row, raw, context, liveZone);
        if (type != "event_msg")
        {
            if (type is "token_usage_record" or "world_state") return [];
            throw new InvalidDataException("Unsupported Codex row: " + type);
        }
        return Completed(row, raw, context, liveZone, readImage);
    }

    private static IReadOnlyList<Interaction> Response(JsonElement row, byte[] raw, SourceContext context, string? liveZone)
    {
        const string type = "response_item";
        var payload = row.At("payload");
        var role = payload.Text("role");
        var kind = payload.Text("type");
        var metadata = payload.At("internal_chat_message_metadata_passthrough");
        var kinds = metadata.At("content_item_kinds").Elements().Select(value => value.Text()).ToArray();
        if (kind == "message" && (role is "developer" or "system" || role == "user" && kinds.Length > 0
            && kinds.All(value => value is not null && !value.StartsWith("user.", StringComparison.Ordinal))))
        {
            if (kinds.Contains("environments.environment_context", StringComparer.Ordinal))
                foreach (var block in payload.At("content").Elements())
                {
                    var match = ZonePattern().Match(block.Text("text") ?? "");
                    if (match.Success && TimeZoneInfo.TryFindSystemTimeZoneById(match.Groups[1].Value.Trim(), out var zone))
                        context.TimeZone = zone.HasIanaId ? zone.Id : null;
                }
            return [Context(row, raw, context, payload.Required("id"), type, role, "/payload/content", liveZone)];
        }
        if (kind == "additional_tools")
            return [Context(row, raw, context, payload.Required("id"), type, role, "/payload", liveZone)];
        if (kind == "reasoning")
        {
            var id = payload.Required("id");
            if (context.Reasoning.Remove(id, out var pending))
            {
                using var completed = JsonDocument.Parse(pending.Entry);
                return [Reasoning(completed.RootElement, pending, raw)];
            }
            // Both sides are durable until paired, even if a source version writes them in reverse order.
            context.Reasoning["raw:" + id] = new(raw, context.TimeZone ?? liveZone, context.Model, context.WorkingDirectory, context.Version, context.TurnId);
        }
        return [];
    }

    private static List<Interaction> Completed(JsonElement row, byte[] raw, SourceContext context,
        string? liveZone, Func<string, BinaryContent?>? readImage)
    {
        var payload = row.At("payload");
        var eventType = payload.Required("type");
        if (eventType is "task_complete" or "turn_aborted") return FinishTurn(payload.Text("turn_id") ?? context.TurnId, context);
        if (eventType is "token_count" or "token_usage_record" or "task_started" or "thread_settings_applied" or "world_state") return [];
        if (eventType != "item_completed") throw new InvalidDataException("Unsupported Codex event: " + eventType);
        var item = payload.At("item");
        var itemType = item.Required("type");
        var itemId = item.Required("id");
        var timeZone = context.TimeZone ?? liveZone;
        var started = payload.Milliseconds("started_at_ms");
        var ended = payload.Milliseconds("completed_at_ms");
        if (started > ended) throw new InvalidDataException("Codex item ends before it starts.");
        var turnId = payload.Text("turn_id") ?? context.TurnId;
        if (itemType == "Reasoning")
        {
            var pending = new PendingReasoning(raw, timeZone, context.Model, context.WorkingDirectory, context.Version, turnId);
            if (context.Reasoning.Remove("raw:" + itemId, out var other)) return [Reasoning(row, pending, other.Entry, rawFirst: true)];
            context.Reasoning[itemId] = pending;
            return [];
        }
        if (itemType is "UserMessage" or "AgentMessage")
        {
            var parts = MessageParts(item, readImage);
            return [Make("message", itemType == "AgentMessage" ? "assistant" : context.Session!.ParentId is null ? "user" : "agent",
                parts, started, itemType == "UserMessage" ? started : ended)];
        }
        if (itemType == "ContextCompaction") return [];
        if (itemType is not ("CommandExecution" or "McpToolCall" or "FileChange" or "Extension" or "ImageView"))
            throw new InvalidDataException("Unsupported Codex completed item: " + itemType);
        var arguments = itemType switch
        {
            "CommandExecution" => "command", "McpToolCall" => "arguments", "FileChange" => "changes",
            "Extension" when item.At("action").ValueKind != JsonValueKind.Undefined => "action",
            "Extension" => "query", _ => "path",
        };
        var output = itemType switch
        {
            "CommandExecution" => "aggregated_output", "McpToolCall" => "result", "FileChange" => "stdout",
            "Extension" => "results", _ => "path",
        };
        var argumentPointer = item.At(arguments).ValueKind == JsonValueKind.Undefined ? "/payload/item" : "/payload/item/" + arguments;
        var argumentValue = item.At(arguments).ValueKind == JsonValueKind.Undefined ? item : item.At(arguments);
        var outputPointer = item.At(output).ValueKind == JsonValueKind.Undefined ? "/payload/item" : "/payload/item/" + output;
        var outputs = new List<ContentPart> { new(output == "aggregated_output" || output == "stdout" ? "text" : "json", null, 0, outputPointer) };
        if (item.At("stderr").ValueKind != JsonValueKind.Undefined) outputs.Add(new("text", null, 0, "/payload/item/stderr"));
        return [Make("tool-call", "assistant", [new("json", argumentValue.GetRawText(), 0, argumentPointer)], started, started),
            Make("tool-result", "tool", outputs, ended, ended)];

        Interaction Make(string kind, string actor, IReadOnlyList<ContentPart> parts, DateTimeOffset? start, DateTimeOffset? end) =>
            new(kind, actor, null, itemType, itemId, context.Version, turnId, null, null,
                kind.StartsWith("tool-", StringComparison.Ordinal) ? itemId : null, item.Text("tool"), item.Text("server"),
                kind == "tool-result" ? item.Text("status") is { } status ? status == "failed" : null : null,
                kind == "tool-result" ? item.Integer("exit_code") : null, item.Text("phase"), context.Model,
                context.WorkingDirectory, start, end, timeZone, parts, [raw]);
    }

    private static List<ContentPart> MessageParts(JsonElement item, Func<string, BinaryContent?>? readImage)
    {
        var parts = new List<ContentPart>();
        var index = 0;
        foreach (var block in item.At("content").Elements())
        {
            var pointer = "/payload/item/content/" + index++;
            switch (block.Text("type"))
            {
                case "text" or "input_text" or "output_text": parts.Add(new("text", block.RequiredText("text"), 0, pointer + "/text")); break;
                case "image" or "input_image": parts.Add(new("image", null, 0, pointer + "/image_url", ImageMediaType(block.Text("image_url")))); break;
                case "local_image": parts.Add(new("image", null, 0, pointer + "/path", null, readImage?.Invoke(block.Required("path")))); break;
                default: parts.Add(new("json", null, 0, pointer)); break;
            }
        }
        if (item.At("text").ValueKind == JsonValueKind.String) parts.Add(new("text", item.Text("text"), 0, "/payload/item/text"));
        return parts;
    }

    private static Interaction Context(JsonElement row, byte[] raw, SourceContext context, string id, string kind,
        string? role, string pointer, string? liveZone) => new("context", "application", role, kind, id,
        context.Version, row.At("payload").At("internal_chat_message_metadata_passthrough").Text("turn_id") ?? context.TurnId,
        null, null, null, null, null, null, null, null, context.Model, context.WorkingDirectory,
        row.Timestamp(), row.Timestamp(), context.TimeZone ?? liveZone, [new("json", null, 0, pointer)], [raw]);

    // The turn has ended: a reasoning item still waiting for its response_item is formed from the item alone.
    private static List<Interaction> FinishTurn(string? turnId, SourceContext context)
    {
        var finished = new List<Interaction>();
        foreach (var (id, pending) in context.Reasoning.ToArray())
        {
            if (id.StartsWith("raw:", StringComparison.Ordinal) || turnId is not null && pending.TurnId != turnId) continue;
            context.Reasoning.Remove(id);
            using var completed = JsonDocument.Parse(pending.Entry);
            finished.Add(Reasoning(completed.RootElement, pending, null));
        }
        return finished;
    }

    private static Interaction Reasoning(JsonElement row, PendingReasoning pending, byte[]? raw, bool rawFirst = false)
    {
        var payload = row.At("payload");
        var item = payload.At("item");
        var parts = new List<ContentPart>();
        var completedIndex = rawFirst ? 1 : 0;
        foreach (var field in new[] { "summary_text", "raw_content" })
        {
            var index = 0;
            foreach (var value in item.At(field).Elements())
                parts.Add(new(field == "summary_text" ? "summary" : "text", value.Text() ?? value.Text("text"),
                    completedIndex, "/payload/item/" + field + "/" + index++ + (value.ValueKind == JsonValueKind.Object ? "/text" : "")));
        }
        if (raw is not null)
        {
            using var source = JsonDocument.Parse(raw);
            if (source.RootElement.At("payload").At("encrypted_content").ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
                parts.Add(new("opaque", null, rawFirst ? 0 : 1, "/payload/encrypted_content"));
        }
        return new("reasoning", "assistant", null, "Reasoning", item.Required("id"), pending.Version,
            payload.Text("turn_id") ?? pending.TurnId, null, null, null, null, null, null, null, null, pending.Model,
            pending.WorkingDirectory, payload.Milliseconds("started_at_ms"), payload.Milliseconds("completed_at_ms"),
            pending.TimeZone, parts, raw is null ? [pending.Entry] : rawFirst ? [raw, pending.Entry] : [pending.Entry, raw]);
    }

    private static string? ImageMediaType(string? url) => url is not null && url.StartsWith("data:", StringComparison.Ordinal)
        && url.IndexOf(';') is var end and > 5 ? url[5..end] : null;

    [GeneratedRegex(@"<timezone>\s*([^<]+)\s*</timezone>", RegexOptions.CultureInvariant)]
    private static partial Regex ZonePattern();
}
