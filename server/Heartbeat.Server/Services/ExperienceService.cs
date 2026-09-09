using System.Text.Json;
using Heartbeat.Server.Calendar;
using Heartbeat.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services;

public sealed record ExperienceSegment(
    Guid Id, Guid StreamId, Guid FactId, long Revision,
    Guid SubjectId, string SubjectKind, string? SubjectName,
    string Source, long? AppId, long? AppIdentityId, string? AppName, string? AppKey,
    DateTimeOffset StartTime, DateTimeOffset EndTime, JsonElement Payload);

public sealed record ExperiencePage(List<ExperienceSegment> Items, Guid? NextCursor);

/// <summary>Bounded raw Segment reads; display semantics belong to Dashboard Fact Views.</summary>
public sealed class ExperienceService(AppDbContext db)
{
    public const int PageSize = 500;

    public async Task<ExperiencePage> ReadAsync(
        string ownerId, ResolvedCalendarWindow window, Guid? after, CancellationToken ct = default)
    {
        var query = db.Segments.AsNoTracking().Where(s => s.OwnerId == ownerId &&
            s.StartTime < window.EndExclusive && (s.EndTime > window.Start ||
                (s.StartTime == s.EndTime && s.StartTime == window.Start)));
        // The database row identity is stable across revisions; no offset drift or timestamp rounding.
        if (after.HasValue) query = query.Where(s => s.Id.CompareTo(after.Value) > 0);
        var rows = await query.OrderBy(s => s.Id).Select(s => new
        {
            s.Id, s.StreamId, s.FactId, s.Revision,
            s.Stream.SubjectId, SubjectKind = s.Stream.Subject.Kind,
            SubjectName = s.Stream.Subject.DisplayName ??
                (s.Stream.Subject.Device == null ? null : s.Stream.Subject.Device.DeviceName),
            s.Source, s.AppIdentityId,
            AppId = s.AppIdentity == null ? (long?)null : s.AppIdentity.AppId,
            AppName = s.AppIdentity == null ? null : s.AppIdentity.App.DisplayName,
            AppKey = s.AppIdentity == null ? null : s.AppIdentity.App.Key,
            s.StartTime, s.EndTime, s.Payload,
        }).Take(PageSize + 1).ToListAsync(ct);
        var hasMore = rows.Count > PageSize;
        var items = rows.Take(PageSize).Select(s => new ExperienceSegment(
            s.Id, s.StreamId, s.FactId, s.Revision, s.SubjectId, s.SubjectKind, s.SubjectName,
            s.Source, s.AppId, s.AppIdentityId, s.AppName, s.AppKey, s.StartTime, s.EndTime, s.Payload.RootElement.Clone())).ToList();
        foreach (var row in rows) row.Payload.Dispose();
        return new ExperiencePage(items, hasMore ? items[^1].Id : null);
    }
}
