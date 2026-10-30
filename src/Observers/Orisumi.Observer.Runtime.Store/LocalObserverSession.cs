using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Orisumi.Core;
using Orisumi.Observer.Sdk;

namespace Orisumi.Observer.Runtime.Store;

/// <summary>The Runtime's finite data operations. Tests can call the same handler in process.</summary>
public sealed class LocalObserverSession(ObserverStore runtimeStore) : IObserverSession
{
    public async Task<JsonElement> RequestAsync(string method, JsonElement parameters, string? requestId = null,
        CancellationToken cancellationToken = default)
    {
        switch (method)
        {
            case "registerSchema":
                return JsonLines.Json(new { versionId = runtimeStore.RegisterSchemaLocally(Entity(parameters, "entityId"),
                    (EntitySchema)VersionJson.ReadContent("entity_schema", parameters.GetProperty("content"))).Value });
            case "registerInstance":
                return JsonLines.Json(new { versionId = runtimeStore.RegisterInstance(runtimeStore.InstanceId, Version(parameters, "schemaVersionId"),
                    new(parameters.GetProperty("content"))).Value });
            case "commit": return Commit(requestId ?? throw new ArgumentException("commit requires a request id."), parameters);
            case "local": return runtimeStore.Execute(local =>
            {
                IEnumerable<LocalVersion> values = local.Versions();
                if (parameters.TryGetProperty("entityId", out var entity)) values = values.Where(v => v.Version.EntityId.Value == entity.GetGuid());
                if (parameters.TryGetProperty("versionId", out var version)) values = values.Where(v => v.Version.VersionId == local.Resolve(new(version.GetGuid())));
                if (parameters.TryGetProperty("draftsOnly", out var drafts) && drafts.GetBoolean()) values = values.Where(v => v.State == SubmissionState.Draft);
                if (parameters.TryGetProperty("category", out var category)) values = values.Where(v => v.Version.Category == category.GetString());
                return JsonLines.Json(new { versions = values.Select(Value).ToArray() });
            });
            case "status": return Status(runtimeStore.Status);
            case "readCurrent":
                var current = await runtimeStore.ReadCurrentAsync(Entity(parameters, "entityId"), cancellationToken);
                return current is null ? JsonLines.Json(new { version = (object?)null }) : JsonLines.Json(new { version = VersionJson.Saved(current) });
            case "entityStatus":
                using (var response = await runtimeStore.GetEntityResponseAsync(Entity(parameters, "entityId"), cancellationToken))
                    return JsonLines.Json(new { status = (int)response.StatusCode });
            case "outcome": return await OutcomeAsync(parameters, cancellationToken);
            case "versions": return await runtimeStore.ReadVersionsAsync(parameters.GetProperty("versionIds").EnumerateArray()
                .Select(value => new VersionId(value.GetGuid())), cancellationToken);
            case "listEntities": case "listReferrers":
                var path = method == "listEntities" ? "entities" : $"entities/{Entity(parameters, "entityId").Value}/referrers";
                var allowed = method == "listEntities" ? new[] { "category", "resourceName", "after", "limit" } : new[] { "versions", "after", "limit" };
                var query = parameters.EnumerateObject().Where(field => field.Value.ValueKind != JsonValueKind.Null && allowed.Contains(field.Name, StringComparer.Ordinal))
                    .Select(field => field.Name + "=" + Uri.EscapeDataString(field.Value.ToString()));
                return await runtimeStore.ReadPageAsync(path + "?" + string.Join("&", query), cancellationToken);
            case "repair": return Repair(parameters, requestId ?? throw new ArgumentException("repair requires a request id."));
            default: throw new ArgumentException("Unknown Observer method: " + method);
        }
    }

    public static JsonElement Value(LocalVersion value) => JsonLines.Json(new
    {
        version = VersionJson.Submission(value.Version), state = value.State.ToString().ToLowerInvariant(),
        strategy = value.Strategy.ToString(), value.Rule, value.Detail,
    });
    public static JsonElement Status(DeliveryStatus status) => JsonLines.Json(new
    {
        status.Pending, status.Drafts, status.Saved, status.Error,
        rejection = status.Rejection is { } rejected ? new { version = Value(rejected.Version), dependents = rejected.Dependents.Select(Value) } : null,
    });

    private JsonElement Commit(string requestId, JsonElement parameters) => runtimeStore.Execute(local => Once(local, requestId, "commit", parameters, () =>
    {
        var operations = parameters.GetProperty("operations");
        if (operations.GetArrayLength() is < 1 or > 512) throw new ArgumentException("A batch requires 1 through 512 operations.");
        var results = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var references = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var versions = new HashSet<Guid>();
        foreach (var operation in operations.EnumerateArray())
        {
            var entity = Entity(operation, "entityId");
            VersionId? result;
            switch (operation.GetProperty("op").GetString())
            {
                case "draft":
                    if (operation.TryGetProperty("ifAbsent", out var absent) && absent.GetBoolean() && local.Latest(entity) is { } present)
                    { result = present.Version.VersionId; break; }
                    var content = BindContent(operation, references);
                    var category = operation.GetProperty("category").GetString()!;
                    var schema = operation.TryGetProperty("schemaVersionId", out var schemaValue) && schemaValue.ValueKind != JsonValueKind.Null
                        ? new VersionId(schemaValue.GetGuid()) : (VersionId?)null;
                    var strategy = operation.TryGetProperty("strategy", out var strategyValue) ? strategyValue.GetString() : "Immediate";
                    if (strategy is not ("Immediate" or "OnCompletion")) throw new ArgumentException("Invalid submission strategy.");
                    var parsed = VersionJson.ReadContent(category, content);
                    if (operation.TryGetProperty("bindings", out var bindings))
                    {
                        var declaration = schema is { } schemaId ? local.Find(local.Resolve(schemaId))?.Version.Content as EntitySchema : null;
                        var positions = VersionRules.GetReferences(new(entity, VersionId.New(), null, schema, parsed), declaration)
                            .Select(reference => reference.Position).ToHashSet(StringComparer.Ordinal);
                        if (bindings.EnumerateObject().Any(binding => !positions.Contains("/content" + binding.Name)))
                            throw new ArgumentException("Bindings must target declared content references.");
                    }
                    result = local.Draft(entity, parsed, schema,
                        Enum.Parse<SubmissionStrategy>(strategy), operation.TryGetProperty("publishPreview", out var publish) && publish.GetBoolean(),
                        operation.TryGetProperty("previousVersionId", out var previous) && previous.ValueKind != JsonValueKind.Null ? new VersionId(previous.GetGuid()) : null);
                    break;
                case "reference": result = local.Reference(entity); break;
                case "complete": local.Complete(entity); result = local.Latest(entity)!.Version.VersionId; break;
                default: throw new ArgumentException("Unknown batch operation.");
            }
            versions.Add(result.Value.Value);
            if (operation.TryGetProperty("as", out var name))
            {
                if (!results.TryAdd(name.GetString()!, result.Value.Value)) throw new ArgumentException("Batch result names must be unique.");
                if (operation.GetProperty("op").GetString() == "reference") references.Add(name.GetString()!, result.Value.Value);
            }
        }
        local.Metadata("batch:" + requestId, JsonSerializer.Serialize(versions));
        return JsonLines.Json(new { results });
    }));

    private async Task<JsonElement> OutcomeAsync(JsonElement parameters, CancellationToken token)
    {
        var batchId = parameters.GetProperty("batchId").GetString()!;
        var wait = parameters.TryGetProperty("wait", out var value) ? value.GetInt32() : 0;
        if (wait is < 0 or > 30) throw new ArgumentException("wait must be between 0 and 30 seconds.");
        var end = DateTimeOffset.UtcNow.AddSeconds(wait);
        do
        {
            var outcome = runtimeStore.Execute(local =>
            {
                var payload = local.Metadata("batch:" + batchId) ?? throw new KeyNotFoundException("Unknown batch.");
                var ids = JsonSerializer.Deserialize<Guid[]>(payload)!.Select(id => new VersionId(id)).ToHashSet();
                var versions = ids.Select(local.Find).ToArray();
                if (versions.Any(version => version is null)) return JsonLines.Json(new { state = "discarded" });
                var rejected = local.Rejection();
                if (rejected is not null && rejected.Dependents.Append(rejected.Version).Any(version => ids.Contains(version.Version.VersionId)))
                    return JsonLines.Json(new { state = "rejected", versionId = rejected.Version.Version.VersionId.Value, rejected.Version.Rule, rejected.Version.Detail });
                return JsonLines.Json(new { state = versions.All(version => version!.State == SubmissionState.Saved) ? "saved" : "pending" });
            });
            if (outcome.GetProperty("state").GetString() != "pending" || DateTimeOffset.UtcNow >= end) return outcome;
            await Task.Delay(TimeSpan.FromMilliseconds(50), token);
        } while (true);
    }

    private static JsonElement BindContent(JsonElement operation, Dictionary<string, Guid> results)
    {
        var content = JsonNode.Parse(operation.GetProperty("content").GetRawText())!;
        if (operation.TryGetProperty("bindings", out var bindings))
            foreach (var binding in bindings.EnumerateObject())
            {
                if (!binding.Name.StartsWith('/')) throw new ArgumentException("Bindings use content JSON Pointers.");
                var path = binding.Name.Split('/').Skip(1).Select(s => s.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)).ToArray();
                var parent = content;
                foreach (var part in path[..^1]) parent = parent is JsonArray array ? array[int.Parse(part, CultureInfo.InvariantCulture)]! : parent[part]!;
                var value = results[binding.Value.GetString()!].ToString();
                if (parent is JsonArray elements) elements[int.Parse(path[^1], CultureInfo.InvariantCulture)] = value;
                else
                {
                    if (parent is not JsonObject obj || !obj.ContainsKey(path[^1])) throw new ArgumentException("Binding target is missing.");
                    parent[path[^1]] = value;
                }
            }
        return JsonSerializer.SerializeToElement(content);
    }

    private JsonElement Repair(JsonElement parameters, string requestId) => runtimeStore.Execute(local => Once(local, requestId, "repair", parameters, () =>
    {
        if (local.Rejection()?.Version.Version.VersionId != Version(parameters, "rejectedVersionId"))
            throw new InvalidOperationException("The rejected version changed; read its state again.");
        switch (parameters.GetProperty("action").GetString())
        {
            case "adoptSaved": local.AdoptSaved(VersionJson.ReadSaved(parameters.GetProperty("saved"))); break;
            case "replaceRejected": local.ReplaceRejected(Entity(parameters, "entityId"),
                VersionJson.ReadContent("business", parameters.GetProperty("content")), local.Resolve(Version(parameters, "schemaVersionId"))); break;
            case "replaceReference":
                var entity = Entity(parameters, "entityId");
                if (local.Latest(entity) is null) local.Draft(entity, new BusinessContent(parameters.GetProperty("content")), Version(parameters, "schemaVersionId"));
                local.ReplaceReferenceAndResume(Version(parameters, "oldVersionId"), local.Reference(entity));
                break;
            case "discard": local.DiscardRejected(); break;
            default: throw new ArgumentException("Unknown repair action.");
        }
        return JsonLines.Json(new { });
    }));

    private static JsonElement Once(LocalTransaction local, string id, string method, JsonElement parameters, Func<JsonElement> perform)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128) throw new ArgumentException("Invalid request id.");
        var key = "receipt:" + id;
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(method + ":" + parameters.GetRawText())));
        if (local.Metadata(key) is { } existing)
        {
            using var receipt = JsonDocument.Parse(existing);
            if (receipt.RootElement.GetProperty("digest").GetString() != digest) throw new InvalidOperationException("Request id was already used with different content.");
            return receipt.RootElement.GetProperty("result").Clone();
        }
        var result = perform();
        local.Metadata(key, JsonSerializer.Serialize(new { digest, result }));
        return result;
    }

    private static EntityId Entity(JsonElement json, string name) => new(json.GetProperty(name).GetGuid());
    private static VersionId Version(JsonElement json, string name) => new(json.GetProperty(name).GetGuid());
}
