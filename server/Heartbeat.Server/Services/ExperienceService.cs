using System.Text.Json;
using System.Text.Json.Serialization;
using Heartbeat.Server.Calendar;
using Heartbeat.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services;

public sealed record ExperienceSegment(
    Guid Id, Guid StreamId, Guid FactId, long Revision,
    Guid? ObserverId, string? TargetKind, long? TargetId, string? TargetName, long? DeviceId,
    [property: JsonPropertyName("subjectId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? LegacySubjectId,
    [property: JsonPropertyName("subjectKind"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LegacySubjectKind,
    [property: JsonPropertyName("subjectName"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LegacySubjectName,
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
            s.ObserverId, s.TargetKind, s.TargetId,
            TargetName = s.TargetKind == "device" ? db.Devices.Where(d => d.OwnerId == s.OwnerId && d.Id == s.TargetId)
                .Select(d => d.DeviceName).FirstOrDefault() : s.TargetKind == "application-context" ? db.ApplicationContexts.Where(c => c.OwnerId == s.OwnerId && c.Id == s.TargetId).Select(c => c.Device.DeviceName + " / " + c.App.DisplayName).FirstOrDefault() : null,
            DeviceId = s.TargetKind == "device" ? s.TargetId : s.TargetKind == "application-context" ? db.ApplicationContexts.Where(c => c.OwnerId == s.OwnerId && c.Id == s.TargetId).Select(c => (long?)c.DeviceId).FirstOrDefault() : s.TargetKind == null ? s.Stream.Subject.DeviceId : null,
            LegacySubjectId = s.TargetKind == null ? (Guid?)s.Stream.SubjectId : null,
            LegacySubjectKind = s.TargetKind == null ? s.Stream.Subject.Kind : null,
            LegacySubjectName = s.TargetKind != null ? null : s.Stream.Subject.DisplayName ??
                (s.Stream.Subject.Device == null ? null : s.Stream.Subject.Device.DeviceName),
            s.Source, s.AppIdentityId,
            AppId = s.TargetKind == "application-context" ? db.ApplicationContexts.Where(c => c.OwnerId == s.OwnerId && c.Id == s.TargetId).Select(c => (long?)c.AppId).FirstOrDefault() : s.AppIdentity == null ? (long?)null : s.AppIdentity.AppId,
            AppName = s.TargetKind == "application-context" ? db.ApplicationContexts.Where(c => c.OwnerId == s.OwnerId && c.Id == s.TargetId).Select(c => c.App.DisplayName).FirstOrDefault() : s.AppIdentity == null ? null : s.AppIdentity.App.DisplayName,
            AppKey = s.TargetKind == "application-context" ? db.ApplicationContexts.Where(c => c.OwnerId == s.OwnerId && c.Id == s.TargetId).Select(c => c.App.Key).FirstOrDefault() : s.AppIdentity == null ? null : s.AppIdentity.App.Key,
            s.StartTime, s.EndTime, s.Payload,
        }).Take(PageSize + 1).ToListAsync(ct);
        var hasMore = rows.Count > PageSize;
        var items = rows.Take(PageSize).Select(s => new ExperienceSegment(
            s.Id, s.StreamId, s.FactId, s.Revision, s.ObserverId, s.TargetKind, s.TargetId, s.TargetName, s.DeviceId,
            s.LegacySubjectId, s.LegacySubjectKind, s.LegacySubjectName,
            s.Source, s.AppId, s.AppIdentityId, s.AppName, s.AppKey, s.StartTime, s.EndTime, s.Payload.RootElement.Clone())).ToList();
        foreach (var row in rows) row.Payload.Dispose();
        return new ExperiencePage(items, hasMore ? items[^1].Id : null);
    }
}
