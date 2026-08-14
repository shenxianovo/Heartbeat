using Heartbeat.Core;
using Heartbeat.Core.DTOs.Input;
using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Collection.Hub.Storage;
using System.Text.Json;

namespace Heartbeat.Collection.Hub.Tests.Storage;

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
    public void SegmentV2_RoundTripsStrictFields_WithoutPersistingLegacyAppName()
    {
        var path = Path.Combine(_directory, "segments.json");
        var segment = new ActivitySegmentItem
        {
            Id = Guid.CreateVersion7(),
            Source = "browser",
            IdentityKey = "https://example.com",
            AppIdentityKey = "win:code",
            AppDisplayName = "Code",
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
        Assert.Equal("Code", loaded.AppDisplayName);
        Assert.Null(loaded.AppName);
        Assert.Equal("Heartbeat", loaded.Attributes!.Value.GetProperty("repository").GetString());
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(2, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.False(document.RootElement.GetProperty("items")[0].TryGetProperty("appName", out _));
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
        Assert.Equal("win:code", migratedSegment.AppIdentityKey);
        Assert.Equal("code", migratedSegment.AppDisplayName);
        Assert.Null(migratedSegment.AppName);
        var migratedInput = Assert.Single(inputs.Load());
        Assert.Equal(inputId, migratedInput.Id);
        Assert.Equal((short)65, migratedInput.Code);
        Assert.Equal(InputCodeSets.WindowsVirtualKeyV1, migratedInput.CodeSet);
        Assert.Equal(CacheFileState.Migrated, segments.Status.State);
        Assert.Equal(CacheFileState.Migrated, inputs.Status.State);
    }

    [Fact]
    public void InputV1_MigratesToV2_PreservesRawFacts_Backup_AndMigratesOnlyOnce()
    {
        var path = Path.Combine(_directory, "input-v1.json");
        var id = Guid.CreateVersion7();
        var timestamp = DateTimeOffset.Parse("2026-08-11T01:00:30Z");
        var legacyJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            items = new[] { new { id, eventType = InputEventType.KeyDown, code = (short)65, timestamp } }
        });
        File.WriteAllText(path, legacyJson);

        string backupPath;
        using (var cache = NewInputCache(path))
        {
            var migrated = Assert.Single(cache.Load());
            Assert.Equal(id, migrated.Id);
            Assert.Equal(InputEventType.KeyDown, migrated.EventType);
            Assert.Equal(InputCodeSets.WindowsVirtualKeyV1, migrated.CodeSet);
            Assert.Equal((short)65, migrated.Code);
            Assert.Equal(timestamp, migrated.Timestamp);
            Assert.Equal(CacheFileState.Migrated, cache.Status.State);
            backupPath = Assert.IsType<string>(cache.Status.BackupPath);
            Assert.Equal(legacyJson, File.ReadAllText(backupPath));
        }

        using var restarted = NewInputCache(path);
        var reloaded = Assert.Single(restarted.Load());
        Assert.Equal(CacheFileState.Ready, restarted.Status.State);
        Assert.Equal(InputCodeSets.WindowsVirtualKeyV1, reloaded.CodeSet);
        Assert.Equal([backupPath], FindBackups(path));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(2, document.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public void InputV2_RoundTripsPhysicalCodeSet()
    {
        var path = Path.Combine(_directory, "input-v2.json");
        var item = new InputEventItem
        {
            Id = Guid.CreateVersion7(),
            EventType = InputEventType.KeyDown,
            CodeSet = InputCodeSets.HeartbeatKeyPositionV1,
            Code = (short)InputKeyPosition.KeyA,
            Timestamp = DateTimeOffset.Parse("2026-08-11T01:00:30Z")
        };
        using (var cache = NewInputCache(path)) cache.Add([item]);

        using var restarted = NewInputCache(path);
        var loaded = Assert.Single(restarted.Load());
        Assert.Equal(item.Id, loaded.Id);
        Assert.Equal(item.CodeSet, loaded.CodeSet);
        Assert.Equal(item.Code, loaded.Code);
    }

    [Fact]
    public void SegmentV1_MigratesDirectlyToV2_AndPreservesOriginalIdentityKey()
    {
        var path = Path.Combine(_directory, "segments-v1.json");
        var id = Guid.CreateVersion7();
        const string originalIdentityKey = "Code\nMain.cs";
        var legacyJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            items = new[]
            {
                new
                {
                    id,
                    source = "system",
                    identityKey = originalIdentityKey,
                    appIdentityKey = (string?)null,
                    appName = "Code.exe",
                    title = "Main.cs",
                    startTime = DateTimeOffset.Parse("2026-08-11T01:00:00Z"),
                    endTime = DateTimeOffset.Parse("2026-08-11T01:05:00Z"),
                    attributes = (object?)null
                }
            }
        });
        File.WriteAllText(path, legacyJson);

        string backupPath;
        using (var cache = NewSegmentCache(path))
        {
            var migrated = Assert.Single(cache.Load());
            Assert.Equal("win:code", migrated.AppIdentityKey);
            Assert.Equal("Code.exe", migrated.AppDisplayName);
            Assert.Equal(originalIdentityKey, migrated.IdentityKey);
            Assert.Null(migrated.AppName);
            Assert.Equal(CacheFileState.Migrated, cache.Status.State);
            backupPath = Assert.IsType<string>(cache.Status.BackupPath);
            Assert.Equal(legacyJson, File.ReadAllText(backupPath));
        }

        using var restarted = NewSegmentCache(path);
        Assert.Equal(CacheFileState.Ready, restarted.Status.State);
        Assert.Equal(originalIdentityKey, Assert.Single(restarted.Load()).IdentityKey);
        Assert.Equal([backupPath], FindBackups(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacySegmentFormats_MapAway_PreserveExistingIdentity_AndMigrateOnlyOnce(bool versioned)
    {
        var path = Path.Combine(_directory, versioned ? "segments-v1-special.json" : "segments-unversioned-special.json");
        var awayId = Guid.CreateVersion7();
        var identifiedId = Guid.CreateVersion7();
        var items = new[]
        {
            new
            {
                id = awayId,
                source = "system",
                identityKey = "__away__\n",
                appIdentityKey = (string?)null,
                appName = "__away__",
                title = (string?)null,
                startTime = DateTimeOffset.Parse("2026-08-11T01:00:00Z"),
                endTime = DateTimeOffset.Parse("2026-08-11T01:01:00Z"),
                attributes = (object?)null
            },
            new
            {
                id = identifiedId,
                source = "system",
                identityKey = "ORIGINAL-BYTES\r\nTitle",
                appIdentityKey = (string?)"mac:com.microsoft.VSCode",
                appName = "Code.exe",
                title = (string?)"Title",
                startTime = DateTimeOffset.Parse("2026-08-11T01:02:00Z"),
                endTime = DateTimeOffset.Parse("2026-08-11T01:03:00Z"),
                attributes = (object?)null
            }
        };
        var legacyJson = versioned
            ? JsonSerializer.Serialize(new { schemaVersion = 1, items })
            : JsonSerializer.Serialize(items);
        File.WriteAllText(path, legacyJson);

        string backupPath;
        using (var cache = NewSegmentCache(path))
        {
            var migrated = cache.Load();
            var away = Assert.Single(migrated, item => item.Id == awayId);
            Assert.Equal(AppIdentityKeys.Away, away.AppIdentityKey);
            Assert.Equal("__away__", away.AppDisplayName);
            Assert.Equal("__away__\n", away.IdentityKey);

            var identified = Assert.Single(migrated, item => item.Id == identifiedId);
            Assert.Equal("mac:com.microsoft.vscode", identified.AppIdentityKey);
            Assert.Equal("Code.exe", identified.AppDisplayName);
            Assert.Equal("ORIGINAL-BYTES\r\nTitle", identified.IdentityKey);
            Assert.Null(identified.AppName);

            Assert.Equal(CacheFileState.Migrated, cache.Status.State);
            backupPath = Assert.IsType<string>(cache.Status.BackupPath);
            Assert.Equal(legacyJson, File.ReadAllText(backupPath));
        }

        using var restarted = NewSegmentCache(path);
        Assert.Equal(CacheFileState.Ready, restarted.Status.State);
        Assert.Equal(2, restarted.Load().Count);
        Assert.Equal([backupPath], FindBackups(path));
    }

    private static string[] FindBackups(string path) => Directory.GetFiles(
        Path.GetDirectoryName(path)!,
        Path.GetFileName(path) + ".legacy-*.bak*");

    private static JsonFileCache<ActivitySegmentItem> NewSegmentCache(string path) => new(
        path,
        20_000,
        HeartbeatCacheFormats.SegmentVersion2(),
        HeartbeatCacheFormats.SegmentMigrations());

    private static JsonFileCache<InputEventItem> NewInputCache(string path) => new(
        path,
        100_000,
        HeartbeatCacheFormats.InputEventVersion2(),
        HeartbeatCacheFormats.InputEventMigrations());
}
