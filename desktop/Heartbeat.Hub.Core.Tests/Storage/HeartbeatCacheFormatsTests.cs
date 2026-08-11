using Heartbeat.Core.DTOs.Input;
using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Hub.Core.Storage;
using System.Text.Json;

namespace Heartbeat.Hub.Core.Tests.Storage;

public sealed class HeartbeatCacheFormatsTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"heartbeat-production-cache-formats-{Guid.NewGuid()}");

    public HeartbeatCacheFormatsTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void SegmentV1_RoundTripsAllCurrentFieldsThroughProductionPersistenceDto()
    {
        var path = Path.Combine(_directory, "segments.json");
        var segment = new ActivitySegmentItem
        {
            Id = Guid.CreateVersion7(),
            Source = "browser",
            IdentityKey = "https://example.com",
            AppIdentityKey = "win:code",
            AppName = "code",
            Title = "HeartbeatCacheFormats.cs",
            StartTime = DateTimeOffset.Parse("2026-08-11T01:00:00Z"),
            EndTime = DateTimeOffset.Parse("2026-08-11T01:01:00Z"),
            Attributes = JsonSerializer.SerializeToElement(new { repository = "Heartbeat" })
        };
        using (var cache = NewSegmentCache(path)) cache.Add([segment]);

        using var restarted = NewSegmentCache(path);
        var loaded = Assert.Single(restarted.Load());
        Assert.Equal(segment.Id, loaded.Id);
        Assert.Equal("win:code", loaded.AppIdentityKey);
        Assert.Equal("code", loaded.AppName);
        Assert.Equal("Heartbeat", loaded.Attributes!.Value.GetProperty("repository").GetString());
    }

    [Fact]
    public void UnversionedProductionCaches_MigrateThroughDedicatedLegacyDtos()
    {
        var segmentPath = Path.Combine(_directory, "legacy-segments.json");
        var inputPath = Path.Combine(_directory, "legacy-input.json");
        var segmentId = Guid.CreateVersion7();
        var inputId = Guid.CreateVersion7();
        File.WriteAllText(segmentPath, JsonSerializer.Serialize(new[]
        {
            new
            {
                Id = segmentId,
                Source = "system",
                IdentityKey = "code\nrepo",
                AppIdentityKey = (string?)null,
                AppName = "code",
                Title = "repo",
                StartTime = DateTimeOffset.Parse("2026-08-11T01:00:00Z"),
                EndTime = DateTimeOffset.Parse("2026-08-11T01:02:00Z"),
                Attributes = (object?)null
            }
        }));
        File.WriteAllText(inputPath, JsonSerializer.Serialize(new[]
        {
            new
            {
                Id = inputId,
                EventType = InputEventType.KeyDown,
                Code = (short)65,
                Timestamp = DateTimeOffset.Parse("2026-08-11T01:00:30Z")
            }
        }));

        using var segments = NewSegmentCache(segmentPath);
        using var inputs = NewInputCache(inputPath);

        var migratedSegment = Assert.Single(segments.Load());
        Assert.Equal(segmentId, migratedSegment.Id);
        Assert.Equal("code\nrepo", migratedSegment.IdentityKey);
        Assert.Equal("code", migratedSegment.AppName);
        Assert.Null(migratedSegment.AppIdentityKey);
        var migratedInput = Assert.Single(inputs.Load());
        Assert.Equal(inputId, migratedInput.Id);
        Assert.Equal((short)65, migratedInput.Code);
        Assert.Equal(CacheFileState.Migrated, segments.Status.State);
        Assert.Equal(CacheFileState.Migrated, inputs.Status.State);
    }

    private static JsonFileCache<ActivitySegmentItem> NewSegmentCache(string path) => new(
        path,
        20_000,
        HeartbeatCacheFormats.SegmentVersion1(),
        HeartbeatCacheFormats.SegmentMigrations());

    private static JsonFileCache<InputEventItem> NewInputCache(string path) => new(
        path,
        100_000,
        HeartbeatCacheFormats.InputEventVersion1(),
        HeartbeatCacheFormats.InputEventMigrations());
}
