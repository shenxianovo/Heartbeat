using System.Text;
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
            var knownObserver = request.Observer.EntityId ?? await transaction.FindRecognitionAsync(request.Observer.SchemaId!.Value, request.Observer.Key!, token);
            if (knownObserver is { } known)
            {
                VersionRules.Require(!await transaction.IsEntityDeletedAsync(known, token), "HISTORY-7", "Deleted observers cannot write.");
                if (host is { } hostId && (request.Observer.EntityId is null || !await transaction.IsHostInstanceAsync(known, hostId, token)))
                    throw new ManagementException(403, "host_mismatch", "The Observer must be an active instance of this host.");
                if (request.Sequence is { } number && number <= (await transaction.ReadStreamAsync(known, token)).LastSequence)
                    return new WriteResponse([], true);
            }
            var defaults = new Dictionary<EntityId, VersionId>();
            foreach (var declaration in BuiltInSchemas.All)
                defaults[declaration.SchemaId] = (await DeclareAsync(transaction, declaration, token)).VersionId;
            foreach (var declaration in request.Schemas)
                defaults[declaration.SchemaId] = (await DeclareAsync(transaction, declaration, token)).VersionId;
            var observer = await ResolveObserverAsync(transaction, request.Observer, defaults, token);
            if (host is { } owner && (request.Observer.EntityId is null || !await transaction.IsHostInstanceAsync(observer, owner, token)))
                throw new ManagementException(403, "host_mismatch", "The Observer must be an active instance of this host.");
            var stream = await transaction.ReadStreamAsync(observer, token);
            if (request.Sequence is { } sequence && sequence <= stream.LastSequence) return new WriteResponse([], true);
            foreach (var declaration in request.Schemas) stream.SchemaVersionIds[declaration.SchemaId] = defaults[declaration.SchemaId];
            foreach (var declaration in BuiltInSchemas.All) stream.SchemaVersionIds.TryAdd(declaration.SchemaId, defaults[declaration.SchemaId]);
            var inputs = new Dictionary<string, VersionId>(StringComparer.Ordinal);
            if (request.Description is { } description)
            {
                var result = await WriteOneAsync(transaction, observer, stream with { DescriptionVersionId = null },
                    new(description.SchemaId, description.Content, "direct", new(observer.Value.ToString("D"), "orisumi.observer-description", "self")), inputs, token);
                stream = stream with { DescriptionVersionId = result.Version!.VersionId };
            }
            var results = new List<WriteResult>();
            foreach (var write in request.Writes)
            {
                if (write.StorageError is { } storageError)
                {
                    results.Add(new(null, false, new(400, null, storageError)));
                    continue;
                }
                var workingInputs = new Dictionary<string, VersionId>(inputs, StringComparer.Ordinal);
                try
                {
                    results.Add(await transaction.SavepointAsync(() => WriteOneAsync(transaction, observer, stream, write, workingInputs, token), token));
                    inputs = workingInputs;
                }
                catch (ModelViolationException error)
                {
                    var status = error.Conflict || error.Rule is "HISTORY-7" or "SCHEMA-5" or "HISTORY-9" ? 409 : 400;
                    results.Add(new(null, false, new(status, error.Rule, error.Message)));
                }
            }
            await transaction.SaveStreamAsync(observer, stream with { LastSequence = request.Sequence ?? stream.LastSequence }, token);
            return new WriteResponse(results);
        }, cancellationToken);

    private async Task<EntityVersion> DeclareAsync(IEntityTransaction transaction, SchemaDeclaration declaration, CancellationToken token)
    {
        var chain = await transaction.ReadVersionsAsync(declaration.SchemaId, token);
        return chain.FirstOrDefault(v => v.Content is EntitySchema && JsonElement.DeepEquals(VersionJson.Content(v.Content), VersionJson.Content(declaration.Content)))
            ?? await SaveAsync(transaction, declaration.SchemaId, (chain.Count == 0 ? null : chain[^1]), null, declaration.Content, token);
    }

    private static async Task<EntityId> ResolveObserverAsync(IEntityTransaction transaction, EntityReference reference,
        Dictionary<EntityId, VersionId> schemas, CancellationToken token)
    {
        if (reference.EntityId is { } id)
        {
            VersionRules.Require(!await transaction.IsEntityDeletedAsync(id, token), "HISTORY-7", "Deleted observers cannot be restored.");
            if (await transaction.ReadThingAsync(id, token) is not null) return id;
            VersionRules.Require(await transaction.EntityExistsAsync(id, token), "ENTITY-2", "EntityId must identify a thing or a reserved instance.");
            VersionRules.Require(await transaction.ReadCurrentAsync(id, token) is null, "OBSERVATION-1", "Observer must be a thing.");
            await transaction.CreateThingAsync(id, BuiltInSchemas.Program, token);
            return id;
        }
        return await RecognizeThingAsync(transaction, reference.SchemaId!.Value, reference.Key!, schemas, token);
    }

    private async Task<WriteResult> WriteOneAsync(IEntityTransaction transaction, EntityId observer, WriteStreamState stream,
        EntityWrite write, Dictionary<string, VersionId> inputs, CancellationToken token)
    {
        VersionRules.Require(stream.SchemaVersionIds.TryGetValue(write.SchemaId, out var schemaId), "SCHEMA-3", "This Observer has not declared the type.");
        var schemaVersion = await transaction.ReadVersionAsync(schemaId, token);
        VersionRules.Require(schemaVersion?.Content is EntitySchema { Kind: "observation" }, "SCHEMA-17", "Writes require an observation Schema.");
        var schema = (EntitySchema)schemaVersion!.Content;
        if (write.Key is { } key) ValidateKey(key);
        var fields = await ResolveObjectAsync(transaction, schema.Fields, write.Content, stream.SchemaVersionIds, token);
        var id = write.Key is { } recognition ? await transaction.FindRecognitionAsync(write.SchemaId, recognition, token) : null;
        var current = id is { } existing ? await transaction.ReadCurrentAsync(existing, token) : null;
        var time = write.Time ?? new(null, null, null);
        var observation = VersionTime.Normalize(new Observation(observer, stream.DescriptionVersionId, write.Formation, fields,
            null, time.StartAt, time.EndAt, time.TimeZone));
        var candidate = new VersionSubmission(id ?? EntityId.New(), VersionId.New(), current?.VersionId, schemaId, observation);
        VersionRules.Validate(candidate, schema);
        var previous = current?.Content as Observation;
        var sameFields = previous is not null && JsonElement.DeepEquals(previous.Fields, fields);
        if (previous is not null && previous.ObserverId != observer)
        {
            VersionRules.Require(sameFields, "OBSERVATION-10", "Another Observer owns this Observation.");
            return new(current, false);
        }
        VersionRules.Require(previous is null || !schema.Immutable || previous.ObserverVersionId != stream.DescriptionVersionId || sameFields,
            "SCHEMA-18", "Immutable content cannot change under the same description version.");
        var members = write.Inputs ?? [];
        VersionRules.Require(members.Distinct().Count() == members.Length, "INPUTSET-3", "Inputs must not repeat.");
        foreach (var member in members)
            VersionRules.Require(await transaction.ReadVersionAsync(member, token) is not null, "ENTITY-6", "Inputs must be saved versions.");
        var previousMembers = previous?.InputSetVersionId is { } previousInputs
            ? ((await transaction.ReadVersionAsync(previousInputs, token))?.Content as InputSet)?.Members : [];
        if (sameFields && current!.SchemaVersionId == schemaId && previous!.ObserverVersionId == observation.ObserverVersionId
            && previous.Formation == observation.Formation && previous.StartAt == observation.StartAt && previous.EndAt == observation.EndAt
            && previous.TimeZone == observation.TimeZone && previousMembers is not null && previousMembers.Value.ToHashSet().SetEquals(members))
            return new(current, false);
        if (members.Length > 0)
        {
            var inputKey = string.Join(',', members.OrderBy(member => member.Value).Select(member => member.Value));
            if (!inputs.TryGetValue(inputKey, out var inputId))
            {
                inputId = (await SaveAsync(transaction, EntityId.New(), null, null, new InputSet(members), token)).VersionId;
                inputs.Add(inputKey, inputId);
            }
            candidate = candidate with { Content = observation with { InputSetVersionId = inputId } };
        }
        var saved = await entities.SaveAsync(transaction, candidate, token);
        if (write.Key is { } savedKey) await transaction.RegisterRecognitionAsync(write.SchemaId, savedKey, saved.EntityId, token);
        return new(saved, true);
    }

    private Task<EntityVersion> SaveAsync(IEntityTransaction transaction, EntityId id, EntityVersion? current,
        VersionId? schemaId, VersionContent content, CancellationToken token) =>
        entities.SaveAsync(transaction, new(id, VersionId.New(), current?.VersionId, schemaId, content), token);

    private static void ValidateKey(RecognitionKey key)
    {
        VersionRules.Require(!string.IsNullOrEmpty(key.Scheme) && !string.IsNullOrEmpty(key.Value)
            && Encoding.UTF8.GetByteCount(key.Scope ?? "") + Encoding.UTF8.GetByteCount(key.Scheme) + Encoding.UTF8.GetByteCount(key.Value) <= 2048,
            "IDENTITY-7", "Recognition key must be nonempty and at most 2048 UTF-8 bytes.");
    }

    private static async Task<EntityId> RecognizeThingAsync(IEntityTransaction transaction, EntityId schemaId, RecognitionKey key,
        Dictionary<EntityId, VersionId> schemas, CancellationToken token)
    {
        ValidateKey(key);
        VersionRules.Require(schemas.TryGetValue(schemaId, out var versionId), "SCHEMA-3", "Thing type must be declared.");
        VersionRules.Require((await transaction.ReadVersionAsync(versionId, token))?.Content is EntitySchema { Kind: "thing" },
            "SCHEMA-17", "Reference requires a thing Schema.");
        if (await transaction.FindRecognitionAsync(schemaId, key, token) is { } found) return found;
        var id = EntityId.New();
        await transaction.CreateThingAsync(id, schemaId, token);
        await transaction.RegisterRecognitionAsync(schemaId, key, id, token);
        return id;
    }

    private static async Task<JsonElement> ResolveObjectAsync(IEntityTransaction transaction, JsonElement fields, JsonElement content,
        Dictionary<EntityId, VersionId> schemas, CancellationToken token)
    {
        var result = JsonNode.Parse(content.GetRawText())!.AsObject();
        foreach (var field in fields.EnumerateObject())
            if (content.TryGetProperty(field.Name, out var value)) result[field.Name] = await ResolveValueAsync(transaction, field.Value, value, schemas, token);
        return JsonSerializer.SerializeToElement(result);
    }

    private static async Task<JsonNode?> ResolveValueAsync(IEntityTransaction transaction, JsonElement definition, JsonElement value,
        Dictionary<EntityId, VersionId> schemas, CancellationToken token)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        switch (definition.GetProperty("type").GetString())
        {
            case "reference":
                var thing = definition.GetProperty("target").GetString() == "thing";
                Guid? target = null;
                if (value.ValueKind == JsonValueKind.String && value.TryGetGuid(out var raw))
                    target = thing ? (await transaction.ReadThingAsync(new(raw), token))?.EntityId.Value
                        : (await transaction.ReadVersionAsync(new(raw), token))?.VersionId.Value;
                else if (value.ValueKind == JsonValueKind.Object)
                {
                    EntityId? id = null;
                    if (!thing && value.TryGetProperty("entityId", out var entity) && entity.TryGetGuid(out var entityId)) id = new(entityId);
                    else if (value.TryGetProperty("schemaId", out var schema) && schema.TryGetGuid(out var schemaId)
                        && value.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.Object
                        && key.TryGetProperty("scope", out var scope) && scope.ValueKind is JsonValueKind.String or JsonValueKind.Null
                        && key.TryGetProperty("scheme", out var scheme) && scheme.ValueKind == JsonValueKind.String
                        && key.TryGetProperty("value", out var keyValue) && keyValue.ValueKind == JsonValueKind.String)
                    {
                        var recognition = new RecognitionKey(scope.GetString(), scheme.GetString()!, keyValue.GetString()!);
                        ValidateKey(recognition);
                        id = thing ? await RecognizeThingAsync(transaction, new(schemaId), recognition, schemas, token)
                            : await transaction.FindRecognitionAsync(new(schemaId), recognition, token);
                    }
                    if (id is { } found) target = thing ? found.Value : (await transaction.ReadCurrentAsync(found, token))?.VersionId.Value;
                }
                VersionRules.Require(target is not null || !thing && definition.GetProperty("nullable").GetBoolean(), "ENTITY-6", "Reference does not resolve to a saved target.");
                return target is null ? null : JsonValue.Create(target.Value);
            case "object" when value.ValueKind == JsonValueKind.Object:
                return JsonNode.Parse((await ResolveObjectAsync(transaction, definition.GetProperty("fields"), value, schemas, token)).GetRawText());
            case "array" when value.ValueKind == JsonValueKind.Array:
                var array = new JsonArray();
                foreach (var item in value.EnumerateArray()) array.Add(await ResolveValueAsync(transaction, definition.GetProperty("items"), item, schemas, token));
                return array;
            default: return JsonNode.Parse(value.GetRawText());
        }
    }
}
