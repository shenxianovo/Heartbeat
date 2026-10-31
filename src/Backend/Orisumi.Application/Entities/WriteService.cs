using System.Text.Json;
using System.Text.Json.Nodes;
using Orisumi.Core;
using Orisumi.Application.Management;

namespace Orisumi.Application.Entities;

public sealed class WriteService(IEntityStore store, EntityService entities)
{
    public Task<WriteResponse> WriteAsync(WriteRequest request, Guid? host = null, CancellationToken cancellationToken = default) =>
        store.ExecuteAsync(async (transaction, token) =>
        {
            var observer = await ResolveObserverAsync(transaction, request.Observer, token);
            if (host is { } owner && (request.Observer.EntityId is null || !await transaction.IsHostInstanceAsync(observer, owner, token)))
                throw new ManagementException(403, "host_mismatch", "The Observer must be an active instance of this host.");
            var stream = await transaction.ReadStreamAsync(observer, token);
            if (request.Sequence is { } sequence && sequence <= stream.LastSequence) return new WriteResponse([], true);
            foreach (var declaration in request.Schemas)
            {
                var chain = await transaction.ReadVersionsAsync(declaration.SchemaId, token);
                var version = chain.FirstOrDefault(v => v.Content is EntitySchema && JsonElement.DeepEquals(VersionJson.Content(v.Content), VersionJson.Content(declaration.Content)))
                    ?? await SaveAsync(transaction, declaration.SchemaId, (chain.Count == 0 ? null : chain[^1]), null, declaration.Content, token);
                stream.SchemaVersionIds[declaration.SchemaId] = version.VersionId;
            }
            var results = new List<WriteResult>();
            var inputs = new Dictionary<string, VersionId>(StringComparer.Ordinal);
            foreach (var write in request.Writes)
            {
                if (write.StorageError is { } storageError)
                {
                    results.Add(new(null, false, null, new(400, null, storageError)));
                    continue;
                }
                var workingInputs = new Dictionary<string, VersionId>(inputs, StringComparer.Ordinal);
                try
                {
                    var result = await transaction.SavepointAsync(() => WriteOneAsync(transaction, observer, stream, write, workingInputs, host, token), token);
                    inputs = workingInputs;
                    results.Add(result);
                }
                catch (ModelViolationException error)
                {
                    var status = error.Conflict || error.Rule is "HISTORY-7" or "SCHEMA-5" or "HISTORY-9" ? 409 : 400;
                    results.Add(new(null, false, null, new(status, error.Rule, error.Message)));
                }
            }
            await transaction.SaveStreamAsync(observer, stream with { LastSequence = request.Sequence ?? stream.LastSequence }, token);
            return new WriteResponse(results);
        }, cancellationToken);

    private static async Task<EntityId> ResolveObserverAsync(IEntityTransaction transaction, EntityReference reference, CancellationToken token)
    {
        if (reference.EntityId is { } id)
        {
            await CheckEntityAsync(transaction, id, token);
            return id;
        }
        var found = await transaction.FindRecognitionAsync(reference.SchemaId!.Value, reference.Key!, token);
        if (found is { } existing) return existing;
        var created = EntityId.New();
        await transaction.RegisterRecognitionAsync(reference.SchemaId.Value, reference.Key!, created, token);
        return created;
    }

    private async Task<WriteResult> WriteOneAsync(IEntityTransaction transaction, EntityId observer, WriteStreamState stream,
        EntityWrite write, Dictionary<string, VersionId> inputs, Guid? host, CancellationToken token)
    {
        VersionRules.Require(stream.SchemaVersionIds.TryGetValue(write.SchemaId, out var schemaId), "SCHEMA-3", "This Observer has not declared the business type.");
        var schemaVersion = await transaction.ReadVersionAsync(schemaId, token);
        VersionRules.Require(schemaVersion?.Content is EntitySchema, "SCHEMA-3", "The declared Schema version must still exist.");
        var schema = (EntitySchema)schemaVersion!.Content;
        var content = new BusinessContent(await ResolveObjectAsync(transaction, schema.Fields, write.Content, token));
        var id = write.EntityId;
        if (id is { } explicitId) await CheckEntityAsync(transaction, explicitId, token);
        else if (write.Key is { } key) id = await transaction.FindRecognitionAsync(write.SchemaId, key, token);
        var current = id is { } existing ? await transaction.ReadCurrentAsync(existing, token) : null;
        if (current is null && write.EntityId is { } reserved && host is { } owner && !await transaction.IsHostInstanceAsync(reserved, owner, token))
            throw new ManagementException(403, "host_mismatch", "The reserved instance belongs to another host.");
        id ??= EntityId.New();
        var candidate = new VersionSubmission(id.Value, VersionId.New(), current?.VersionId, schemaId, content);
        // Comparison walks the content along its Schema, so the content must fit it first.
        VersionRules.Validate(candidate, schema);
        var changed = current is null || current.SchemaVersionId != schemaId
            || !await VersionRules.SameBusinessContentAsync(schema, (BusinessContent)current.Content, content,
                reference => transaction.FindVersionEntityAsync(reference, token));
        var version = changed ? await entities.SaveAsync(transaction, candidate, token) : current!;
        if (write.Key is { } recognition) await transaction.RegisterRecognitionAsync(write.SchemaId, recognition, id.Value, token);
        var observation = write.Time is { } time
            ? await ObserveAsync(transaction, observer, version, time, write.Inputs ?? [], inputs, token) : null;
        return new(version, changed, observation);
    }

    private async Task<EntityVersion> ObserveAsync(IEntityTransaction transaction, EntityId observerId, EntityVersion content,
        FactTime time, VersionId[] members, Dictionary<string, VersionId> inputs, CancellationToken token)
    {
        var observer = await transaction.ReadCurrentAsync(observerId, token);
        VersionRules.Require(observer?.Content is BusinessContent, "OBSERVATION-1", "The Observer must have a saved business version.");
        foreach (var member in members)
            VersionRules.Require(await transaction.ReadVersionAsync(member, token) is not null, "ENTITY-6", "Inputs must be saved versions.");
        var current = await transaction.FindObservationAsync(observerId, content.EntityId, token);
        if (current?.Content is Observation previous && previous.ContentVersionId == content.VersionId
            && previous.StartAt == time.StartAt && previous.EndAt == time.EndAt && previous.TimeZone == time.TimeZone)
        {
            var previousMembers = previous.InputSetVersionId is { } input
                ? ((await transaction.ReadVersionAsync(input, token))?.Content as InputSet)?.Members : [];
            if (members.Distinct().Count() == members.Length && previousMembers is not null && previousMembers.Value.ToHashSet().SetEquals(members)) return current;
        }
        VersionId? inputId = null;
        if (members.Length > 0)
        {
            var key = string.Join(',', members.OrderBy(id => id.Value).Select(id => id.Value));
            if (!inputs.TryGetValue(key, out var saved))
            {
                saved = (await SaveAsync(transaction, EntityId.New(), null, null, new InputSet(members), token)).VersionId;
                inputs.Add(key, saved);
            }
            inputId = saved;
        }
        return await SaveAsync(transaction, current?.EntityId ?? EntityId.New(), current, null,
            new Observation(observer!.VersionId, content.VersionId, inputId, time.StartAt, time.EndAt, time.TimeZone), token);
    }

    private Task<EntityVersion> SaveAsync(IEntityTransaction transaction, EntityId id, EntityVersion? current,
        VersionId? schemaId, VersionContent content, CancellationToken token) =>
        entities.SaveAsync(transaction, VersionTime.Normalize(new VersionSubmission(id, VersionId.New(), current?.VersionId, schemaId, content)), token);

    private static async Task CheckEntityAsync(IEntityTransaction transaction, EntityId id, CancellationToken token)
    {
        VersionRules.Require(!await transaction.IsEntityDeletedAsync(id, token), "HISTORY-7", "Deleted entities cannot be restored.");
        VersionRules.Require(await transaction.EntityExistsAsync(id, token), "ENTITY-2", "EntityId must identify an existing entity or a reserved instance.");
    }

    private static async Task<JsonElement> ResolveObjectAsync(IEntityTransaction transaction, JsonElement fields, JsonElement content, CancellationToken token)
    {
        var result = JsonNode.Parse(content.GetRawText())!.AsObject();
        foreach (var field in fields.EnumerateObject())
            if (content.TryGetProperty(field.Name, out var value)) result[field.Name] = await ResolveValueAsync(transaction, field.Value, value, token);
        return JsonSerializer.SerializeToElement(result);
    }

    private static async Task<JsonNode?> ResolveValueAsync(IEntityTransaction transaction, JsonElement definition, JsonElement value, CancellationToken token)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        switch (definition.GetProperty("type").GetString())
        {
            case "reference":
                EntityVersion? target = null;
                if (value.ValueKind == JsonValueKind.String && value.TryGetGuid(out var versionId))
                    target = await transaction.ReadVersionAsync(new(versionId), token);
                else if (value.ValueKind == JsonValueKind.Object)
                {
                    EntityId? id = null;
                    if (value.TryGetProperty("entityId", out var entity) && entity.ValueKind == JsonValueKind.String && entity.TryGetGuid(out var entityId)) id = new(entityId);
                    else if (value.TryGetProperty("schemaId", out var schema) && schema.ValueKind == JsonValueKind.String && schema.TryGetGuid(out var schemaId)
                        && value.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.Object
                        && key.TryGetProperty("scope", out var scope) && scope.ValueKind is JsonValueKind.String or JsonValueKind.Null
                        && key.TryGetProperty("scheme", out var scheme) && scheme.ValueKind == JsonValueKind.String
                        && key.TryGetProperty("value", out var keyValue) && keyValue.ValueKind == JsonValueKind.String)
                        id = await transaction.FindRecognitionAsync(new(schemaId), new(scope.GetString(), scheme.GetString()!, keyValue.GetString()!), token);
                    if (id is { } found) target = await transaction.ReadCurrentAsync(found, token);
                }
                VersionRules.Require(target is not null || definition.GetProperty("nullable").GetBoolean(), "ENTITY-6", "Reference does not resolve to a saved version.");
                return target is null ? null : JsonValue.Create(target.VersionId.Value);
            case "object" when value.ValueKind == JsonValueKind.Object:
                return JsonNode.Parse((await ResolveObjectAsync(transaction, definition.GetProperty("fields"), value, token)).GetRawText());
            case "array" when value.ValueKind == JsonValueKind.Array:
                var array = new JsonArray();
                foreach (var item in value.EnumerateArray()) array.Add(await ResolveValueAsync(transaction, definition.GetProperty("items"), item, token));
                return array;
            default: return JsonNode.Parse(value.GetRawText());
        }
    }
}
