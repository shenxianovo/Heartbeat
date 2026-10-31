using System.Text.Json;
using Microsoft.Data.Sqlite;
using Orisumi.Observer.Sdk;

namespace Orisumi.Observers.AssistantSessions;

internal sealed record EntityEntry(Guid EntityId, Guid? ObservationId = null, string? SessionKey = null, SessionInfo? Session = null);

// Business identities and checkpoints. They change only after the batch that formed them was saved.
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

    internal async Task SubmitAsync(IObserverSession session, JsonElement payload, Dictionary<string, JsonElement> changes, CancellationToken token)
    {
        if (payload.GetProperty("operations").GetArrayLength() > 0) await session.RequestAsync("commit", payload, token);
        Apply(changes);
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
