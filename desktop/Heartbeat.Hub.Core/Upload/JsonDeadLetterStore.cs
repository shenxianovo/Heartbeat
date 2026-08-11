using Heartbeat.Hub.Core.Http;
using System.Text.Json;

namespace Heartbeat.Hub.Core.Upload;

/// <summary>Durable, atomically updated JSON dead-letter ledger.</summary>
public sealed class JsonDeadLetterStore<T> : IDeadLetterStore<T>
{
    private readonly string _filePath;
    private readonly object _gate = new();
    private List<DeadLetterEntry<T>> _entries;

    public JsonDeadLetterStore(string filePath)
    {
        _filePath = filePath;
        _entries = LoadExisting();
    }

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    public string? Location => _filePath;

    public void Append(string stream, T item, ApiResult rejection)
    {
        lock (_gate)
        {
            var replacement = new List<DeadLetterEntry<T>>(_entries)
            {
                new(
                    DateTimeOffset.UtcNow,
                    stream,
                    rejection.StatusCode,
                    rejection.ResponseBody,
                    item)
            };
            WriteAtomic(replacement);
            _entries = replacement;
        }
    }

    private List<DeadLetterEntry<T>> LoadExisting()
    {
        if (!File.Exists(_filePath)) return [];
        var document = JsonSerializer.Deserialize<DeadLetterEnvelope<T>>(
            File.ReadAllText(_filePath), SerializerOptions());
        if (document?.SchemaVersion != 1)
            throw new JsonException("Unsupported dead-letter schema version.");
        return document.Entries ?? [];
    }

    private void WriteAtomic(List<DeadLetterEntry<T>> entries)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var tempPath = _filePath + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(new DeadLetterEnvelope<T>(1, entries), SerializerOptions());
            File.WriteAllText(tempPath, json);
            _ = JsonSerializer.Deserialize<DeadLetterEnvelope<T>>(
                File.ReadAllText(tempPath), SerializerOptions())
                ?? throw new JsonException("Dead-letter replacement could not be validated.");
            File.Move(tempPath, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private static JsonSerializerOptions SerializerOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private sealed record DeadLetterEnvelope<TItem>(int SchemaVersion, List<DeadLetterEntry<TItem>> Entries);
    private sealed record DeadLetterEntry<TItem>(
        DateTimeOffset RejectedAt,
        string Stream,
        int? StatusCode,
        string? ResponseBody,
        TItem Item);
}
