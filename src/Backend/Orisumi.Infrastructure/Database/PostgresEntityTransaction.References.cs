using Orisumi.Application.Entities;
using Orisumi.Core;
using NpgsqlTypes;

namespace Orisumi.Infrastructure.Database;

internal sealed partial class PostgresEntityTransaction
{
    private async Task SaveReferencesAsync(VersionId versionId, IReadOnlyList<ResolvedReference> references, CancellationToken token)
    {
        if (references.Count == 0) return;
        await ExecuteAsync("""
            INSERT INTO version_references(target_entity_id, version_id)
            SELECT DISTINCT unnest($1::uuid[]), $2
            """, token, references.Select(reference => reference.TargetEntityId.Value).ToArray(), versionId.Value);
    }

    public async Task<IReadOnlyList<Referrer>> ListReferrersAsync(ReferrerQuery query, CancellationToken cancellationToken)
    {
        var current = query.AllVersions ? "" : "AND v.version_id = e.current_version_id";
        var (after, order) = query.Descending ? ("<", "DESC") : (">", "ASC");
        var versions = await ReadAsync($"""
            SELECT {Columns} FROM version_references r
            JOIN versions v ON v.version_id = r.version_id
            JOIN entities e ON e.entity_id = v.entity_id
            WHERE r.target_entity_id = $1 AND ($2::uuid IS NULL OR r.version_id {after} $2) {current}
            ORDER BY r.version_id {order} LIMIT $3
            """, cancellationToken, query.Target.Value, Typed(NpgsqlDbType.Uuid, query.After?.Value), query.Limit + 1);
        var references = await ResolveReferencesAsync(versions, cancellationToken);
        return versions.Select(version => new Referrer(version, references[version.VersionId]
            .Where(reference => reference.TargetEntityId == query.Target)
            .Select(reference => new VersionReference(reference.Position, reference.TargetVersionId)).ToArray())).ToArray();
    }

    private async Task<Dictionary<VersionId, ResolvedReference[]>> ResolveReferencesAsync(
        IReadOnlyList<EntityVersion> versions, CancellationToken token)
    {
        var schemaIds = versions.Where(version => version.SchemaVersionId is not null)
            .Select(version => version.SchemaVersionId!.Value.Value).Distinct().ToArray();
        var schemas = schemaIds.Length == 0 ? [] : await ReadAsync($"SELECT {Columns} FROM versions v WHERE version_id = ANY($1)", token, schemaIds);
        var definitions = schemas.ToDictionary(version => version.VersionId, version => (EntitySchema)version.Content);
        var references = versions.ToDictionary(version => version.VersionId, version => VersionRules.GetReferences(version.Submission,
            version.SchemaVersionId is { } id ? definitions[id] : null)
            .Where(reference => reference.Position is not ("/previousVersionId" or "/schemaVersionId")).ToArray());
        var targets = references.Values.SelectMany(items => items).Select(reference => reference.Target.Value).Distinct().ToArray();
        var owners = new Dictionary<Guid, EntityId>();
        if (targets.Length > 0)
        {
            await using var command = Command("""
                SELECT version_id, entity_id FROM versions WHERE version_id = ANY($1)
                UNION ALL SELECT version_id, entity_id FROM deleted_versions WHERE version_id = ANY($1)
                """, [targets]);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) owners.Add(reader.GetGuid(0), new(reader.GetGuid(1)));
        }
        return references.ToDictionary(pair => pair.Key, pair => pair.Value.Select(reference =>
            new ResolvedReference(reference.Position, reference.Target, owners[reference.Target.Value])).ToArray());
    }

    internal async Task RebuildProjectionsAsync(CancellationToken token)
    {
        // Only projections are replaced; immutable versions and deletion identities are untouched.
        var versions = await ReadAsync($"SELECT {Columns} FROM versions v ORDER BY ordinal, entity_id", token);
        var references = await ResolveReferencesAsync(versions, token);
        await ExecuteAsync("DELETE FROM version_contents; DELETE FROM version_references; DELETE FROM observations; DELETE FROM schemas; DELETE FROM entities", token);
        await ExecuteAsync("""
            INSERT INTO entities(entity_id, category, schema_id, current_version_id)
            SELECT DISTINCT ON (v.entity_id) v.entity_id, v.category, s.entity_id, v.version_id
            FROM versions v LEFT JOIN versions s ON s.version_id = v.schema_version_id
            ORDER BY v.entity_id, v.ordinal DESC
            """, token);
        await ExecuteAsync("""
            INSERT INTO schemas(entity_id, resource_name)
            SELECT e.entity_id, v.content->>'resourceName' FROM entities e
            JOIN versions v ON v.version_id = e.current_version_id WHERE e.category = 'entity_schema'
            """, token);
        foreach (var version in versions) await SaveReferencesAsync(version.VersionId, references[version.VersionId], token);
        var definitions = versions.Where(version => version.Content is EntitySchema).ToDictionary(version => version.VersionId, version => (EntitySchema)version.Content);
        foreach (var version in versions)
            await SaveContentsAsync(version.VersionId, BinaryValue.Collect(version.Submission,
                version.SchemaVersionId is { } schema ? definitions[schema] : null), token);
        var current = await ReadAsync($"SELECT {Columns} FROM versions v JOIN entities e ON e.current_version_id = v.version_id WHERE v.category = 'observation'", token);
        foreach (var version in current)
        {
            // Reuse the ordinary projection conversion, without rewriting the version or duplicating its edges.
            await SaveProjectionsAsync(version, null, [], token);
        }
    }
}
