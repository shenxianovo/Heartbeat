using System.Text.Json;
using NpgsqlTypes;
using Orisumi.Application.Entities;
using Orisumi.Core;

namespace Orisumi.Infrastructure.Database;

internal sealed partial class PostgresEntityTransaction
{
    public async Task<bool> IsHostInstanceAsync(EntityId id, Guid host, CancellationToken cancellationToken) =>
        (bool)(await ScalarAsync("SELECT EXISTS(SELECT 1 FROM observer_instances WHERE entity_id = $1 AND host_id = $2 AND NOT removed)", cancellationToken, id.Value, host))!;

    public async Task<T> SavepointAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        await transaction.SaveAsync("entity_write", cancellationToken);
        try
        {
            var result = await operation();
            await transaction.ReleaseAsync("entity_write", cancellationToken);
            return result;
        }
        catch (ModelViolationException)
        {
            await transaction.RollbackAsync("entity_write", cancellationToken);
            await transaction.ReleaseAsync("entity_write", cancellationToken);
            throw;
        }
    }

    public async Task<EntityId?> FindRecognitionAsync(EntityId schemaId, RecognitionKey key, CancellationToken cancellationToken) =>
        await ScalarAsync("SELECT entity_id FROM recognition_keys WHERE schema_id = $1 AND scope IS NOT DISTINCT FROM $2 AND scheme = $3 AND value = $4",
            cancellationToken, schemaId.Value, Typed(NpgsqlDbType.Text, key.Scope), key.Scheme, key.Value) is Guid id ? new(id) : null;

    public Task RegisterRecognitionAsync(EntityId schemaId, RecognitionKey key, EntityId entityId, CancellationToken cancellationToken) =>
        ExecuteAsync("INSERT INTO recognition_keys(schema_id, scope, scheme, value, entity_id) VALUES($1,$2,$3,$4,$5) ON CONFLICT DO NOTHING",
            cancellationToken, schemaId.Value, Typed(NpgsqlDbType.Text, key.Scope), key.Scheme, key.Value, entityId.Value);

    public async Task<WriteStreamState> ReadStreamAsync(EntityId observer, CancellationToken cancellationToken)
    {
        await using var command = Command("SELECT last_sequence, schema_version_ids, description_version_id FROM write_streams WHERE observer_entity_id = $1", [observer.Value]);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return new(0, []);
        using var schemas = JsonDocument.Parse(reader.GetString(1));
        return new(reader.GetInt64(0), schemas.RootElement.EnumerateObject().ToDictionary(p => new EntityId(Guid.Parse(p.Name)), p => new VersionId(p.Value.GetGuid())), reader.IsDBNull(2) ? null : new(reader.GetGuid(2)));
    }

    public Task SaveStreamAsync(EntityId observer, WriteStreamState stream, CancellationToken cancellationToken) =>
        ExecuteAsync("""
            INSERT INTO write_streams(observer_entity_id, last_sequence, schema_version_ids, description_version_id) VALUES($1,$2,$3,$4)
            ON CONFLICT(observer_entity_id) DO UPDATE SET last_sequence = excluded.last_sequence, schema_version_ids = excluded.schema_version_ids, description_version_id = excluded.description_version_id
            """, cancellationToken, observer.Value, stream.LastSequence,
            Typed(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(stream.SchemaVersionIds.ToDictionary(p => p.Key.Value, p => p.Value.Value))), Typed(NpgsqlDbType.Uuid, stream.DescriptionVersionId?.Value));

}
