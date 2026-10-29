using Orisumi.Application.Observers;
using Orisumi.Core;
using Npgsql;
using NpgsqlTypes;

namespace Orisumi.Infrastructure.Database;

public sealed class PostgresServerObserverStore(NpgsqlDataSource source) : IServerObserverStore
{
    public async Task<EntityId> GetOrCreateAsync(string key, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var command = new NpgsqlCommand("""
            INSERT INTO server_observers (observer_key, entity_id) VALUES ($1, $2)
            ON CONFLICT (observer_key) DO UPDATE SET observer_key = EXCLUDED.observer_key
            RETURNING entity_id
            """, connection);
        command.Parameters.AddWithValue(key);
        command.Parameters.AddWithValue(EntityId.New().Value);
        return new((Guid)(await command.ExecuteScalarAsync(token))!);
    }

    public Task<string?> ReadPendingAsync(EntityId instance, CancellationToken token) =>
        ReadAsync("SELECT payload::text FROM server_observer_submissions WHERE entity_id = $1", instance, token);
    public Task<string?> ReadStateAsync(EntityId instance, CancellationToken token) =>
        ReadAsync("SELECT payload::text FROM server_observer_state WHERE entity_id = $1", instance, token);
    public Task SavePendingAsync(EntityId instance, string payload, CancellationToken token) =>
        WriteAsync("INSERT INTO server_observer_submissions (entity_id, payload) VALUES ($1, $2)", instance, payload, token);
    public Task SaveStateAsync(EntityId instance, string payload, CancellationToken token) =>
        WriteAsync("""
            INSERT INTO server_observer_state (entity_id, payload) VALUES ($1, $2)
            ON CONFLICT (entity_id) DO UPDATE SET payload = EXCLUDED.payload
            """, instance, payload, token);
    public Task ClearPendingAsync(EntityId instance, CancellationToken token) =>
        WriteAsync("DELETE FROM server_observer_submissions WHERE entity_id = $1", instance, null, token);

    private async Task<string?> ReadAsync(string sql, EntityId instance, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(instance.Value);
        return (string?)await command.ExecuteScalarAsync(token);
    }

    private async Task WriteAsync(string sql, EntityId instance, string? payload, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(instance.Value);
        if (payload is not null) command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, payload);
        await command.ExecuteNonQueryAsync(token);
    }
}
