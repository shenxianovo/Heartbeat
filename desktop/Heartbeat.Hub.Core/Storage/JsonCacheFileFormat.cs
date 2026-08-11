using System.Text.Json;

namespace Heartbeat.Hub.Core.Storage;

public interface IJsonCacheFileFormat<T>
{
    int Version { get; }
    JsonElement SerializeItems(IReadOnlyList<T> items);
    List<T> DeserializeItems(JsonElement items);
}

/// <summary>
/// A version-specific persistence format. TPersistence is deliberately separate from T so a cache
/// schema can evolve without asking System.Text.Json to hydrate an old file into a current domain DTO.
/// </summary>
public sealed class JsonCacheFileFormat<T, TPersistence>(
    int version,
    Func<T, TPersistence> toPersistence,
    Func<TPersistence, T> fromPersistence) : IJsonCacheFileFormat<T>
{
    internal static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public int Version { get; } = version;

    public JsonElement SerializeItems(IReadOnlyList<T> items) =>
        JsonSerializer.SerializeToElement(items.Select(toPersistence).ToList(), SerializerOptions);

    public List<T> DeserializeItems(JsonElement items)
    {
        if (items.ValueKind != JsonValueKind.Array)
            throw new JsonException("Cache items must be a JSON array.");

        var persisted = items.Deserialize<List<TPersistence>>(SerializerOptions)
            ?? throw new JsonException("Cache items could not be deserialized.");
        return persisted.Select(fromPersistence).ToList();
    }
}
