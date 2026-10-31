using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Orisumi.Observers.AssistantSessions;

// Reading progress and pending source context are committed after writes are accepted.
internal sealed class AssistantState : IDisposable
{
    private readonly SqliteConnection _connection;
    internal AssistantState(string directory)
    {
        Directory.CreateDirectory(directory);
        _connection = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "capture.sqlite"), Pooling = false }.ToString());
        _connection.Open();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS state(key TEXT PRIMARY KEY, payload TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
    }

    internal T? Read<T>(string key)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT payload FROM state WHERE key=$key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<T>(json, JsonSerializerOptions.Web) : default;
    }

    internal void Apply(Dictionary<string, JsonElement> changes)
    {
        using var transaction = _connection.BeginTransaction();
        foreach (var (key, value) in changes)
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO state(key,payload) VALUES($key,$payload) ON CONFLICT(key) DO UPDATE SET payload=excluded.payload";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$payload", value.GetRawText());
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void Dispose() => _connection.Dispose();
    internal static string Key(params string[] parts) => JsonSerializer.Serialize(parts);
}
