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
            INSERT INTO version_references(version_id, path, target_version_id, target_entity_id)
            SELECT * FROM unnest($1::uuid[], $2::text[], $3::uuid[], $4::uuid[])
            """, token, references.Select(_ => versionId.Value).ToArray(),
            references.Select(reference => reference.Position).ToArray(),
            references.Select(reference => reference.TargetVersionId.Value).ToArray(),
            references.Select(reference => reference.TargetEntityId.Value).ToArray());
    }

    public async Task<IReadOnlyList<Referrer>> ListReferrersAsync(ReferrerQuery query, CancellationToken cancellationToken)
    {
        // Limit the distinct versions first, then read every matching position in those versions.
        var current = query.AllVersions ? "" : "AND v.version_id = e.current_version_id";
        await using var command = Command($"""
            WITH page AS (
                SELECT DISTINCT r.version_id
                FROM version_references r
                JOIN versions v ON v.version_id = r.version_id
                JOIN entities e ON e.entity_id = v.entity_id
                WHERE r.target_entity_id = $1 AND ($2::uuid IS NULL OR r.version_id > $2) {current}
                ORDER BY r.version_id LIMIT $3
            )
            SELECT {Columns}, r.path, r.target_version_id
            FROM page p
            JOIN versions v ON v.version_id = p.version_id
            JOIN version_references r ON r.version_id = p.version_id AND r.target_entity_id = $1
            ORDER BY v.version_id
            """, [query.Target.Value, Typed(NpgsqlDbType.Uuid, query.After?.Value), query.Limit + 1]);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<Referrer>();
        List<VersionReference>? references = null;
        while (await reader.ReadAsync(cancellationToken))
        {
            if (result.Count == 0 || result[^1].Version.VersionId.Value != reader.GetGuid(1))
            {
                references = [];
                result.Add(new(ReadVersion(reader), references));
            }
            references!.Add(new(reader.GetString(7), new(reader.GetGuid(8))));
        }
        return result;
    }
}
