using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orisumi.Observer.Sdk;

/// <summary>Encodes the public HTTP contract. Identity and version formation belong to the backend.</summary>
public sealed class DataClient : IAsyncDisposable
{
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private const int MaximumBytes = 10 * 1024 * 1024;
    private readonly HttpClient _write;
    private readonly HttpClient _api;
    private readonly bool _ownsClients;
    private readonly CancellationTokenSource _stopping = new();
    public Ref Observer { get; }
    public string? DataDirectory { get; init; }
    public string? PackageVersion { get; init; }
    public DeviceContext? Device { get; init; }
    public JsonElement Settings { get; init; } = JsonSerializer.SerializeToElement(new { });
    public CancellationToken Stopping => _stopping.Token;
    public DataClient Direct => ReferenceEquals(_write, _api) ? this : new(_api, _api, Observer);

    public DataClient(HttpClient write, HttpClient api, Ref observer, bool ownsClients = false)
    { _write = write; _api = api; Observer = observer; _ownsClients = ownsClients; }

    public static DataClient Connect(Uri? api = null, Ref? observer = null, string? credential = null)
    {
        var managed = api is null;
        var backend = api ?? new Uri(Required("ORISUMI_API_URL"));
        var read = Client(backend, credential ?? Environment.GetEnvironmentVariable("ORISUMI_API_TOKEN"));
        var write = managed ? Client(new Uri(Required("ORISUMI_WRITE_URL")), null) : read;
        var client = new DataClient(write, read, observer ?? Ref.Entity(Guid.Parse(Required("ORISUMI_INSTANCE_ID"))), true)
        {
            DataDirectory = managed ? Required("ORISUMI_DATA_DIR") : null,
            PackageVersion = managed ? Required("ORISUMI_PACKAGE_VERSION") : null,
            Device = Environment.GetEnvironmentVariable("ORISUMI_DEVICE") is { Length: > 0 } device ? JsonSerializer.Deserialize<DeviceContext>(device, Json) : null,
            Settings = JsonSerializer.Deserialize<JsonElement>(Environment.GetEnvironmentVariable("ORISUMI_SETTINGS") ?? "{}")
        };
        if (managed) client.WatchStopping(Console.In);
        return client;
    }
    public void WatchStopping(TextReader input) => _ = Task.Run(async () =>
    {
        var buffer = new char[256];
        try { while (await input.ReadAsync(buffer, _stopping.Token) != 0) { } }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { return; }
        await _stopping.CancelAsync();
    });
    private static string Required(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"Missing {name}.");
    private static HttpClient Client(Uri address, string? credential)
    {
        var client = new HttpClient { BaseAddress = address };
        if (!string.IsNullOrEmpty(credential)) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return client;
    }
    public async Task UseAsync(IEnumerable<SchemaDefinition> definitions, Guid selfSchema, object self, CancellationToken token = default)
    {
        await SendAsync([], definitions.ToArray(), [], token, new { schemaId = selfSchema, content = self });
    }
    public async Task<WriteResult?> WriteAsync(WriteItem item, CancellationToken token = default)
    { var results = await WriteAllAsync([item], token); return results.Count == 0 ? null : results[0]; }
    public async Task<IReadOnlyList<WriteResult>> WriteAllAsync(IEnumerable<WriteItem> items, CancellationToken token = default)
    {
        var results = new List<WriteResult>();
        var batch = new List<JsonElement>();
        var uploads = new Dictionary<string, BinaryContent>();
        // Include the sequence that Runtime inserts when measuring its forwarded envelope.
        foreach (var item in items)
        {
            var encoded = Encode(item, out var files);
            batch.Add(encoded);
            if (Bytes(batch, null, long.MaxValue).Length > MaximumBytes)
            {
                batch.RemoveAt(batch.Count - 1);
                if (batch.Count != 0) results.AddRange(await SendAsync(batch, null, uploads, token));
                batch = [encoded]; uploads = [];
                if (Bytes(batch, null, long.MaxValue).Length > MaximumBytes) throw new InvalidDataException("One write exceeds 10 MiB.");
            }
            foreach (var file in files) uploads[file.Key] = file.Value;
        }
        if (batch.Count != 0) results.AddRange(await SendAsync(batch, null, uploads, token));
        return results;
    }
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1869", Justification = "The converter owns the files collected for this write; options must not share that mutable state.")]
    private static JsonElement Encode(WriteItem item, out Dictionary<string, BinaryContent> files)
    {
        files = [];
        var options = new JsonSerializerOptions(Json);
        // Content null members are meaningful; optional envelope members are omitted separately.
        options.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        options.Converters.Add(new BinaryConverter(files));
        var content = JsonSerializer.SerializeToElement(item.Content, options);
        return JsonSerializer.SerializeToElement(item with { Content = content }, Json);
    }
    private byte[] Bytes(IReadOnlyList<JsonElement> writes, SchemaDefinition[]? schemas, long? sequence = null, object? description = null) =>
        JsonSerializer.SerializeToUtf8Bytes(new { observer = Observer, schemas, writes, sequence, description }, Json);
    private async Task<IReadOnlyList<WriteResult>> SendAsync(IReadOnlyList<JsonElement> writes, SchemaDefinition[]? schemas,
        Dictionary<string, BinaryContent> files, CancellationToken token, object? description = null)
    {
        var bytes = Bytes(writes, schemas, description: description);
        if (Bytes(writes, schemas, long.MaxValue, description).Length > MaximumBytes) throw new InvalidDataException("Write request exceeds 10 MiB.");
        foreach (var file in files.Values)
        {
            using var upload = new ByteArrayContent(file.Data);
            upload.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var uploaded = await _write.PutAsync($"contents/{file.Digest}", upload, token);
            uploaded.EnsureSuccessStatusCode();
        }
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await _write.PostAsync("writes", content, token);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode == HttpStatusCode.Accepted) return [];
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
        var results = body.RootElement.GetProperty("results").Deserialize<WriteResult[]>(Json)!;
        // Every write was processed; expose failures so progress is not advanced on direct imports.
        if (results.FirstOrDefault(result => result.Error is not null) is { Error: { } error }) throw new WriteRejectedException(error);
        return results;
    }
    public Task<JsonElement> VersionAsync(Guid id, CancellationToken token = default) => GetAsync($"versions/{id}", token);
    public Task<JsonElement> EntityAsync(Guid id, CancellationToken token = default) => GetAsync($"entities/{id}", token);
    public async Task<JsonElement> VersionsAsync(IEnumerable<Guid> ids, CancellationToken token = default)
    {
        using var response = await _api.PostAsJsonAsync("versions/read", new { versionIds = ids }, Json, token);
        return await ReadAsync(response, token);
    }
    public Task<JsonElement> ObservationsAsync(string query = "", CancellationToken token = default) => GetAsync("observations" + query, token);
    public async Task<IReadOnlyList<JsonElement>> ListAsync(string path, CancellationToken token = default)
    {
        var items = new List<JsonElement>();
        string? cursor = null;
        do
        {
            var page = await GetAsync(path + (cursor is null ? "" : (path.Contains('?') ? "&" : "?") + "after=" + Uri.EscapeDataString(cursor)), token);
            items.AddRange(page.GetProperty(path.StartsWith("things", StringComparison.Ordinal) ? "things" : "versions").EnumerateArray());
            cursor = page.GetProperty("next").GetString();
        } while (cursor is not null);
        return items;
    }
    public async Task<JsonElement> GetAsync(string path, CancellationToken token = default)
    {
        using var response = await _api.GetAsync(path, token);
        return await ReadAsync(response, token);
    }
    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, CancellationToken token)
    {
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
        return document.RootElement.Clone();
    }
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        if (_ownsClients) { _write.Dispose(); if (!ReferenceEquals(_write, _api)) _api.Dispose(); }
    }
}
