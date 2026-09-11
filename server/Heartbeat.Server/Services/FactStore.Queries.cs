using Heartbeat.Core.DTOs.Facts;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services;

public sealed partial class FactStore
{
    private static System.Text.Json.JsonElement ReadPayload(System.Text.Json.JsonDocument payload) => payload.RootElement.Clone();

    public async Task<List<FactResponse>> ReadSegmentsAsync(string ownerId, long? deviceId,
        DateTimeOffset? start, DateTimeOffset? end, CancellationToken ct = default, long? appId = null, long? accountId = null, Guid? foiId = null)
    {
        var query =
        (from f in db.Segments
         join attribution in db.FactAttributions on f.Id equals attribution.Id
         where f.OwnerId == ownerId && (deviceId == null || attribution.DeviceId == deviceId) &&
             (appId == null || attribution.AppId == appId) && (accountId == null || attribution.AccountId == accountId) && (foiId == null || f.FoiId == foiId) &&
             (start == null || f.EndTime >= start) && (end == null || f.StartTime < end)
         orderby f.StartTime descending, f.Id
         select new FactResponse
         {
             Id = f.Id, Kind = f.Kind, StreamId = f.StreamId, FactId = f.FactId, Revision = f.Revision,
             ObserverId = f.ObserverId, FoiId = f.FoiId, Aspect = f.Aspect,
             Source = f.Source,
             DeviceId = attribution.DeviceId, AppId = attribution.AppId,
             Start = f.StartTime, End = f.EndTime, Payload = ReadPayload(f.Payload)
         }).Take(10000);
        return await new ObservationQuery(db).Read(ownerId, query, ct);
    }

    public async Task<List<FactResponse>> ReadEventsAsync(string ownerId, long? deviceId,
        DateTimeOffset? start, DateTimeOffset? end, CancellationToken ct = default, long? appId = null, long? accountId = null, Guid? foiId = null)
    {
        var query =
        (from f in db.Events
         join attribution in db.FactAttributions on f.Id equals attribution.Id
         where f.OwnerId == ownerId && (deviceId == null || attribution.DeviceId == deviceId) &&
             (appId == null || attribution.AppId == appId) && (accountId == null || attribution.AccountId == accountId) && (foiId == null || f.FoiId == foiId) &&
             (start == null || f.Timestamp >= start) && (end == null || f.Timestamp < end)
         orderby f.Timestamp descending, f.Id
         select new FactResponse
         {
             Id = f.Id, Kind = f.Kind, StreamId = f.StreamId, FactId = f.FactId, Revision = f.Revision,
             ObserverId = f.ObserverId, FoiId = f.FoiId, Aspect = f.Aspect,
             Source = f.Source,
             DeviceId = attribution.DeviceId, AppId = attribution.AppId,
             OccurredAt = f.Timestamp, Payload = ReadPayload(f.Payload)
         }).Take(10000);
        return await new ObservationQuery(db).Read(ownerId, query, ct);
    }
}
