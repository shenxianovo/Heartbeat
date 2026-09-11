using Heartbeat.Core.DTOs.Facts;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services;

public sealed partial class FactStore
{
    private static System.Text.Json.JsonElement ReadPayload(System.Text.Json.JsonDocument payload) => payload.RootElement.Clone();

    public Task<List<FactResponse>> ReadSegmentsAsync(string ownerId, long? deviceId,
        DateTimeOffset? start, DateTimeOffset? end, CancellationToken ct = default, long? appId = null, long? accountId = null) =>
        (from f in db.Segments
         join attribution in db.FactAttributions on f.Id equals attribution.Id
         where f.OwnerId == ownerId && (deviceId == null || attribution.DeviceId == deviceId) &&
             (appId == null || attribution.AppId == appId) && (accountId == null || attribution.AccountId == accountId) &&
             (start == null || f.EndTime >= start) && (end == null || f.StartTime < end)
         orderby f.StartTime descending, f.Id
         select new FactResponse
         {
             Id = f.Id, StreamId = f.StreamId, FactId = f.FactId, Revision = f.Revision,
             ObserverId = f.ObserverId, FoiId = f.FoiId, Aspect = f.Aspect,
             TargetKind = f.TargetKind, TargetId = f.TargetId, Source = f.Source,
             DeviceId = attribution.DeviceId, AppId = attribution.AppId,
             Start = f.StartTime, End = f.EndTime, Payload = ReadPayload(f.Payload)
         }).Take(10000).ToListAsync(ct);

    public Task<List<FactResponse>> ReadEventsAsync(string ownerId, long? deviceId,
        DateTimeOffset? start, DateTimeOffset? end, CancellationToken ct = default, long? appId = null, long? accountId = null) =>
        (from f in db.Events
         join attribution in db.FactAttributions on f.Id equals attribution.Id
         where f.OwnerId == ownerId && (deviceId == null || attribution.DeviceId == deviceId) &&
             (appId == null || attribution.AppId == appId) && (accountId == null || attribution.AccountId == accountId) &&
             (start == null || f.Timestamp >= start) && (end == null || f.Timestamp < end)
         orderby f.Timestamp descending, f.Id
         select new FactResponse
         {
             Id = f.Id, StreamId = f.StreamId, FactId = f.FactId, Revision = f.Revision,
             ObserverId = f.ObserverId, FoiId = f.FoiId, Aspect = f.Aspect,
             TargetKind = f.TargetKind, TargetId = f.TargetId, Source = f.Source,
             DeviceId = attribution.DeviceId, AppId = attribution.AppId,
             OccurredAt = f.Timestamp, Payload = ReadPayload(f.Payload)
         }).Take(10000).ToListAsync(ct);
}
