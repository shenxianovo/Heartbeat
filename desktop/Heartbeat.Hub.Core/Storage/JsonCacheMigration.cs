using System.Text.Json;

namespace Heartbeat.Hub.Core.Storage;

public interface IJsonCacheMigration<T>
{
    int? SourceVersion { get; }
    int TargetVersion { get; }
    List<T> Migrate(JsonElement root);
}

/// <summary>Reads one legacy schema through its own persistence DTO and maps it to current items.</summary>
public sealed class JsonCacheMigration<T, TLegacy> : IJsonCacheMigration<T>
{
    private readonly bool _unversionedArray;
    private readonly Func<TLegacy, T> _map;

    private JsonCacheMigration(
        int? sourceVersion,
        int targetVersion,
        bool unversionedArray,
        Func<TLegacy, T> map)
    {
        SourceVersion = sourceVersion;
        TargetVersion = targetVersion;
        _unversionedArray = unversionedArray;
        _map = map;
    }

    public int? SourceVersion { get; }
    public int TargetVersion { get; }

    public static JsonCacheMigration<T, TLegacy> FromUnversionedArray(
        int targetVersion,
        Func<TLegacy, T> map) => new(null, targetVersion, true, map);

    public static JsonCacheMigration<T, TLegacy> FromVersion(
        int sourceVersion,
        int targetVersion,
        Func<TLegacy, T> map) => new(sourceVersion, targetVersion, false, map);

    public List<T> Migrate(JsonElement root)
    {
        var items = _unversionedArray ? root : root.GetProperty("items");
        if (items.ValueKind != JsonValueKind.Array)
            throw new JsonException("Legacy cache items must be a JSON array.");

        var persisted = items.Deserialize<List<TLegacy>>(
            JsonCacheFileFormat<T, TLegacy>.SerializerOptions)
            ?? throw new JsonException("Legacy cache items could not be deserialized.");
        return persisted.Select(_map).ToList();
    }
}
