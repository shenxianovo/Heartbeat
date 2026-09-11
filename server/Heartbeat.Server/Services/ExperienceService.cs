using System.Text.Json;
using Heartbeat.Core.DTOs.Facts;
using Heartbeat.Server.Calendar;
using Heartbeat.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services;

public sealed class ExperienceSegment : ObservationResponse
{
    public Guid? StreamId { get; set; }
    public Guid? FactId { get; set; }
    public long Revision { get; set; }
    public long? DeviceId { get; set; }
    public string? Source { get; set; }
    public long? AppId { get; set; }
    public long? AppIdentityId { get; set; }
    public string? AppName { get; set; }
    public string? AppKey { get; set; }
    public string? Aspect { get; set; }
    public DateTimeOffset StartTime { get; set; }
    public DateTimeOffset EndTime { get; set; }
    public JsonElement Payload { get; set; }
}
public sealed record ExperiencePage(List<ExperienceSegment> Items, Guid? NextCursor);

/// <summary>Bounded raw Segment reads; display semantics belong to Dashboard Fact Views.</summary>
public sealed class ExperienceService(AppDbContext db)
{
    public const int PageSize = 500;

    public async Task<ExperiencePage> ReadAsync(string ownerId, ResolvedCalendarWindow window, Guid? after, CancellationToken ct = default)
    {
        var query = db.Segments.AsNoTracking().Where(s => s.OwnerId == ownerId &&
            s.StartTime < window.EndExclusive && (s.EndTime > window.Start ||
                (s.StartTime == s.EndTime && s.StartTime == window.Start)));
        if (after.HasValue) query = query.Where(s => s.Id.CompareTo(after.Value) > 0);
        var result = (from s in query
                          join attribution in db.FactAttributions on s.Id equals attribution.Id
                          join a in db.Apps on attribution.AppId equals (long?)a.Id into apps
                          from app in apps.DefaultIfEmpty()
                          orderby s.Id
                          select new ExperienceSegment
                          {
                              Id = s.Id, StreamId = s.StreamId, FactId = s.FactId, Revision = s.Revision,
                              CollectorId = s.ObserverId, FoiId = s.FoiId, DeviceId = attribution.DeviceId,
                              Source = s.Source, Aspect = s.Aspect, AppIdentityId = s.AppIdentityId,
                              AppId = attribution.AppId, AppName = app != null ? app.DisplayName : null, AppKey = app != null ? app.Key : null,
                              StartTime = s.StartTime, EndTime = s.EndTime, Payload = ObservationQuery.ReadPayload(s.Payload)
                          }).Take(PageSize + 1);
        var rows = await new ObservationQuery(db).Read(ownerId, result, ct);
        var hasMore = rows.Count > PageSize;
        var items = rows.Take(PageSize).ToList();
        return new ExperiencePage(items, hasMore ? items[^1].Id : null);
    }
}
