using System.Text.Json;
using Heartbeat.Core.DTOs.Input;
using Heartbeat.Core.DTOs.Facts;
using Heartbeat.Core.Facts;
using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services;

public sealed partial class FactStore
{
    /// <summary>Drain pre-Fact caches. Native revisions always own an already claimed identity.</summary>
    public async Task ImportSegmentsAsync(long deviceId, List<ActivitySegmentItem> segments, bool validated = false)
    {
        if (!validated) SegmentIngestContract.Validate(segments, _time.GetUtcNow());
        var device = await db.Devices.SingleAsync(d => d.Id == deviceId);
        await Atomic(device.OwnerId, async () =>
        {
            var claims = await NativeLegacyClaims(device, "segment", segments.Select(s => s.Id).ToArray());
            foreach (var item in segments.OrderBy(s => s.StartTime))
            {
                if (claims.TryGetValue(item.Id, out var source))
                {
                    if (source != item.Source) throw new FactIngestException("Legacy Segment Source changed.", true);
                    continue;
                }
                var row = await db.Segments.Include(s => s.Stream).ThenInclude(s => s.Subject).SingleOrDefaultAsync(s => s.Id == item.Id);
                if (row is not null && row.Stream.Origin != "legacy-import")
                    throw new FactIngestException("Native database row identity is not a legacy import identity.", true);
                var stream = await LegacyStream(device, item.Source, "segment");
                if (row is not null && (row.OwnerId != device.OwnerId || !SameSubject(row.Stream.Subject, stream.Subject) ||
                    row.Source != item.Source || String(row.Payload.RootElement, "activityKey") != item.IdentityKey))
                    throw new SegmentIngestContractException(SegmentIngestContractViolation.IdentityConflict,
                        $"Segment {item.Id} conflicts with its existing Subject, Source or activity key.");
                var start = NormalizeTime(item.StartTime)!.Value;
                var end = NormalizeTime(item.EndTime)!.Value;
                if (row is null)
                {
                    var account = item.Source == "vrchat.account" && stream.Subject.Kind == "account"
                        ? await new ServiceAccountService(db).ResolveAsync(device.OwnerId, null, stream.SubjectId, CancellationToken.None) : null;
                    var app = account is not null || string.IsNullOrWhiteSpace(item.AppIdentityKey) ? null :
                        await new AppIdentityService(db).ResolveAsync(item.AppIdentityKey, item.AppDisplayName);
                    row = new Segment
                    {
                        Id = item.Id,
                        OwnerId = device.OwnerId,
                        StreamId = stream.StreamId,
                        Stream = stream,
                        FactId = item.Id,
                        Revision = 1,
                        Source = item.Source,
                        AppIdentityId = app?.Id,
                        StartTime = start,
                        EndTime = end,
                        Payload = JsonDocument.Parse(LegacySegmentPayload(item.IdentityKey, item.Title, item.Attributes?.GetRawText()))
                    };
                    db.Segments.Add(row);
                }
                else
                {
                    var previous = row.Payload.RootElement;
                    var payload = row.Payload;
                    if (end >= row.EndTime)
                    {
                        if (item.Attributes is { } attributes)
                            payload = JsonDocument.Parse(LegacySegmentPayload(item.IdentityKey,
                                item.Title ?? String(previous, "title"), attributes.GetRawText()));
                        else if (item.Title is not null)
                        {
                            var updated = System.Text.Json.Nodes.JsonNode.Parse(previous.GetRawText())!.AsObject();
                            updated["title"] = item.Title;
                            payload = JsonDocument.Parse(updated.ToJsonString());
                        }
                    }
                    var nextStart = start < row.StartTime ? start : row.StartTime;
                    var nextEnd = end > row.EndTime ? end : row.EndTime;
                    if (nextStart != row.StartTime || nextEnd != row.EndTime || !JsonElement.DeepEquals(previous, payload.RootElement))
                    {
                        row.Revision++;
                        row.StartTime = nextStart;
                        row.EndTime = nextEnd;
                        row.Payload = payload;
                    }
                }
                row.Aspect = FactAspectCompatibility.Infer(row.Source, "segment", row.Payload.RootElement);
                var observation = await ResolveLegacyObservation(stream, new FactSnapshot(), row.Payload.RootElement,
                    row.Aspect, CancellationToken.None, row.AppIdentityId);
                row.ObserverId = observation.CollectorId;
                row.FoiId = observation.FoiId;
                row.TargetKind = null;
                row.TargetId = null;
                row.AppIdentityId = observation.AppIdentityId;
                await db.SaveChangesAsync();
                await WriteRelations(row, observation.Relations, CancellationToken.None);
            }
        });
    }

    public async Task ImportInputEventsAsync(string ownerId, string hardwareId, string? deviceName, InputEventUploadRequest request)
    {
        InputEventIngestContract.Validate(request.Events);
        await Atomic(ownerId, async () =>
        {
            var device = await new DeviceService(db).ResolveByHardwareIdAsync(ownerId, hardwareId, deviceName);
            await ImportInputEventsAsync(device.Id, request);
        });
    }

    public async Task ImportInputEventsAsync(long deviceId, InputEventUploadRequest request)
    {
        InputEventIngestContract.Validate(request.Events);
        var device = await db.Devices.SingleAsync(d => d.Id == deviceId);
        await Atomic(device.OwnerId, async () =>
        {
            var claims = await NativeLegacyClaims(device, "event", request.Events.Select(e => e.Id).ToArray());
            var stream = await LegacyStream(device, "system", "event");
            foreach (var item in request.Events.DistinctBy(e => e.Id))
            {
                if (claims.ContainsKey(item.Id)) continue;
                var existing = await db.Events.Include(e => e.Stream).ThenInclude(s => s.Subject).SingleOrDefaultAsync(e => e.Id == item.Id);
                if (existing is not null)
                {
                    if (existing.Stream.Origin != "legacy-import" || existing.OwnerId != device.OwnerId || !SameSubject(existing.Stream.Subject, stream.Subject))
                        throw new FactIngestException("Legacy input identity belongs to another Subject.", true);
                    continue;
                }
                db.Events.Add(new Event
                {
                    Id = item.Id,
                    OwnerId = device.OwnerId,
                    StreamId = stream.StreamId,
                    Stream = stream,
                    FactId = item.Id,
                    Revision = 1,
                    Source = "system",
                    FoiId = stream.Subject.Kind == "machine" ? await ObjectId(device, CancellationToken.None) : null,
                    Aspect = FactAspects.Input,
                    Timestamp = NormalizeTime(item.Timestamp)!.Value,
                    Payload = JsonDocument.Parse(JsonSerializer.Serialize(new { eventType = EventName(item.EventType), codeSet = item.CodeSet, code = item.Code }))
                });
            }
            await db.SaveChangesAsync();
        });
    }

    private async Task<Dictionary<Guid, string>> NativeLegacyClaims(Device device, string kind, Guid[] ids)
    {
        if (ids.Length == 0) return [];
        var parts = device.HardwareId.Split(':');
        var account = parts.Length == 3 && parts[0] == "subject" && parts[1] is "account" or "person" && Guid.TryParse(parts[2], out var parsed)
            ? parsed : (Guid?)null;
        List<IFactRecord> candidates;
        if (kind == "segment")
        {
            // The old projector copies the UUID's first 48 bits exactly. Bound the identity lookup,
            // then verify its complete deterministic ID; never infer identity from activity times.
            var prefixes = ids.Select(id => id.ToString("N")[..12]).Order().ToArray();
            var first = Guid.ParseExact(prefixes[0] + "00000000000000000000", "N");
            var last = Guid.ParseExact(prefixes[^1] + "ffffffffffffffffffff", "N");
            candidates = (await db.Segments.Where(f => f.OwnerId == device.OwnerId && f.Stream.Origin == "native" &&
                (f.Stream.Subject.DeviceId == device.Id || account != null && f.Stream.Subject.SubjectId == account) &&
                f.FactId.CompareTo(first) >= 0 && f.FactId.CompareTo(last) <= 0).ToListAsync()).Cast<IFactRecord>().ToList();
        }
        else candidates = (await db.Events.Where(f => f.OwnerId == device.OwnerId && f.Source == "system" && f.Stream.Origin == "native" &&
            (f.Stream.Subject.DeviceId == device.Id || account != null && f.Stream.Subject.SubjectId == account) &&
            ids.Contains(f.FactId)).ToListAsync()).Cast<IFactRecord>().ToList();
        var requested = ids.ToHashSet();
        var result = new Dictionary<Guid, string>();
        foreach (var fact in candidates)
        {
            var id = kind == "segment" ? FactIngestContract.ProjectedSegmentId(fact.StreamId, fact.FactId) : fact.FactId;
            if (!requested.Contains(id)) continue;
            if (!result.TryAdd(id, fact.Source)) throw new FactIngestException("Legacy identity matches multiple native Streams.", true);
        }
        return result;
    }

    private async Task<FactStream> LegacyStream(Device device, string source, string kind)
    {
        var subjectId = FactIngestContract.LegacyId($"legacy-subject:{device.OwnerId}:{device.Id}");
        var subjectKind = "machine";
        var parts = device.HardwareId.Split(':');
        if (parts.Length == 3 && parts[0] == "subject" && parts[1] is "account" or "person" && Guid.TryParse(parts[2], out var parsed))
        {
            subjectId = parsed;
            subjectKind = parts[1];
        }
        var subject = await db.FactSubjects.FindAsync(device.OwnerId, subjectId);
        if (subject is null)
        {
            subject = new FactSubjectRecord { OwnerId = device.OwnerId, SubjectId = subjectId, Kind = subjectKind, DeviceId = subjectKind == "machine" ? device.Id : null, DisplayName = device.DeviceName };
            db.FactSubjects.Add(subject);
        }
        var streamId = FactIngestContract.LegacyId($"legacy-stream:{device.OwnerId}:{device.Id}:{source}:{kind}");
        var stream = await db.FactStreams.FindAsync(device.OwnerId, streamId);
        if (stream is null)
        {
            stream = new FactStream { OwnerId = device.OwnerId, StreamId = streamId, SubjectId = subjectId, Subject = subject, OutputId = "legacy-import", Source = source, FactKind = kind, Origin = "legacy-import" };
            db.FactStreams.Add(stream);
        }
        return stream;
    }

    internal static string LegacySegmentPayload(string identityKey, string? title, string? attributes)
    {
        var raw = attributes is null ? (JsonElement?)null : JsonDocument.Parse(attributes).RootElement;
        if (raw is { ValueKind: JsonValueKind.Object } full && String(full, "identityKey") == identityKey &&
            (!full.TryGetProperty("title", out var oldTitle) || oldTitle.ValueKind is JsonValueKind.Null or JsonValueKind.String) &&
            String(full, "title") == title && full.TryGetProperty("attributes", out var nested) && nested.ValueKind == JsonValueKind.Object)
            return StoredPayload(full, "segment").RootElement.GetRawText();
        return JsonSerializer.Serialize(new { activityKey = identityKey, title, attributes = raw });
    }
}
