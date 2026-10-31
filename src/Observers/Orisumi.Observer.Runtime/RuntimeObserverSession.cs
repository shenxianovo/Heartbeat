using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Orisumi.Core;
using Orisumi.Observer.Sdk;

namespace Orisumi.Observer.Runtime;

/// <summary>Handles one program instance's requests by submitting to the backend directly. Tests call it in process.</summary>
public sealed class RuntimeObserverSession(HttpClient http, EntityId instanceId, Func<bool>? canSend = null) : IObserverSession
{
    // Saved versions never change, so a Schema read once can be reused for binary descriptors.
    private readonly Dictionary<VersionId, EntitySchema?> _schemas = [];

    public EntityId InstanceId { get; } = instanceId;

    public async Task<JsonElement> RequestAsync(string method, JsonElement parameters, CancellationToken cancellationToken = default)
    {
        switch (method)
        {
            case "registerSchema":
                return JsonLines.Json(new { versionId = (await RegisterSchemaAsync(Entity(parameters, "entityId"),
                    (EntitySchema)VersionJson.ReadContent("entity_schema", parameters.GetProperty("content")), cancellationToken)).Value });
            case "registerInstance":
                return JsonLines.Json(new { versionId = (await RegisterInstanceAsync(Version(parameters, "schemaVersionId"),
                    new BusinessContent(parameters.GetProperty("content")), cancellationToken)).Value });
            case "commit": return await CommitAsync(parameters, cancellationToken);
            case "readCurrent":
                var current = await ReadCurrentAsync(Entity(parameters, "entityId"), cancellationToken);
                return current is null ? JsonLines.Json(new { version = (object?)null }) : JsonLines.Json(new { version = VersionJson.Saved(current) });
            case "versions": return await ReadVersionsAsync(parameters.GetProperty("versionIds").EnumerateArray().Select(value => new VersionId(value.GetGuid())), cancellationToken);
            case "listEntities": case "listReferrers":
                var path = method == "listEntities" ? "entities" : $"entities/{Entity(parameters, "entityId").Value}/referrers";
                var allowed = method == "listEntities" ? new[] { "category", "resourceName", "after", "limit" } : new[] { "versions", "after", "limit" };
                var query = parameters.EnumerateObject().Where(field => field.Value.ValueKind != JsonValueKind.Null && allowed.Contains(field.Name, StringComparer.Ordinal))
                    .Select(field => field.Name + "=" + Uri.EscapeDataString(field.Value.ToString()));
                return await ReadPageAsync(path + "?" + string.Join("&", query), cancellationToken);
            default: throw new ArgumentException("Unknown Observer method: " + method);
        }
    }

    private async Task<VersionId> RegisterSchemaAsync(EntityId id, EntitySchema schema, CancellationToken token)
    {
        var chain = await ReadChainAsync(id, token);
        if (chain.FirstOrDefault(saved => SameContent(saved.Submission, schema, null)) is { } match) return match.VersionId;
        var version = new VersionSubmission(id, VersionId.New(), chain.Count == 0 ? null : chain[^1].VersionId, null, schema);
        await SubmitAsync([version], new Dictionary<string, byte[]>(), token);
        return version.VersionId;
    }

    private async Task<VersionId> RegisterInstanceAsync(VersionId schema, BusinessContent content, CancellationToken token)
    {
        var current = await ReadCurrentAsync(InstanceId, token);
        if (current is not null && SameContent(current.Submission, content, schema)) return current.VersionId;
        var version = new VersionSubmission(InstanceId, VersionId.New(), current?.VersionId, schema, content);
        await SubmitAsync([version], new Dictionary<string, byte[]>(), token);
        return version.VersionId;
    }

    private async Task<JsonElement> CommitAsync(JsonElement parameters, CancellationToken token)
    {
        var operations = parameters.GetProperty("operations");
        if (operations.GetArrayLength() is < 1 or > 512) throw new ArgumentException("A batch requires 1 through 512 operations.");
        var contents = Contents(parameters);
        var versions = new List<VersionSubmission>();
        var latest = new Dictionary<EntityId, VersionId>();
        var results = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var operation in operations.EnumerateArray())
        {
            var entity = Entity(operation, "entityId");
            VersionId result;
            switch (operation.GetProperty("op").GetString())
            {
                case "write":
                    if (operation.TryGetProperty("ifAbsent", out var absent) && absent.GetBoolean() && await CurrentAsync(entity) is { } present)
                    { result = present; break; }
                    var category = operation.GetProperty("category").GetString()!;
                    var schema = operation.TryGetProperty("schemaVersionId", out var schemaValue) && schemaValue.ValueKind != JsonValueKind.Null
                        ? new VersionId(schemaValue.GetGuid()) : (VersionId?)null;
                    var previous = operation.TryGetProperty("previousVersionId", out var previousValue)
                        ? previousValue.ValueKind == JsonValueKind.Null ? null : new VersionId(previousValue.GetGuid())
                        : await CurrentAsync(entity);
                    var content = VersionJson.ReadContent(category, Bind(operation, results));
                    var version = VersionTime.Normalize(new VersionSubmission(entity, VersionId.New(), previous, schema, content));
                    var declaration = schema is { } schemaId ? await SchemaAsync(schemaId, versions, token) : null;
                    if (operation.TryGetProperty("bindings", out var bindings))
                    {
                        var positions = VersionRules.GetReferences(version, declaration).Select(reference => reference.Position).ToHashSet(StringComparer.Ordinal);
                        if (bindings.EnumerateObject().Any(binding => !positions.Contains("/content" + binding.Name)))
                            throw new ArgumentException("Bindings must target declared content references.");
                    }
                    foreach (var descriptor in BinaryValue.Collect(version, declaration))
                        if (!contents.TryGetValue(descriptor.Digest, out var data) || data.LongLength != descriptor.Size)
                            throw new ArgumentException("Every binary value requires matching bytes in this batch.");
                    versions.Add(version);
                    latest[entity] = result = version.VersionId;
                    break;
                case "reference":
                    result = await CurrentAsync(entity) ?? throw new ArgumentException("The referenced entity has no version.");
                    break;
                default: throw new ArgumentException("Unknown batch operation.");
            }
            if (operation.TryGetProperty("as", out var name) && !results.TryAdd(name.GetString()!, result.Value))
                throw new ArgumentException("Batch result names must be unique.");
        }
        if (versions.Count > 0) await SubmitAsync(versions, contents, token);
        return JsonLines.Json(new { results });

        async Task<VersionId?> CurrentAsync(EntityId entity) =>
            latest.TryGetValue(entity, out var formed) ? formed : (await ReadCurrentAsync(entity, token))?.VersionId;
    }

    private async Task SubmitAsync(IReadOnlyList<VersionSubmission> versions, IReadOnlyDictionary<string, byte[]> contents, CancellationToken token)
    {
        EnsureConnected();
        foreach (var (digest, data) in contents)
        {
            using var body = new ByteArrayContent(data);
            body.Headers.ContentType = new("application/octet-stream");
            using var upload = await http.PutAsync("contents/" + digest, body, token);
            if (upload.StatusCode != HttpStatusCode.NoContent) throw new HttpRequestException($"Content upload failed: HTTP {(int)upload.StatusCode}.");
        }
        using var response = await http.PostAsJsonAsync("versions", new { versions = versions.Select(VersionJson.Submission) }, token);
        if (response.IsSuccessStatusCode) return;
        var problem = await ReadProblemAsync(response, token);
        if (problem.ValueKind == JsonValueKind.Object && problem.TryGetProperty("rule", out var rule) && problem.TryGetProperty("versionId", out var rejected))
            throw new SubmissionRejectedException(rule.GetString()!, rejected.GetGuid(),
                rule.GetString() + ": " + (problem.TryGetProperty("detail", out var detail) ? detail.GetString() : "rejected"));
        throw new HttpRequestException($"Submission failed: HTTP {(int)response.StatusCode}.");
    }

    private async Task<EntitySchema?> SchemaAsync(VersionId id, IEnumerable<VersionSubmission> formed, CancellationToken token)
    {
        if (formed.FirstOrDefault(version => version.VersionId == id) is { } local) return local.Content as EntitySchema;
        if (!_schemas.TryGetValue(id, out var schema))
        {
            var response = await ReadVersionsAsync([id], token);
            var saved = response.GetProperty("versions").EnumerateArray().Select(VersionJson.ReadSubmission).SingleOrDefault();
            _schemas[id] = schema = saved?.Content as EntitySchema;
        }
        return schema;
    }

    private static Dictionary<string, byte[]> Contents(JsonElement parameters)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (!parameters.TryGetProperty("contents", out var items)) return result;
        foreach (var item in items.EnumerateArray())
        {
            var digest = item.GetProperty("digest").GetString()!;
            var data = item.GetProperty("data").GetBytesFromBase64();
            if (!BinaryValue.ValidDigest(digest) || BinaryValue.ComputeDigest(data) != digest)
                throw new ArgumentException("Content bytes do not match their digest.");
            result[digest] = data;
        }
        return result;
    }

    private static JsonElement Bind(JsonElement operation, Dictionary<string, Guid> results)
    {
        var content = JsonNode.Parse(operation.GetProperty("content").GetRawText())!;
        if (operation.TryGetProperty("bindings", out var bindings))
            foreach (var binding in bindings.EnumerateObject())
            {
                if (!binding.Name.StartsWith('/')) throw new ArgumentException("Bindings use content JSON Pointers.");
                if (!results.TryGetValue(binding.Value.GetString()!, out var target)) throw new ArgumentException("Bindings must name an earlier operation.");
                var path = binding.Name.Split('/').Skip(1).Select(s => s.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)).ToArray();
                var parent = content;
                foreach (var part in path[..^1]) parent = parent is JsonArray array ? array[int.Parse(part, CultureInfo.InvariantCulture)]! : parent[part]!;
                if (parent is JsonArray elements) elements[int.Parse(path[^1], CultureInfo.InvariantCulture)] = target.ToString();
                else
                {
                    if (parent is not JsonObject obj || !obj.ContainsKey(path[^1])) throw new ArgumentException("Binding target is missing.");
                    parent[path[^1]] = target.ToString();
                }
            }
        return JsonSerializer.SerializeToElement(content);
    }

    private async Task<IReadOnlyList<EntityVersion>> ReadChainAsync(EntityId id, CancellationToken token)
    {
        EnsureConnected();
        using var response = await http.GetAsync("entities/" + id.Value + "/versions", token);
        // A deleted Schema is rejected on submission, so business code receives the rule.
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return [];
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return json.RootElement.GetProperty("versions").EnumerateArray().Select(VersionJson.ReadSaved).ToArray();
    }

    internal async Task<EntityVersion?> ReadCurrentAsync(EntityId id, CancellationToken token)
    {
        EnsureConnected();
        using var response = await http.GetAsync("entities/" + id.Value, token);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return null;
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return VersionJson.ReadSaved(json.RootElement);
    }

    internal async Task<JsonElement> ReadVersionsAsync(IEnumerable<VersionId> requested, CancellationToken token)
    {
        var ids = requested.Distinct().ToArray();
        if (ids.Length > 256) throw new ArgumentException("At most 256 version ids are allowed.");
        EnsureConnected();
        var versions = new List<JsonElement>();
        var missing = new List<Guid>();
        foreach (var id in ids)
        {
            using var response = await http.GetAsync("versions/" + id.Value, token);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) { missing.Add(id.Value); continue; }
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            versions.Add(VersionJson.Submission(VersionJson.ReadSaved(json.RootElement).Submission));
        }
        return JsonSerializer.SerializeToElement(new { versions, missing });
    }

    private async Task<JsonElement> ReadPageAsync(string path, CancellationToken token)
    {
        EnsureConnected();
        using var response = await http.GetAsync(path, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return JsonSerializer.SerializeToElement(new { versions = Array.Empty<object>(), referrers = Array.Empty<object>(), next = (string?)null });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(token);
    }

    private static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response, CancellationToken token)
    {
        try { return await response.Content.ReadFromJsonAsync<JsonElement>(token); }
        catch (JsonException) { return default; }
    }

    private void EnsureConnected()
    {
        if (canSend?.Invoke() == false) throw new HttpRequestException("Runtime is not connected.");
    }

    private static bool SameContent(VersionSubmission version, VersionContent content, VersionId? schema) =>
        VersionRules.SameSubmission(version, version with { Content = content, SchemaVersionId = schema });
    private static EntityId Entity(JsonElement json, string name) => new(json.GetProperty(name).GetGuid());
    private static VersionId Version(JsonElement json, string name) => new(json.GetProperty(name).GetGuid());
}
