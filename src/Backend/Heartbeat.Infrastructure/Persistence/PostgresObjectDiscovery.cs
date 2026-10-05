using System.Text.Json;
using Heartbeat.Contracts;
using Heartbeat.Recording;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Heartbeat.Persistence;

internal sealed class PostgresObjectDiscovery(HeartbeatDbContext db)
{
    private const string ResolveSql = """
        WITH binding AS (
            INSERT INTO object_bindings(owner_id, scope_id, identity_namespace, identity_key, object_id)
            VALUES (@owner, @scope, @namespace, @key, @candidate)
            ON CONFLICT (owner_id, scope_id, identity_namespace, identity_key)
            DO UPDATE SET object_id = object_bindings.object_id
            WHERE @known IS NULL OR object_bindings.object_id = @known
            RETURNING object_id
        ), identity AS (
            INSERT INTO objects(id, owner_id)
            SELECT object_id, @owner FROM binding ON CONFLICT (id) DO NOTHING
        )
        SELECT object_id AS "Value" FROM binding
        """;

    public async Task AssociateAsync(Guid owner, Record record, CancellationToken token)
    {
        var resolved = new List<ResolvedReference>();
        var scopes = new Dictionary<ObjectScope, Guid>();
        for (var index = 0; index < record.Objects.Length; index++)
        {
            var reference = record.Objects[index];
            Guid? scopeId = reference.Scope is null ? null : scopes[reference.Scope];
            var id = await ResolveAsync(owner, reference, scopeId, token);
            resolved.Add(new ResolvedReference(id, reference.Role, reference.Name, index));
            if (reference.Scope is null && reference.Namespace is not null)
                scopes[new ObjectScope(reference.Namespace, reference.Key!)] = id;
        }
        var json = JsonSerializer.Serialize(resolved, JsonSerializerOptions.Web);
        var observedAt = record.ObservedAt ?? record.StartedAt;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO record_objects(record_id, reference_index, object_id, role)
            SELECT {record.Id}, reference_index, object_id, role
            FROM jsonb_to_recordset({json}::jsonb)
                AS item(object_id uuid, role text, name text, reference_index int)
            ON CONFLICT (record_id, reference_index) DO NOTHING;
            INSERT INTO object_descriptions(object_id, name, observed_at, record_id)
            SELECT DISTINCT ON (object_id) object_id, name, {observedAt}, {record.Id}
            FROM jsonb_to_recordset({json}::jsonb)
                AS item(object_id uuid, role text, name text, reference_index int)
            ORDER BY object_id, name COLLATE "C" NULLS LAST
            ON CONFLICT (object_id) DO UPDATE
            SET name = EXCLUDED.name, observed_at = EXCLUDED.observed_at, record_id = EXCLUDED.record_id
            WHERE EXCLUDED.name IS NOT NULL AND (object_descriptions.name IS NULL OR (EXCLUDED.observed_at, EXCLUDED.record_id) >
                (object_descriptions.observed_at, object_descriptions.record_id));
            """, token);
    }

    private async Task<Guid> ResolveAsync(Guid owner, ObjectReference reference, Guid? scope, CancellationToken token)
    {
        if (reference.Id is { } known && !await db.Objects.AnyAsync(item => item.Id == known && item.OwnerId == owner, token))
            throw new ArgumentException("The referenced object is unknown or unavailable.");
        if (reference.Namespace is null) return reference.Id!.Value;
        var candidate = reference.Id ?? RecordingObject.Create(owner).Id;
        var ids = await db.Database.SqlQueryRaw<Guid>(ResolveSql,
            new NpgsqlParameter("owner", owner),
            new NpgsqlParameter("scope", NpgsqlDbType.Uuid) { Value = (object?)scope ?? DBNull.Value },
            new NpgsqlParameter("namespace", reference.Namespace), new NpgsqlParameter("key", reference.Key!),
            new NpgsqlParameter("candidate", candidate),
            new NpgsqlParameter("known", NpgsqlDbType.Uuid) { Value = (object?)reference.Id ?? DBNull.Value })
            .ToListAsync(token);
        if (ids.Count == 0) throw new ArgumentException("The identifier already refers to another object.");
        return ids[0];
    }

    private sealed record ResolvedReference(
        [property: System.Text.Json.Serialization.JsonPropertyName("object_id")] Guid ObjectId,
        string Role, string? Name,
        [property: System.Text.Json.Serialization.JsonPropertyName("reference_index")] int ReferenceIndex);
}
