using System.Text.Json;
using Orisumi.Application.Entities;
using Orisumi.Core;
using Npgsql;
using NpgsqlTypes;

namespace Orisumi.Infrastructure.Database;

internal sealed partial class PostgresEntityTransaction(NpgsqlConnection connection, NpgsqlTransaction transaction,
    TimeProvider timeProvider) : IEntityTransaction
{
    private const string Columns = "v.entity_id, v.version_id, v.previous_version_id, v.schema_version_id, v.category, v.recorded_at, v.content";
    private const string Savepoint = "version_save";
    public Task CreateSavepointAsync(CancellationToken cancellationToken) => transaction.SaveAsync(Savepoint, cancellationToken);
    public Task RollbackSavepointAsync(CancellationToken cancellationToken) => transaction.RollbackAsync(Savepoint, cancellationToken);
    public Task ReleaseSavepointAsync(CancellationToken cancellationToken) => transaction.ReleaseAsync(Savepoint, cancellationToken);

    public async Task<EntityVersion?> ReadVersionAsync(VersionId id, CancellationToken cancellationToken) =>
        (await ReadAsync($"SELECT {Columns} FROM versions v WHERE v.version_id = $1", cancellationToken, id.Value)).SingleOrDefault();
    public async Task<EntityVersion?> ReadCurrentAsync(EntityId id, CancellationToken cancellationToken) =>
        (await ReadAsync($"SELECT {Columns} FROM entities e JOIN versions v ON v.version_id = e.current_version_id WHERE e.entity_id = $1", cancellationToken, id.Value)).SingleOrDefault();
    public Task<IReadOnlyList<EntityVersion>> ReadVersionsAsync(EntityId id, CancellationToken cancellationToken) =>
        ReadAsync($"SELECT {Columns} FROM versions v WHERE v.entity_id = $1 ORDER BY v.ordinal", cancellationToken, id.Value);
    public async Task<bool> EntityExistsAsync(EntityId id, CancellationToken cancellationToken) =>
        (bool)(await ScalarAsync("SELECT EXISTS(SELECT 1 FROM entities WHERE entity_id = $1)", cancellationToken, id.Value))!;
    public async Task<bool> IsEntityDeletedAsync(EntityId id, CancellationToken cancellationToken) =>
        (bool)(await ScalarAsync("SELECT EXISTS(SELECT 1 FROM entities WHERE entity_id = $1 AND deleted_at IS NOT NULL)", cancellationToken, id.Value))!;
    public async Task<bool> IsVersionDeletedAsync(VersionId id, CancellationToken cancellationToken) =>
        (bool)(await ScalarAsync("SELECT EXISTS(SELECT 1 FROM version_tombstones WHERE version_id = $1)", cancellationToken, id.Value))!;
    public async Task<EntityId?> FindSchemaAsync(string resourceName, CancellationToken cancellationToken) =>
        await ScalarAsync("SELECT entity_id FROM entities WHERE resource_name = $1", cancellationToken, resourceName) is Guid id ? new(id) : null;
    public async Task<EntityId?> FindVersionEntityAsync(VersionId id, CancellationToken cancellationToken) =>
        await ScalarAsync("SELECT entity_id FROM versions WHERE version_id = $1 UNION ALL SELECT entity_id FROM version_tombstones WHERE version_id = $1", cancellationToken, id.Value) is Guid entity ? new(entity) : null;
    public async Task<bool> HasBusinessVersionsAsync(EntityId schemaId, CancellationToken cancellationToken) =>
        (bool)(await ScalarAsync("SELECT EXISTS(SELECT 1 FROM entities WHERE schema_id = $1)", cancellationToken, schemaId.Value))!;

    public Task<IReadOnlyList<EntityVersion>> ListEntitiesAsync(EntityQuery query, CancellationToken cancellationToken)
    {
        var values = new List<object>();
        var filters = new List<string> { "e.deleted_at IS NULL" };
        if (query.Category is { } category) Add("e.category", category);
        if (query.SchemaId is { } schema) Add("e.schema_id", schema.Value);
        if (query.After is { } after) Add("e.entity_id", after.Value, ">");
        values.Add(query.Limit + 1);
        return ReadAsync($"SELECT {Columns} FROM entities e JOIN versions v ON v.version_id = e.current_version_id WHERE {string.Join(" AND ", filters)} ORDER BY e.entity_id LIMIT ${values.Count}", cancellationToken, values.ToArray());
        void Add(string column, object value, string comparison = "=")
        {
            values.Add(value);
            filters.Add($"{column} {comparison} ${values.Count}");
        }
    }

    public Task<IReadOnlyList<EntityVersion>> ListObservationsAsync(ObservationQuery query, CancellationToken cancellationToken)
    {
        var values = new List<object> { VersionTime.Normalize(query.From), VersionTime.Normalize(query.To) };
        var filters = new List<string> { "o.fact_range && tstzrange($1, $2, '[)')" };
        if (query.SchemaId is { } schema)
        {
            values.Add(schema.Value);
            filters.Add($"o.content_schema_id = ${values.Count}");
        }
        if (query.After is { } after)
        {
            values.Add(VersionTime.Normalize(after.Time));
            values.Add(after.EntityId.Value);
            filters.Add($"(lower(o.fact_range), o.entity_id) > (${values.Count - 1}, ${values.Count})");
        }
        values.Add(query.Limit + 1);
        return ReadAsync($"SELECT {Columns} FROM current_observations o JOIN versions v ON v.version_id = o.version_id WHERE {string.Join(" AND ", filters)} ORDER BY lower(o.fact_range), o.entity_id LIMIT ${values.Count}", cancellationToken, values.ToArray());
    }

    public async Task SaveAsync(EntityVersion version, IReadOnlyList<ResolvedReference> references, CancellationToken cancellationToken)
    {
        var schemaId = version.SchemaVersionId is { } schema
            ? await ScalarAsync("SELECT entity_id FROM versions WHERE version_id = $1", cancellationToken, schema.Value) : null;
        var ordinal = (int)(await ScalarAsync("SELECT COALESCE((SELECT v.ordinal FROM entities e JOIN versions v ON v.version_id = e.current_version_id WHERE e.entity_id = $1), 0) + 1", cancellationToken, version.EntityId.Value))!;
        await ExecuteAsync("""
            INSERT INTO entities(entity_id, category, schema_id, resource_name, current_version_id)
            VALUES ($1, $2, $3, $4, $5)
            ON CONFLICT(entity_id) DO UPDATE SET current_version_id = excluded.current_version_id,
                resource_name = excluded.resource_name;
            """, cancellationToken, version.EntityId.Value, version.Category, Typed(NpgsqlDbType.Uuid, schemaId),
            Typed(NpgsqlDbType.Text, (version.Content as EntitySchema)?.ResourceName), version.VersionId.Value);
        await ExecuteAsync("""
            INSERT INTO versions(version_id, entity_id, ordinal, previous_version_id, schema_version_id, category, recorded_at, content)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
            """, cancellationToken, version.VersionId.Value, version.EntityId.Value, ordinal,
            Typed(NpgsqlDbType.Uuid, version.PreviousVersionId?.Value), Typed(NpgsqlDbType.Uuid, version.SchemaVersionId?.Value),
            version.Category, VersionTime.Normalize(version.RecordedAt),
            Typed(NpgsqlDbType.Jsonb, VersionJson.Content(version.Content).GetRawText()));
        await SaveReferencesAsync(version.VersionId, references, cancellationToken);
        if (version.Content is not Observation observation) return;
        var contentSchema = await ScalarAsync("SELECT e.schema_id FROM versions v JOIN entities e ON e.entity_id = v.entity_id WHERE v.version_id = $1", cancellationToken, observation.ContentVersionId.Value);
        NpgsqlRange<DateTimeOffset>? range = null;
        if ((observation.StartAt ?? observation.EndAt) is { } lower)
        {
            var upper = observation.EndAt ?? lower;
            lower = VersionTime.Normalize(lower);
            upper = VersionTime.Normalize(upper);
            range = new(lower, true, upper, lower == upper);
        }
        await ExecuteAsync("""
            INSERT INTO current_observations(entity_id, version_id, content_schema_id, fact_range) VALUES($1, $2, $3, $4)
            ON CONFLICT(entity_id) DO UPDATE SET version_id = excluded.version_id,
                content_schema_id = excluded.content_schema_id, fact_range = excluded.fact_range
            """, cancellationToken, version.EntityId.Value, version.VersionId.Value,
            Typed(NpgsqlDbType.Uuid, contentSchema), Typed(NpgsqlDbType.Range | NpgsqlDbType.TimestampTz, range));
    }

    public async Task DeleteAsync(EntityId id, CancellationToken cancellationToken)
    {
        await ExecuteAsync("INSERT INTO version_tombstones(version_id, entity_id) SELECT version_id, entity_id FROM versions WHERE entity_id = $1", cancellationToken, id.Value);
        await ExecuteAsync("DELETE FROM current_observations WHERE entity_id = $1", cancellationToken, id.Value);
        await ExecuteAsync("DELETE FROM versions WHERE entity_id = $1", cancellationToken, id.Value);
        await ExecuteAsync("UPDATE entities SET category = NULL, schema_id = NULL, resource_name = NULL, current_version_id = NULL, deleted_at = $2 WHERE entity_id = $1",
            cancellationToken, id.Value, VersionTime.Normalize(timeProvider.GetUtcNow()));
    }

    private async Task<IReadOnlyList<EntityVersion>> ReadAsync(string sql, CancellationToken token, params object[] values)
    {
        await using var command = Command(sql, values);
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<EntityVersion>();
        while (await reader.ReadAsync(token))
            result.Add(ReadVersion(reader));
        return result;
    }

    private static EntityVersion ReadVersion(NpgsqlDataReader reader)
    {
        using var content = JsonDocument.Parse(reader.GetString(6));
        return new(new(new(reader.GetGuid(0)), new(reader.GetGuid(1)),
            reader.IsDBNull(2) ? null : new(reader.GetGuid(2)), reader.IsDBNull(3) ? null : new(reader.GetGuid(3)),
            VersionJson.ReadContent(reader.GetString(4), content.RootElement)), reader.GetFieldValue<DateTimeOffset>(5));
    }

    private async Task<object?> ScalarAsync(string sql, CancellationToken token, params object[] values)
    {
        await using var command = Command(sql, values);
        return await command.ExecuteScalarAsync(token);
    }
    private async Task ExecuteAsync(string sql, CancellationToken token, params object[] values)
    {
        await using var command = Command(sql, values);
        await command.ExecuteNonQueryAsync(token);
    }
    private NpgsqlCommand Command(string sql, object[] values)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var value in values)
            command.Parameters.Add(value is NpgsqlParameter parameter ? parameter : new NpgsqlParameter { Value = value });
        return command;
    }
    private static NpgsqlParameter Typed(NpgsqlDbType type, object? value) => new() { NpgsqlDbType = type, Value = value ?? DBNull.Value };
}
