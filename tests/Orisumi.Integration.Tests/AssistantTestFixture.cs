using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Orisumi.Core;
using Orisumi.Testing;
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
    private readonly TestDatabase? _database;
    private readonly OrisumiApiFactory? _factory;
    private readonly Network _network;
    internal DataClient Data { get; }
    internal bool Offline { get => _network.Offline; set => _network.Offline = value; }
    internal AssistantState State { get; private set; }
    internal AssistantCollector Collector { get; private set; }
    internal List<string> Diagnostics { get; } = [];

    internal AssistantTestFixture(HttpMessageHandler? handler = null)
    {
        if (handler is null)
        {
            _database = DatabaseFixture.CreateAsync().GetAwaiter().GetResult();
            _factory = new OrisumiApiFactory(_database.ConnectionString);
            handler = _factory.Server.CreateHandler();
        }
        _network = new(handler);
        _http = new HttpClient(_network) { BaseAddress = new("http://localhost/") };
        Data = new DataClient(_http, _http, Ref.Recognize(AssistantRegistration.ObserverSchemaId, new(null, "test", Guid.NewGuid().ToString())));
        State = new(StateDirectory);
        AssistantRegistration.RegisterAsync(Data, "0.1.0").GetAwaiter().GetResult();
        Collector = CreateCollector();
    }

    internal AssistantCollector CreateCollector() => new(Data, State,
        Claude, Codex, Path.Combine(StateDirectory, "archives"), Diagnostics.Add, () => "Asia/Tokyo", TimeSpan.Zero);

    internal void Restart()
    {
        State.Dispose(); State = new(StateDirectory); Collector = CreateCollector();
    }

    internal string Write(string relative, params byte[][] rows)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path);
        foreach (var row in rows) { file.Write(row); file.WriteByte((byte)'\n'); }
        return path;
    }

    internal IReadOnlyList<EntityVersion> Interactions() => Versions("assistant-interactions");
    internal IReadOnlyList<EntityVersion> Sessions() => Versions("assistant-sessions");
    internal IReadOnlyList<EntityVersion> Titles() => Versions("assistant-session-titles");
    internal IReadOnlyList<EntityVersion> Subagents() => Versions("assistant-subagents");
    private EntityVersion[] Versions(string resource) => Data.ListAsync("entities?resourceName=" + resource).GetAwaiter().GetResult().Select(VersionJson.ReadSaved).ToArray();
    internal IReadOnlyList<EntityVersion> Observations() => Data.ListAsync("entities?category=observation").GetAwaiter().GetResult().Select(VersionJson.ReadSaved).ToArray();
    internal Task<byte[]> Bytes(string digest) => _http.GetByteArrayAsync("contents/" + digest);
    internal static JsonElement Content(EntityVersion version) => ((BusinessContent)version.Content).Fields;
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

    public void Dispose()
    {
        State.Dispose(); _http.Dispose(); _factory?.Dispose();
        if (_database is not null) _database.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _directory.Delete(true);
    }
    private sealed class Network(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        internal bool Offline { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Offline ? throw new HttpRequestException("offline") : base.SendAsync(request, token);
    }
}
