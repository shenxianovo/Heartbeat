using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Orisumi.Core;
using Orisumi.Observer.Runtime;
using Orisumi.Observer.Sdk;
using Orisumi.Observers.AssistantSessions;

namespace Orisumi.Integration.Tests;

internal sealed class AssistantTestFixture : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("orisumi-assistants-");
    private readonly HttpClient _http;
    internal string Root => _directory.FullName;
    internal string Claude => Path.Combine(Root, "claude");
    internal string Codex => Path.Combine(Root, "codex");
    internal string StateDirectory => Path.Combine(Root, "capture");
    internal EntityId Owner { get; } = EntityId.New();
    internal MemoryBackend Backend { get; }
    internal IObserverSession Session { get; private set; }
    internal AssistantState State { get; private set; }
    internal AssistantRegistration Registration { get; }
    internal AssistantCollector Collector { get; private set; }
    internal List<string> Diagnostics { get; } = [];

    internal AssistantTestFixture(HttpMessageHandler? handler = null)
    {
        Backend = new(handler);
        _http = Backend.CreateClient();
        Session = new RuntimeObserverSession(_http, Owner);
        State = new(StateDirectory);
        Registration = AssistantRegistration.RegisterAsync(Session, "0.1.0").GetAwaiter().GetResult();
        Collector = CreateCollector(Session);
    }

    internal AssistantCollector CreateCollector(IObserverSession session) => new(session, State, Owner,
        Registration, Claude, Codex, Path.Combine(StateDirectory, "archives"), Diagnostics.Add, () => "Asia/Tokyo", TimeSpan.Zero);

    internal void Restart()
    {
        State.Dispose();
        Session = new RuntimeObserverSession(_http, Owner);
        State = new(StateDirectory);
        Collector = CreateCollector(Session);
    }

    internal string Write(string relative, params byte[][] rows)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path);
        foreach (var row in rows) { file.Write(row); file.WriteByte((byte)'\n'); }
        return path;
    }

    internal IReadOnlyList<StoredVersion> Interactions() => Versions(Registration.InteractionSchema);
    internal IReadOnlyList<StoredVersion> Sessions() => Versions(Registration.SessionSchema);
    private StoredVersion[] Versions(VersionId schema) => Backend.Snapshot().Where(version => version.Version.SchemaVersionId == schema).ToArray();
    internal IReadOnlyList<StoredVersion> Observations() => Backend.Snapshot().Where(version => version.Version.Content is Observation).ToArray();
    internal static JsonElement Content(StoredVersion version) => ((BusinessContent)version.Version.Content).Fields;
    internal static byte[] Json(object value) => JsonSerializer.SerializeToUtf8Bytes(value, JsonSerializerOptions.Web);
    internal static byte[] ClaudeMessage(string id, object content, string type = "user", string? actor = "human",
        string? parent = null, string? cwd = "/work", bool meta = false, string sessionId = "main") => Json(new
        {
            type, uuid = id, sessionId, parentUuid = parent, cwd, version = "2.1.309", timestamp = "2026-10-30T10:00:00Z",
            entrypoint = "cli", origin = actor is null ? null : new { kind = actor }, isMeta = meta,
            message = new { role = type, content, id = type == "assistant" ? "response-one" : null, model = type == "assistant" ? "test-model" : null },
        });
    internal static byte[] Meta(string id = "codex-main", string? parent = null) => Json(new { type = "session_meta", timestamp = "2026-10-30T10:00:00Z",
        payload = new { id, cli_version = "0.166.0", originator = "cli", base_instructions = new { text = "Instructions" },
            source = parent is null ? null : new { subagent = new { thread_spawn = new { parent_thread_id = parent, tool_call_id = "spawn-1" } } } } });
    internal static byte[] Item(string type, string id, JsonObject fields, long start = 1793354400000, long end = 1793354402000)
    {
        var item = (JsonObject)fields.DeepClone(); item["type"] = type; item["id"] = id;
        return Json(new { type = "event_msg", timestamp = "2026-10-30T10:00:02Z",
            payload = new { type = "item_completed", turn_id = "turn-one", item, started_at_ms = start, completed_at_ms = end } });
    }
    internal static JsonObject Fields(string json) => JsonNode.Parse(json)!.AsObject();
    internal static JsonElement Pointer(JsonElement value, string pointer)
    {
        foreach (var raw in pointer.Split('/').Skip(1))
        {
            var key = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            value = value.ValueKind == JsonValueKind.Array ? value[int.Parse(key, System.Globalization.CultureInfo.InvariantCulture)] : value.GetProperty(key);
        }
        return value;
    }

    public void Dispose() { State.Dispose(); _http.Dispose(); Backend.Dispose(); _directory.Delete(true); }
}
