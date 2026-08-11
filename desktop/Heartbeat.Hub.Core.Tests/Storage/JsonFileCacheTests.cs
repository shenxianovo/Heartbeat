using Heartbeat.Core.DTOs.Input;
using Heartbeat.Hub.Core.Storage;

namespace Heartbeat.Hub.Core.Tests.Storage;

/// <summary>离线缓存唯一生产实现的行为（原 InputEventLocalCacheTests，wrapper 随 ADR-020 退役后直测泛型缓存）。</summary>
public class JsonFileCacheTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var f in _tempFiles)
        {
            if (File.Exists(f)) File.Delete(f);
            if (File.Exists(f + ".tmp")) File.Delete(f + ".tmp");
        }
    }

    private string TempPath()
    {
        var p = Path.Combine(Path.GetTempPath(), $"heartbeat-cache-{Guid.NewGuid()}.json");
        _tempFiles.Add(p);
        return p;
    }

    private JsonFileCache<InputEventItem> NewCache(string? path = null)
        => new(path ?? TempPath(), maxItems: 100_000);

    private static InputEventItem Item() => new()
    {
        Id = Guid.CreateVersion7(),
        EventType = InputEventType.KeyDown,
        Code = 65,
        Timestamp = DateTimeOffset.UtcNow
    };

    [Fact]
    public void Add_ThenLoad_ReturnsItems()
    {
        var cache = NewCache();

        cache.Add([Item(), Item()]);

        Assert.Equal(2, cache.Load().Count);
    }

    [Fact]
    public void Add_EmptyList_NoOp()
    {
        var cache = NewCache();

        cache.Add([]);

        Assert.Empty(cache.Load());
    }

    [Fact]
    public void Clear_EmptiesCache()
    {
        var cache = NewCache();
        cache.Add([Item()]);

        cache.Clear();

        Assert.Empty(cache.Load());
    }

    [Fact]
    public void Persists_AcrossInstances()
    {
        var path = TempPath();
        var cache1 = NewCache(path);
        cache1.Add([Item(), Item(), Item()]);

        var cache2 = NewCache(path);

        Assert.Equal(3, cache2.Load().Count);
    }

    [Fact]
    public void Add_PreservesItemFields()
    {
        var cache = NewCache();
        var item = new InputEventItem
        {
            Id = Guid.CreateVersion7(),
            EventType = InputEventType.MouseScroll,
            Code = 2,
            Timestamp = DateTimeOffset.UtcNow
        };

        cache.Add([item]);
        var loaded = cache.Load().Single();

        Assert.Equal(item.Id, loaded.Id);
        Assert.Equal(InputEventType.MouseScroll, loaded.EventType);
        Assert.Equal((short)2, loaded.Code);
    }

    [Fact]
    public void Add_OverCapacity_DropsOldest()
    {
        var cache = new JsonFileCache<InputEventItem>(TempPath(), maxItems: 3);
        var oldest = Item();

        cache.Add([oldest, Item(), Item(), Item()]);

        var loaded = cache.Load();
        Assert.Equal(3, loaded.Count);
        Assert.DoesNotContain(loaded, i => i.Id == oldest.Id);
    }
}
