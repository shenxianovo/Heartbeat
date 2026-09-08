using System.Text.Json;
using Heartbeat.Core.DTOs.Input;
using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services;

public sealed partial class FactStore
{
    /// <summary>Compatibility importer for installed clients and durable pre-Fact caches; never rewrites native custody.</summary>
    public async Task ImportSegmentsAsync(long deviceId, List<ActivitySegmentItem> segments, bool validated = false)
    {
        if (!validated) SegmentIngestContract.Validate(segments, _time.GetUtcNow());
        var device = await db.Devices.SingleAsync(d => d.Id == deviceId);
        await Atomic(device.OwnerId, async () =>
        {
            var ids = segments.Select(s => s.Id).ToArray();
            var claimed = await NativeLegacyClaims(device, "segment", ids);
            var pending = segments.Where(s => !claimed.Contains(s.Id)).ToList();
            await ProjectLegacySegmentsAsync(deviceId, pending);
            var pendingIds = pending.Select(s => s.Id).ToArray();
            foreach (var row in await db.ActivitySegments.Where(s => pendingIds.Contains(s.Id)).ToListAsync())
            {
                var stream = await LegacyStream(device, row.Source, "segment");
                var fact = row.FactKey is { } key ? await db.Facts.FindAsync(key) : null;
                var archive = JsonSerializer.Serialize(new { row.Id, row.DeviceId, row.Source, row.IdentityKey, row.AppId, row.AppIdentityId, row.Title, row.StartTime, row.EndTime, Attributes = Parse(row.Attributes) });
                var payload = LegacySegmentPayload(row.IdentityKey, row.Title, row.Attributes);
                if (fact is null)
                {
                    fact = new ObservedFact { Id = Guid.CreateVersion7(), OwnerId = device.OwnerId, StreamId = stream.StreamId, FactId = row.Id, Revision = 0, SchemaRevision = 0,
                        Origin = "legacy-import", LegacyId = row.Id, LegacyDeviceId = device.Id, LegacyKind = "segment", LegacyRecord = archive, Stream = stream };
                    db.Facts.Add(fact);
                }
                fact.Start = row.StartTime;
                fact.End = row.EndTime;
                fact.Payload = payload;
                fact.LegacyRecord ??= archive;
                fact.ContentHash = FactIngestContract.Hash(payload);
                row.FactKey = fact.Id;
                row.Fact = fact;
                row.OwnerId = device.OwnerId;
                row.DeviceId = stream.Subject.DeviceId;
                row.Payload = payload;
                var attributes = JsonDocument.Parse(payload).RootElement.GetProperty("attributes");
                row.Attributes = attributes.ValueKind == JsonValueKind.Null ? null : attributes.GetRawText();
            }
            await db.SaveChangesAsync();
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
            var ids = request.Events.Select(e => e.Id).ToArray();
            var claimed = await NativeLegacyClaims(device, "event", ids);
            var pending = new InputEventUploadRequest { Events = request.Events.Where(e => !claimed.Contains(e.Id)).ToList() };
            await ProjectLegacyInputEventsAsync(deviceId, pending);
            var pendingIds = pending.Events.Select(e => e.Id).ToArray();
            foreach (var row in await db.InputEvents.Where(e => pendingIds.Contains(e.Id)).ToListAsync())
            {
                if (row.DeviceId != deviceId) throw new FactIngestException("Legacy input identity belongs to another Subject.", true);
                if (row.FactKey is not null) continue;
                var stream = await LegacyStream(device, "system", "event");
                var payload = JsonSerializer.Serialize(new { eventType = EventName(row.EventType), codeSet = row.CodeSet, code = row.Code });
                var fact = new ObservedFact { Id = Guid.CreateVersion7(), OwnerId = device.OwnerId, StreamId = stream.StreamId, FactId = row.Id, Revision = 0, SchemaRevision = 0,
                    Origin = "legacy-import", LegacyId = row.Id, LegacyDeviceId = device.Id, LegacyKind = "event", OccurredAt = row.Timestamp, Payload = payload,
                    ContentHash = FactIngestContract.Hash(payload), Stream = stream,
                    LegacyRecord = JsonSerializer.Serialize(new { row.Id, row.DeviceId, row.EventType, row.CodeSet, row.Code, row.Timestamp }) };
                db.Facts.Add(fact);
                row.FactKey = fact.Id;
                row.Fact = fact;
            }
            await db.SaveChangesAsync();
        });
    }

    private async Task ProjectLegacySegmentsAsync(
        long deviceId,
        List<ActivitySegmentItem> segments)
    {
        var appIdentityService = new AppIdentityService(db);
        var ordered = segments.OrderBy(s => s.StartTime).ToList();

        // 快照 upsert：一次批量取回本批涉及的已有行，新插入的行也进字典，
        // 让批内后续同 Id 快照走扩展路径（枢纽攒批场景）。
        var ids = ordered.Select(s => s.Id).Distinct().ToList();
        var rows = await db.ActivitySegments
            .Include(x => x.Fact)
            .Where(x => ids.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id);

        foreach (var group in ordered.GroupBy(s => s.Id))
        {
            long? expectedDeviceId = deviceId;
            var expectedSource = group.First().Source;
            var expectedIdentityKey = group.First().IdentityKey;
            if (rows.TryGetValue(group.Key, out var existing))
            {
                expectedDeviceId = existing.DeviceId ?? existing.Fact?.LegacyDeviceId;
                expectedSource = existing.Source;
                expectedIdentityKey = existing.IdentityKey;
            }

            if (expectedDeviceId == deviceId
                && group.All(item =>
                    string.Equals(expectedSource, item.Source, StringComparison.Ordinal)
                    && string.Equals(expectedIdentityKey, item.IdentityKey, StringComparison.Ordinal)))
            {
                continue;
            }

            throw new SegmentIngestContractException(
                SegmentIngestContractViolation.IdentityConflict,
                $"Segment {group.Key} conflicts with its existing device, source, or identity key.");
        }

        // 只为新事实解析身份；被 identity guard 拒绝的旧 Id 不得制造 provisional App。
        var identityByItem = new Dictionary<ActivitySegmentItem, AppIdentity?>();
        foreach (var item in ordered.Where(x => !rows.ContainsKey(x.Id)))
        {
            var key = ResolveIdentityKey(item);
            if (key == null)
            {
                identityByItem[item] = null;
                continue;
            }

            identityByItem[item] = await appIdentityService.ResolveAsync(key, item.AppDisplayName);
        }

        foreach (var s in ordered)
        {
            if (rows.TryGetValue(s.Id, out var row))
            {
                // 后写胜只对"最新快照"生效：乱序到达的旧快照不得回退 Title/Attributes。
                var isNewest = s.EndTime >= row.EndTime;
                if (s.StartTime < row.StartTime) row.StartTime = s.StartTime;
                if (s.EndTime > row.EndTime) row.EndTime = s.EndTime;
                if (isNewest)
                {
                    if (s.Title != null) row.Title = s.Title;
                    if (s.Attributes.HasValue) row.Attributes = s.Attributes.Value.GetRawText();
                }
            }
            else
            {
                identityByItem.TryGetValue(s, out var appIdentity);
                var entity = new ActivitySegment
                {
                    Id = s.Id,
                    DeviceId = deviceId,
                    Source = s.Source,
                    IdentityKey = s.IdentityKey,
                    AppIdentityId = appIdentity?.Id,
                    // expand 双写：旧消费者仍读 AppId；产品语义的权威路径是 AppIdentity.AppId。
                    AppId = appIdentity?.AppId,
                    Title = s.Title,
                    StartTime = s.StartTime,
                    EndTime = s.EndTime,
                    Attributes = s.Attributes?.GetRawText()
                };
                db.ActivitySegments.Add(entity);
                rows[s.Id] = entity;
            }
        }

        await db.SaveChangesAsync();
    }

    private static string? ResolveIdentityKey(ActivitySegmentItem item)
    {
        return string.IsNullOrWhiteSpace(item.AppIdentityKey) ? null : item.AppIdentityKey;
    }

    private async Task ProjectLegacyInputEventsAsync(long deviceId, InputEventUploadRequest request)
    {
        InputEventIngestContract.Validate(request.Events);

        // 批内按 Id 去重
        var items = request.Events
            .GroupBy(e => e.Id)
            .Select(g => g.First())
            .ToList();

        if (items.Count == 0) return;

        // 过滤掉库中已存在的 Id（幂等：重传整批不会重复插入）
        var ids = items.Select(e => e.Id).ToList();
        var existing = await db.InputEvents
            .Where(e => ids.Contains(e.Id))
            .Select(e => e.Id)
            .ToHashSetAsync();

        var toInsert = items
            .Where(e => !existing.Contains(e.Id))
            .Select(e => new InputEvent
            {
                Id = e.Id,
                DeviceId = deviceId,
                EventType = e.EventType,
                CodeSet = e.CodeSet,
                Code = e.Code,
                Timestamp = e.Timestamp
            });

        db.InputEvents.AddRange(toInsert);
        await db.SaveChangesAsync();
    }

    private async Task<HashSet<Guid>> NativeLegacyClaims(Device device, string kind, Guid[] ids)
    {
        var candidates = await db.Facts.Include(f => f.Stream).ThenInclude(s => s.Subject)
            .Where(f => f.OwnerId == device.OwnerId && f.Origin == "native" && f.LegacyKind == kind && f.LegacyId != null && ids.Contains(f.LegacyId.Value)).ToListAsync();
        var parts = device.HardwareId.Split(':');
        var accountSubject = parts.Length == 3 && parts[0] == "subject" && parts[1] is "account" or "person" && Guid.TryParse(parts[2], out _)
            ? Guid.Parse(parts[2]) : (Guid?)null;
        return candidates.Where(f => f.Stream.Subject.DeviceId == device.Id || accountSubject != null && f.Stream.SubjectId == accountSubject)
            .Select(f => f.LegacyId!.Value).ToHashSet();
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
            stream = new FactStream { OwnerId = device.OwnerId, StreamId = streamId, SubjectId = subjectId, Subject = subject, OutputId = "legacy-import", Source = source, FactKind = kind,
                SchemaId = "heartbeat.legacy." + kind, SchemaMajor = 1, Origin = "legacy-import" };
            db.FactStreams.Add(stream);
        }
        return stream;
    }

    internal static string LegacySegmentPayload(string identityKey, string? title, string? attributes)
    {
        var raw = Parse(attributes);
        // Match the complete old projector envelope, not just a coincidental nested attributes key.
        if (raw is { ValueKind: JsonValueKind.Object } full && String(full, "identityKey") == identityKey &&
            String(full, "title") == title && full.TryGetProperty("attributes", out var nested) && nested.ValueKind == JsonValueKind.Object)
            return full.GetRawText();
        return JsonSerializer.Serialize(new { identityKey, title, attributes = raw });
    }

    private static JsonElement? Parse(string? json) => json is null ? null : JsonDocument.Parse(json).RootElement.Clone();
}
