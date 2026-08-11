using Heartbeat.Core.DTOs.Input;
using Heartbeat.Core.DTOs.Segments;
using System.Text.Json;

namespace Heartbeat.Hub.Core.Storage;

/// <summary>
/// Production cache schemas. Domain DTOs never serve as persistence DTOs: both the current
/// versioned envelope and the unversioned predecessor have explicit shapes and mappings.
/// Ticket 05/07 can add business conversions as new migrations without changing JsonFileCache.
/// </summary>
public static class HeartbeatCacheFormats
{
    public static IJsonCacheFileFormat<ActivitySegmentItem> SegmentVersion1() =>
        new JsonCacheFileFormat<ActivitySegmentItem, SegmentCacheItemV1>(
            version: 1,
            ToSegmentV1,
            FromSegmentV1);

    public static IReadOnlyList<IJsonCacheMigration<ActivitySegmentItem>> SegmentMigrations() =>
    [
        JsonCacheMigration<ActivitySegmentItem, LegacySegmentCacheItem>.FromUnversionedArray(
            targetVersion: 1,
            item => new ActivitySegmentItem
            {
                Id = item.Id,
                Source = item.Source,
                IdentityKey = item.IdentityKey,
                AppIdentityKey = item.AppIdentityKey,
                AppName = item.AppName,
                Title = item.Title,
                StartTime = item.StartTime,
                EndTime = item.EndTime,
                Attributes = item.Attributes?.Clone()
            })
    ];

    public static IJsonCacheFileFormat<InputEventItem> InputEventVersion1() =>
        new JsonCacheFileFormat<InputEventItem, InputEventCacheItemV1>(
            version: 1,
            item => new InputEventCacheItemV1(item.Id, item.EventType, item.Code, item.Timestamp),
            item => new InputEventItem
            {
                Id = item.Id,
                EventType = item.EventType,
                Code = item.Code,
                Timestamp = item.Timestamp
            });

    public static IReadOnlyList<IJsonCacheMigration<InputEventItem>> InputEventMigrations() =>
    [
        JsonCacheMigration<InputEventItem, LegacyInputEventCacheItem>.FromUnversionedArray(
            targetVersion: 1,
            item => new InputEventItem
            {
                Id = item.Id,
                EventType = item.EventType,
                Code = item.Code,
                Timestamp = item.Timestamp
            })
    ];

    private static SegmentCacheItemV1 ToSegmentV1(ActivitySegmentItem item) => new(
        item.Id,
        item.Source,
        item.IdentityKey,
        item.AppIdentityKey,
        item.AppName,
        item.Title,
        item.StartTime,
        item.EndTime,
        item.Attributes?.Clone());

    private static ActivitySegmentItem FromSegmentV1(SegmentCacheItemV1 item) => new()
    {
        Id = item.Id,
        Source = item.Source,
        IdentityKey = item.IdentityKey,
        AppIdentityKey = item.AppIdentityKey,
        AppName = item.AppName,
        Title = item.Title,
        StartTime = item.StartTime,
        EndTime = item.EndTime,
        Attributes = item.Attributes?.Clone()
    };

    private sealed record SegmentCacheItemV1(
        Guid Id,
        string Source,
        string IdentityKey,
        string? AppIdentityKey,
        string? AppName,
        string? Title,
        DateTimeOffset StartTime,
        DateTimeOffset EndTime,
        JsonElement? Attributes);

    private sealed record LegacySegmentCacheItem(
        Guid Id,
        string Source,
        string IdentityKey,
        string? AppIdentityKey,
        string? AppName,
        string? Title,
        DateTimeOffset StartTime,
        DateTimeOffset EndTime,
        JsonElement? Attributes);

    private sealed record InputEventCacheItemV1(
        Guid Id,
        InputEventType EventType,
        short Code,
        DateTimeOffset Timestamp);

    private sealed record LegacyInputEventCacheItem(
        Guid Id,
        InputEventType EventType,
        short Code,
        DateTimeOffset Timestamp);
}
