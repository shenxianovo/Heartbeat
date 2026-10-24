using System.Text.Json;
using Heartbeat.Core;
using Microsoft.Data.Sqlite;

namespace Heartbeat.Observers.ForegroundState;

internal sealed class ForegroundStore
{
    private readonly string _connectionString;

    internal ForegroundStore(string path, EntityId observerId)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS owner (id INTEGER PRIMARY KEY CHECK (id = 1), observer_id TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS active_period (
                id INTEGER PRIMARY KEY CHECK (id = 1), observation_id TEXT NOT NULL, payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS outbox (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT, observation_id TEXT NOT NULL UNIQUE,
                payload TEXT NOT NULL);
            INSERT OR IGNORE INTO owner (id, observer_id) VALUES (1, $observer);
            """, ("$observer", observerId.Value.ToString()));
        using var check = connection.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = "SELECT observer_id FROM owner WHERE id = 1";
        if ((string?)check.ExecuteScalar() != observerId.Value.ToString())
            throw new InvalidDataException("The local state belongs to another observer; it was not changed.");
        transaction.Commit();
    }

    internal void Checkpoint(ForegroundCapture active, ForegroundCapture? completed)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        if (completed is not null)
            Execute(connection, transaction,
                "INSERT INTO outbox (observation_id, payload) VALUES ($id, $payload)",
                ("$id", completed.ObservationId.Value.ToString()), ("$payload", Payload(completed)));
        Execute(connection, transaction, """
            INSERT INTO active_period (id, observation_id, payload) VALUES (1, $id, $payload)
            ON CONFLICT (id) DO UPDATE SET observation_id = excluded.observation_id, payload = excluded.payload;
            """, ("$id", active.ObservationId.Value.ToString()), ("$payload", Payload(active)));
        transaction.Commit();
    }

    // Used both at shutdown and after a crash: the last durable confirmation closes the period.
    internal void FinishActive()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, """
            INSERT INTO outbox (observation_id, payload) SELECT observation_id, payload FROM active_period;
            DELETE FROM active_period;
            """);
        transaction.Commit();
    }

    internal IReadOnlyList<OutboxPeriod> Pending()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sequence, observation_id, payload FROM outbox ORDER BY sequence";
        using var reader = command.ExecuteReader();
        var result = new List<OutboxPeriod>();
        while (reader.Read())
            result.Add(new(reader.GetInt64(0), Guid.Parse(reader.GetString(1)),
                JsonSerializer.Deserialize<ForegroundCapture>(reader.GetString(2), JsonSerializerOptions.Web)
                    ?? throw new InvalidDataException("Invalid outbox payload.")));
        return result;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA synchronous = FULL;";
            command.ExecuteNonQuery();
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    private static string Payload(ForegroundCapture capture) =>
        JsonSerializer.Serialize(capture, JsonSerializerOptions.Web);

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        command.ExecuteNonQuery();
    }
}
