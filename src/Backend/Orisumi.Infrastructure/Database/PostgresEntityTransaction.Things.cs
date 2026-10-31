using NpgsqlTypes;
using Orisumi.Application.Entities;
using Orisumi.Core;

namespace Orisumi.Infrastructure.Database;

internal sealed partial class PostgresEntityTransaction
{
    private static string CurrentStanding(string entity) => $"""
        NOT EXISTS (SELECT 1 FROM reviews r WHERE r.target_entity_id = {entity} AND r.whole AND r.verdict = 'deny')
        AND (NOT EXISTS (SELECT 1 FROM reviews r WHERE r.target_entity_id = {entity} AND r.verdict = 'supersede')
             OR EXISTS (SELECT 1 FROM reviews r WHERE r.target_entity_id = {entity} AND r.verdict = 'confirm' AND r.by_person))
        """;

    public async Task CreateThingAsync(EntityId id, EntityId schemaId, CancellationToken token)
    {
        await ExecuteAsync("INSERT INTO things(entity_id, schema_id, deleted) VALUES ($1, $2, false)", token, id.Value, schemaId.Value);
        await ExecuteAsync("INSERT INTO entities(entity_id, category, schema_id) VALUES ($1, 'thing', $2)", token, id.Value, schemaId.Value);
    }

    public async Task<Thing?> ReadThingAsync(EntityId id, CancellationToken token) =>
        (await ReadThingsAsync([id], token)).SingleOrDefault();

    public async Task<IReadOnlyList<Thing>> ListThingsAsync(EntityQuery query, CancellationToken token)
    {
        var ids = (Guid[])(await ScalarAsync("""
            SELECT ARRAY(SELECT entity_id FROM entities WHERE category = 'thing'
              AND ($1::uuid IS NULL OR schema_id = $1) AND ($2::uuid IS NULL OR entity_id > $2)
              ORDER BY entity_id LIMIT $3)
            """, token, Typed(NpgsqlDbType.Uuid, query.SchemaId?.Value), Typed(NpgsqlDbType.Uuid, query.After?.Value), query.Limit + 1))!;
        return await ReadThingsAsync(ids.Select(id => new EntityId(id)).ToArray(), token);
    }

    public async Task<IReadOnlyList<Thing>> ReadThingsAsync(IReadOnlyList<EntityId> ids, CancellationToken token)
    {
        var values = ids.Select(id => id.Value).Distinct().ToArray();
        var things = new Dictionary<Guid, Thing>();
        await using (var command = Command("SELECT entity_id, schema_id FROM things WHERE entity_id = ANY($1) AND NOT deleted", [values]))
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token)) things.Add(reader.GetGuid(0), new(new(reader.GetGuid(0)), new(reader.GetGuid(1)), new List<RecognitionKey>()));
        await using (var command = Command("SELECT k.entity_id, k.scope, k.scheme, k.value FROM recognition_keys k JOIN things t ON t.entity_id = k.entity_id WHERE k.entity_id = ANY($1) AND NOT t.deleted", [values]))
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
                ((List<RecognitionKey>)things[reader.GetGuid(0)].Keys).Add(new(reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        // UNDERSTANDING-9: fetch names for the complete page in one query.
        await using (var command = Command($"""
            SELECT DISTINCT ON (v.content->>'thingId') (v.content->>'thingId')::uuid, v.content->>'name'
            FROM observations o JOIN versions v ON v.version_id = o.version_id
            JOIN things author ON author.entity_id = v.observer_id
            WHERE o.schema_id = $2 AND (v.content->>'thingId')::uuid = ANY($1) AND {CurrentStanding("o.entity_id")}
            ORDER BY v.content->>'thingId', (author.schema_id = $3) DESC, v.recorded_at DESC, v.version_id DESC
            """, [values, BuiltInSchemas.Name.Value, BuiltInSchemas.Person.Value]))
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
                if (things.TryGetValue(reader.GetGuid(0), out var thing)) things[reader.GetGuid(0)] = thing with { Name = reader.GetString(1) };
        return ids.Where(id => things.ContainsKey(id.Value)).Select(id => things[id.Value]).ToArray();
    }
}
