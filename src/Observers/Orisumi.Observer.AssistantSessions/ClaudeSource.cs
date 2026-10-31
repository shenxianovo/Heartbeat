using System.Text.Json;

namespace Orisumi.Observers.AssistantSessions;

internal static class ClaudeSource
{
    internal static IReadOnlyList<Interaction> Read(byte[] raw, SourceContext context, SessionInfo location, string? liveZone)
    {
        using var document = JsonDocument.Parse(raw);
        var row = document.RootElement;
        var type = row.Required("type");
        var session = context.Session ?? location;
        var sourceSession = location.ParentId is not null ? location.SourceSessionId : row.Text("sessionId") ?? session.SourceSessionId;
        context.Session = session with { SourceSessionId = sourceSession, Client = row.Text("entrypoint") ?? session.Client,
            ParentId = location.ParentId, AgentName = location.AgentName ?? session.AgentName,
            SpawnCallId = location.SpawnCallId ?? session.SpawnCallId, Title = location.Title ?? session.Title };
        context.Version = row.Text("version") ?? context.Version;
        if (type is "custom-title" or "agent-name")
        {
            context.Session = context.Session with { Title = row.Text("customTitle") ?? row.Text("agentName") ?? context.Session.Title };
            return [];
        }
        var frame = new Frame(row, raw, context.Session, liveZone);
        if (type is "attachment" or "api-request-shape" || type == "user" && (row.Boolean("isMeta") == true || row.Boolean("isCompactSummary") == true))
            return [frame.Context(type)];
        if (type is not ("user" or "assistant"))
        {
            if (type is "api-request" or "api-request-blob" or "file-history-snapshot" or "file-history-delta" or "queue-operation"
                or "last-prompt" or "atis-latch" or "mode" or "permission-mode" or "cost-state" or "ai-title" or "system" or "relocated") return [];
            throw new InvalidDataException("Unsupported Claude Code row: " + type);
        }
        var content = row.At("message").At("content");
        var uuid = row.Required("uuid");
        if (content.ValueKind == JsonValueKind.String)
            return [frame.Create("message", frame.Actor, type, uuid, [new("text", content.GetString(), 0, "/message/content")])];
        if (content.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Missing Claude Code message content.");
        return Blocks(frame, content, type, uuid);
    }

    private static List<Interaction> Blocks(Frame frame, JsonElement content, string type, string uuid)
    {
        var result = new List<Interaction>();
        var userParts = new List<ContentPart>();
        var index = 0;
        foreach (var block in content.EnumerateArray())
        {
            var blockType = block.Required("type");
            var pointer = "/message/content/" + index;
            switch (blockType)
            {
                case "tool_result" when type == "user":
                    result.Add(frame.Create("tool-result", "tool", blockType, uuid + "#" + index,
                        [new("json", null, 0, pointer + "/content")], block.Required("tool_use_id"), error: block.Boolean("is_error")));
                    break;
                case "tool_use" or "server_tool_use" when type == "assistant":
                    result.Add(frame.Create("tool-call", "assistant", blockType, uuid,
                        [new("json", block.At("input").GetRawText(), 0, pointer + "/input")], block.Required("id"), block.Text("name")));
                    break;
                case "thinking" when type == "assistant":
                    var parts = new List<ContentPart> { new("text", block.RequiredText("thinking"), 0, pointer + "/thinking") };
                    if (block.At("signature").ValueKind != JsonValueKind.Undefined) parts.Add(new("opaque", null, 0, pointer + "/signature"));
                    result.Add(frame.Create("reasoning", "assistant", blockType, uuid, parts));
                    break;
                case "redacted_thinking" when type == "assistant":
                    result.Add(frame.Create("reasoning", "assistant", blockType, uuid, [new("opaque", null, 0, pointer + "/data")]));
                    break;
                default:
                    var part = BlockPart(block, pointer);
                    if (type == "user") userParts.Add(part);
                    else if (blockType == "text") result.Add(frame.Create("message", "assistant", blockType, uuid, [part]));
                    else throw new InvalidDataException("Unsupported Claude Code assistant block: " + blockType);
                    break;
            }
            index++;
        }
        if (userParts.Count > 0) result.Add(frame.Create("message", frame.Actor, type, uuid, userParts));
        if (result.GroupBy(item => (item.Kind, item.SourceId)).Any(group => group.Count() > 1))
            throw new InvalidDataException("Multiple Claude Code blocks share an interaction identity.");
        return result;
    }

    private sealed class Frame(JsonElement row, byte[] raw, SessionInfo session, string? liveZone)
    {
        internal string? Actor => row.Text("type") == "assistant" ? "assistant" : row.At("origin").Text("kind") switch
        {
            "human" => session.ParentId is null ? "user" : "agent",
            "peer" or "coordinator" => "agent", "task-notification" => "application", _ => null,
        };

        internal Interaction Context(string type)
        {
            var id = type == "api-request-shape" ? session.SourceSessionId + "/" + row.Required("shapeHash") : row.Required("uuid");
            var pointer = type switch { "attachment" => "/attachment", "api-request-shape" => "/shape", _ => "/message/content" };
            return Create("context", "application", type == "attachment" ? "attachment/" + row.At("attachment").Text("type") : type,
                id, [new("json", null, 0, pointer)]);
        }

        internal Interaction Create(string kind, string? actor, string sourceKind, string id, IReadOnlyList<ContentPart> parts,
            string? call = null, string? tool = null, bool? error = null)
        {
            var message = row.At("message");
            var time = row.Timestamp();
            return new(kind, actor, message.Text("role"), sourceKind, id,
                row.Text("version"), row.Text("promptId"), row.Text("parentUuid"), message.Text("id"), call, tool, null, error,
                null, null, message.Text("model"), row.Text("cwd"),
                kind == "reasoning" || kind == "message" && actor == "assistant" ? null : time, time, liveZone, parts, [raw]);
        }
    }

    private static ContentPart BlockPart(JsonElement block, string pointer) => block.Text("type") switch
    {
        "text" => new("text", block.RequiredText("text"), 0, pointer + "/text"),
        "image" when block.At("source").Text("type") == "base64" =>
            new("image", null, 0, pointer + "/source/data", block.At("source").Text("media_type")),
        "image" when block.At("source").Text("type") == "url" => new("image", null, 0, pointer + "/source/url"),
        _ => new("json", null, 0, pointer),
    };
}
