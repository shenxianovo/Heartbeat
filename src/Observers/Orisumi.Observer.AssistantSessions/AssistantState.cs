using System.Text.Json;
using Microsoft.Data.Sqlite;
using Orisumi.Observer.Sdk;

namespace Orisumi.Observers.AssistantSessions;

internal sealed record EntityEntry(Guid EntityId, Guid? ObservationId = null, string? SessionKey = null,
    SessionInfo? Session = null, bool Deleted = false);

// Business identities/checkpoints are independent of Runtime versions. The durable outbox bridges their transactions.
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
            CREATE TABLE IF NOT EXISTS pending(singleton INTEGER PRIMARY KEY CHECK(singleton=1), id TEXT NOT NULL, payload TEXT NOT NULL, changes TEXT NOT NULL);
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

    internal IReadOnlyList<KeyValuePair<string, EntityEntry>> FindEntity(Guid id)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT key,payload FROM state WHERE json_extract(payload,'$.entityId')=$id OR json_extract(payload,'$.observationId')=$id";
        command.Parameters.AddWithValue("$id", id.ToString());
        using var reader = command.ExecuteReader();
        var entries = new List<KeyValuePair<string, EntityEntry>>();
        while (reader.Read()) entries.Add(new(reader.GetString(0), JsonSerializer.Deserialize<EntityEntry>(reader.GetString(1), JsonSerializerOptions.Web)!));
        return entries;
    }

    internal async Task SubmitAsync(IObserverSession session, JsonElement payload, Dictionary<string, JsonElement> changes, CancellationToken token)
    {
        if (payload.GetProperty("operations").GetArrayLength() == 0) { Apply(changes); return; }
        if (!Fits(payload, AssistantLimits.Default))
            throw new InvalidDataException("Source segment exceeds the Runtime's batch limits; its checkpoint was not advanced.");
        var id = Guid.CreateVersion7().ToString();
        var json = payload.GetRawText();
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO pending(singleton,id,payload,changes) VALUES(1,$id,$payload,$changes)";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$payload", json);
            command.Parameters.AddWithValue("$changes", JsonSerializer.Serialize(changes));
            command.ExecuteNonQuery();
        }
        await ResumeAsync(session, token);
    }

    internal static bool Fits(JsonElement payload, AssistantLimits limits) =>
        payload.GetProperty("operations").GetArrayLength() <= limits.BatchOperations
        && System.Text.Encoding.UTF8.GetByteCount(payload.GetRawText()) <= limits.BatchBytes;

    internal async Task ResumeAsync(IObserverSession session, CancellationToken token)
    {
        string id, payload, changes;
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "SELECT id,payload,changes FROM pending WHERE singleton=1";
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return;
            id = reader.GetString(0); payload = reader.GetString(1); changes = reader.GetString(2);
        }
        using var document = JsonDocument.Parse(payload);
        await session.RequestAsync("commit", document.RootElement, id, token);
        Apply(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(changes)!, clearPending: true);
    }

    internal void Apply(Dictionary<string, JsonElement> changes, bool clearPending = false)
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
        if (clearPending)
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM pending";
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void Dispose() => _connection.Dispose();
    internal static string Key(params string[] parts) => JsonSerializer.Serialize(parts);
}
