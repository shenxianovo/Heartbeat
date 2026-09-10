using Heartbeat.Core.DTOs.Facts;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services;

public sealed partial class FactStore
{
    private static System.Text.Json.JsonElement ReadPayload(System.Text.Json.JsonDocument payload) => payload.RootElement.Clone();

    public Task<List<FactResponse>> ReadSegmentsAsync(string ownerId, long? deviceId,
        DateTimeOffset? start, DateTimeOffset? end, CancellationToken ct = default) => db.Segments
        .Where(f => f.OwnerId == ownerId &&
            (deviceId == null || f.TargetKind == "device" && f.TargetId == deviceId ||
             f.TargetKind == null && f.Stream.Subject.DeviceId == deviceId) &&
            (start == null || f.EndTime >= start) && (end == null || f.StartTime < end))
        .OrderByDescending(f => f.StartTime).ThenBy(f => f.Id).Take(10000)
        .Select(f => new FactResponse
        {
            Id = f.Id, StreamId = f.StreamId, FactId = f.FactId, Revision = f.Revision,
            ObserverId = f.ObserverId, TargetKind = f.TargetKind, TargetId = f.TargetId, Source = f.Source,
            Start = f.StartTime, End = f.EndTime, Payload = ReadPayload(f.Payload)
        }).ToListAsync(ct);

    public Task<List<FactResponse>> ReadEventsAsync(string ownerId, long? deviceId,
        DateTimeOffset? start, DateTimeOffset? end, CancellationToken ct = default) => db.Events
        .Where(f => f.OwnerId == ownerId &&
            (deviceId == null || f.TargetKind == "device" && f.TargetId == deviceId ||
             f.TargetKind == null && f.Stream.Subject.DeviceId == deviceId) &&
            (start == null || f.Timestamp >= start) && (end == null || f.Timestamp < end))
        .OrderByDescending(f => f.Timestamp).ThenBy(f => f.Id).Take(10000)
        .Select(f => new FactResponse
        {
            Id = f.Id, StreamId = f.StreamId, FactId = f.FactId, Revision = f.Revision,
            ObserverId = f.ObserverId, TargetKind = f.TargetKind, TargetId = f.TargetId, Source = f.Source,
            OccurredAt = f.Timestamp, Payload = ReadPayload(f.Payload)
        }).ToListAsync(ct);
}
